using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee
{
    /// <summary>
    /// Owns the Spotify connection and exposes playback commands to the UI and MusicBee.
    /// Events may fire on background threads; UI subscribers must marshal to their own thread.
    /// </summary>
    public sealed class SpotiBeeController : IDisposable
    {
        private readonly HttpClient http;
        private SpotifyClient client;
        private PlaybackMonitor monitor;

        public SpotiBeeController(string settingsPath)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotiBee/0.1 (MusicBee plugin)");
            Settings = PluginSettings.Load(settingsPath);
            Store = new TrackStore(Path.Combine(Path.GetDirectoryName(settingsPath), "library.json"));
        }

        public PluginSettings Settings { get; }
        public TrackStore Store { get; }

        /// <summary>Null when not connected.</summary>
        internal SpotifyClient Client => client;
        internal HttpClient Http => http;
        public UserProfile User { get; private set; }
        public PlaybackSnapshot LastSnapshot { get; private set; }
        public bool IsConnected => client != null;

        public event Action ConnectionChanged;
        public event Action<PlaybackSnapshot> PlaybackUpdated;
        public event Action<Track> TrackChanged;
        public event Action<string> StatusMessage;

        /// <summary>Resume a saved session, if there is one.</summary>
        public void Start()
        {
            var refreshToken = Settings.RefreshToken;
            if (string.IsNullOrEmpty(Settings.ClientId) || string.IsNullOrEmpty(refreshToken))
                return;
            AttachClient(new SpotifyClient(http, Settings.ClientId, refreshToken));
            _ = LoadUserAsync();
        }

        /// <summary>Runs the browser login. Throws SpotifyAuthException or OperationCanceledException on failure.</summary>
        public async Task ConnectAsync(string clientId, CancellationToken ct)
        {
            clientId = clientId?.Trim();
            if (string.IsNullOrEmpty(clientId))
                throw new SpotifyAuthException("Enter your Spotify app's Client ID first.");

            var token = await SpotifyAuth.AuthorizeAsync(http, clientId, ct);

            DetachClient();
            Settings.ClientId = clientId;
            Settings.RefreshToken = token.RefreshToken;
            SaveSettings();
            AttachClient(new SpotifyClient(http, clientId, token));
            await LoadUserAsync();
        }

        public void Disconnect()
        {
            DetachClient();
            Settings.RefreshToken = null;
            SaveSettings();
            User = null;
            LastSnapshot = null;
            ConnectionChanged?.Invoke();
        }

        // --- Commands ------------------------------------------------------

        public Task PlayPauseAsync() => RunAsync(async c =>
        {
            if (LastSnapshot?.IsPlaying == true)
                await c.PauseAsync();
            else
                await ResumeAsync(c);
        });

        public Task PlayAsync() => RunAsync(ResumeAsync);
        public Task PauseAsync() => RunAsync(c => c.PauseAsync());
        public Task NextAsync() => RunAsync(c => c.NextAsync());
        public Task PreviousAsync() => RunAsync(c => c.PreviousAsync());
        public Task SeekAsync(TimeSpan position) => RunAsync(c => c.SeekAsync((int)position.TotalMilliseconds));
        public Task SetVolumeAsync(int percent) => RunAsync(c => c.SetVolumeAsync(percent));

        public Task ToggleShuffleAsync() => RunAsync(c => c.SetShuffleAsync(!(LastSnapshot?.State?.ShuffleState ?? false)));

        /// <summary>off → context → track → off, matching the Spotify app's repeat button.</summary>
        public Task CycleRepeatAsync() => RunAsync(c =>
        {
            var next = (LastSnapshot?.State?.RepeatState) switch
            {
                "off" => "context",
                "context" => "track",
                _ => "off",
            };
            return c.SetRepeatAsync(next);
        });

        public Task TransferToAsync(string deviceId, bool? play = null) => RunAsync(async c =>
        {
            Settings.PreferredDeviceId = deviceId;
            SaveSettings();
            await c.TransferPlaybackAsync(deviceId, play ?? LastSnapshot?.IsPlaying ?? false);
        });

        public async Task<Device[]> GetDevicesAsync()
        {
            var c = client;
            if (c == null)
                return new Device[0];
            try
            {
                return await c.GetDevicesAsync();
            }
            catch (Exception ex)
            {
                Report(ex);
                return new Device[0];
            }
        }

        public void OpenSpotifyApp()
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch
            {
                // Desktop app not installed; the web player works as a Connect device too
                Process.Start(new ProcessStartInfo("https://open.spotify.com") { UseShellExecute = true });
            }
        }

        // --- Internals -----------------------------------------------------

        private async Task ResumeAsync(SpotifyClient c)
        {
            if (LastSnapshot?.State?.Device != null)
            {
                try
                {
                    await c.PlayAsync();
                    return;
                }
                catch (SpotifyApiException ex) when (ex.IsNoActiveDevice)
                {
                    // The device went away since the last poll; fall through and wake one
                }
            }

            var device = await PickDeviceAsync(c);
            if (device == null)
                throw new SpotifyApiException(HttpStatusCode.NotFound, "No Spotify devices available", "NO_ACTIVE_DEVICE");
            await c.TransferPlaybackAsync(device.Id, play: true);
        }

        private async Task<Device> PickDeviceAsync(SpotifyClient c)
        {
            var devices = (await c.GetDevicesAsync()).Where(d => !d.IsRestricted && d.Id != null).ToArray();
            return devices.FirstOrDefault(d => d.Id == Settings.PreferredDeviceId)
                ?? devices.FirstOrDefault(d => d.IsActive)
                ?? devices.FirstOrDefault(d => d.Type == "Computer")
                ?? devices.FirstOrDefault();
        }

        private async Task RunAsync(Func<SpotifyClient, Task> action)
        {
            var c = client;
            if (c == null)
            {
                StatusMessage?.Invoke("Not connected to Spotify.");
                return;
            }
            try
            {
                await action(c);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                monitor?.RequestRefresh();
            }
        }

        private void Report(Exception ex)
        {
            switch (ex)
            {
                case SpotifyAuthException auth when auth.IsInvalidGrant:
                    Disconnect();
                    StatusMessage?.Invoke("Spotify session expired. Please reconnect.");
                    break;
                case SpotifyAuthException auth:
                    StatusMessage?.Invoke(auth.Message);
                    break;
                case SpotifyApiException api:
                    StatusMessage?.Invoke(api.FriendlyMessage);
                    break;
                case HttpRequestException _:
                case TaskCanceledException _:
                    StatusMessage?.Invoke("Couldn't reach Spotify. Check your connection.");
                    break;
                default:
                    StatusMessage?.Invoke("SpotiBee error: " + ex.Message);
                    break;
            }
        }

        private async Task LoadUserAsync()
        {
            var c = client;
            if (c == null)
                return;
            try
            {
                User = await c.GetCurrentUserAsync();
                ConnectionChanged?.Invoke();
                if (User?.IsPremium == false)
                    StatusMessage?.Invoke("This account isn't Premium. Spotify only allows playback control for Premium accounts.");
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        private void AttachClient(SpotifyClient newClient)
        {
            client = newClient;
            client.RefreshTokenChanged += token =>
            {
                Settings.RefreshToken = token;
                SaveSettings();
            };

            monitor = new PlaybackMonitor(client);
            monitor.StateUpdated += snapshot =>
            {
                LastSnapshot = snapshot;
                PlaybackUpdated?.Invoke(snapshot);
            };
            monitor.TrackChanged += track => TrackChanged?.Invoke(track);
            monitor.PollFailed += Report;
            monitor.Start();

            ConnectionChanged?.Invoke();
        }

        private void DetachClient()
        {
            monitor?.Dispose();
            monitor = null;
            client?.Dispose();
            client = null;
        }

        private void SaveSettings()
        {
            try
            {
                Settings.Save();
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke("Couldn't save SpotiBee settings: " + ex.Message);
            }
        }

        public void Dispose()
        {
            DetachClient();
            http.Dispose();
        }
    }
}
