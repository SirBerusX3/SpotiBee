using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Library;
using SpotiBee.Playback;
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
        private bool fastPolling;

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
        public event Action<Exception> PollFailed;

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

        // While MusicBee's queue is driving Spotify, transport acts on MusicBee so its queue stays in charge

        public Task PlayPauseAsync()
        {
            if (Router?.IsDriving == true)
                return Driving(Router.PlayPause);
            return RunAsync(async c =>
            {
                if (LastSnapshot?.IsPlaying == true)
                    await c.PauseAsync();
                else
                    await ResumeAsync(c);
            });
        }

        public Task NextAsync() => Router?.IsDriving == true ? Driving(Router.Next) : RunAsync(c => c.NextAsync());
        public Task PreviousAsync() => Router?.IsDriving == true ? Driving(Router.Previous) : RunAsync(c => c.PreviousAsync());

        public Task SeekAsync(TimeSpan position) => Router?.IsDriving == true
            ? Driving(() => Router.Seek((int)position.TotalMilliseconds))
            : RunAsync(c => c.SeekAsync((int)position.TotalMilliseconds));
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

        /// <summary>Shows a message in the panel's status line.</summary>
        public void ShowStatus(string message) => StatusMessage?.Invoke(message);

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

        // --- Playback routing ---------------------------------------------

        /// <summary>Set by the plugin once MusicBee is ready; null before that.</summary>
        public PlaybackRouter Router { get; internal set; }

        private static Task Driving(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        /// <summary>Spotify commands for the router. These throw instead of reporting, so it can fail over.</summary>
        internal ISpotifyPlayback RouterPlayback => new RouterSpotifyPlayback(this);

        private sealed class RouterSpotifyPlayback : ISpotifyPlayback
        {
            private readonly SpotiBeeController owner;

            public RouterSpotifyPlayback(SpotiBeeController owner)
            {
                this.owner = owner;
            }

            private SpotifyClient Client =>
                owner.client ?? throw new SpotifyAuthException("Not connected to Spotify.");

            private string DeviceId => owner.LastSnapshot?.State?.Device?.Id;

            public bool IsConnected => owner.client != null;

            public async Task PlayTrackAsync(string uri, int positionMs)
            {
                var c = Client;
                var device = DeviceId;
                if (device == null)
                    device = (await owner.PickDeviceAsync(c))?.Id;
                if (device == null)
                    throw new SpotifyApiException(HttpStatusCode.NotFound, "No Spotify devices available", "NO_ACTIVE_DEVICE");
                try
                {
                    await c.PlayUrisAsync(new[] { uri }, device, positionMs);
                }
                catch (SpotifyApiException ex) when (ex.IsNoActiveDevice)
                {
                    // The last-seen device went away; try whichever device is available now
                    var fallback = await owner.PickDeviceAsync(c);
                    if (fallback == null || fallback.Id == device)
                        throw;
                    await c.PlayUrisAsync(new[] { uri }, fallback.Id, positionMs);
                }
                owner.monitor?.RequestRefresh();
            }

            public async Task PauseAsync()
            {
                // Pausing something already paused is a 403 from Spotify; that's fine here
                try { await Client.PauseAsync(); }
                catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { }
                owner.monitor?.RequestRefresh();
            }

            public async Task ResumeAsync()
            {
                try { await Client.PlayAsync(); }
                catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden && ex.Reason != "PREMIUM_REQUIRED") { }
                owner.monitor?.RequestRefresh();
            }

            public async Task SeekAsync(int positionMs)
            {
                await Client.SeekAsync(positionMs);
                owner.monitor?.RequestRefresh();
            }

            public Task SetVolumeAsync(int percent) => Client.SetVolumeAsync(percent);

            public async Task EnsureRepeatOffAsync()
            {
                // With a single track queued, Spotify's repeat would loop it instead of letting MusicBee advance
                var repeat = owner.LastSnapshot?.State?.RepeatState;
                if (repeat != null && repeat != "off")
                    await Client.SetRepeatAsync("off");
            }

            public void SetFastPolling(bool fast)
            {
                owner.fastPolling = fast;
                if (owner.monitor != null)
                    owner.monitor.FastPolling = fast;
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

            monitor = new PlaybackMonitor(client) { FastPolling = fastPolling };
            monitor.StateUpdated += snapshot =>
            {
                LastSnapshot = snapshot;
                PlaybackUpdated?.Invoke(snapshot);
            };
            monitor.TrackChanged += track => TrackChanged?.Invoke(track);
            monitor.PollFailed += ex =>
            {
                PollFailed?.Invoke(ex);
                Report(ex);
            };
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
