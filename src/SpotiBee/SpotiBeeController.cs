using System;
using System.Collections.Generic;
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
        // Spotify track ID → album URI, for playing a track within its album
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> albumUris =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private SpotifyClient client;
        private PlaybackMonitor monitor;
        private bool fastPolling;

        public SpotiBeeController(string settingsPath)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var version = typeof(SpotiBeeController).Assembly.GetName().Version.ToString(3);
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SpotiBee/{version} (MusicBee plugin)");
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

        /// <summary>Logs in again with the same app, e.g. to grant permissions added in a newer version.</summary>
        public Task ReconnectAsync(CancellationToken ct) => ConnectAsync(Settings.ClientId, ct);

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

        // --- Notices ---------------------------------------------------------

        private readonly List<PanelNotice> notices = new List<PanelNotice>();

        /// <summary>Raised when a notice is shown or cleared, possibly on a background thread.</summary>
        public event Action NoticesChanged;

        /// <summary>The notice the panel shows (the newest one); null when there's none.</summary>
        public PanelNotice CurrentNotice
        {
            get
            {
                lock (notices)
                    return notices.LastOrDefault();
            }
        }

        /// <summary>
        /// Shows a notice on the panel, replacing any with the same key. Different wording counts as
        /// news and goes to the front; the same notice repeated (e.g. an hourly check) keeps its place.
        /// </summary>
        public void ShowNotice(PanelNotice notice)
        {
            lock (notices)
            {
                var index = notices.FindIndex(n => n.Key == notice.Key);
                if (index >= 0 && notices[index].Text == notice.Text)
                {
                    notices[index] = notice;
                }
                else
                {
                    if (index >= 0)
                        notices.RemoveAt(index);
                    notices.Add(notice);
                }
            }
            NoticesChanged?.Invoke();
        }

        public void ClearNotice(string key)
        {
            bool removed;
            lock (notices)
                removed = notices.RemoveAll(n => n.Key == key) > 0;
            if (removed)
                NoticesChanged?.Invoke();
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

        // --- Playback routing ---------------------------------------------

        /// <summary>Set by the plugin once MusicBee is ready; null before that.</summary>
        public PlaybackRouter Router { get; internal set; }

        /// <summary>Set by the plugin once MusicBee is ready; null before that.</summary>
        public LyricsService Lyrics { get; internal set; }

        /// <summary>Like, playlist, album and artist actions. Set by the plugin once MusicBee is ready; null before that.</summary>
        public SpotifyLibrary SpotifyLibrary { get; internal set; }

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

            /// <remarks>
            /// Some Spotify apps (the Windows desktop app, October 2026) accept a lone track but play
            /// nothing or their own last song, yet do play it when asked for it within its album.
            /// Every play is checked; a device that only works the album way is remembered and asked
            /// that way first from then on.
            /// </remarks>
            public async Task PlayTrackAsync(string uri, int positionMs)
            {
                var c = Client;
                var device = DeviceId;
                if (device == null)
                    device = (await owner.PickDeviceAsync(c))?.Id;
                if (device == null)
                    throw new SpotifyApiException(HttpStatusCode.NotFound, "No Spotify devices available", "NO_ACTIVE_DEVICE");

                var albumFirst = owner.Settings.AlbumPlayDevices?.Contains(device) == true;
                PlaybackState state;
                if (albumFirst)
                {
                    state = await TryAlbumAsync(c, uri, device, positionMs);
                    if (Done(state, uri))
                        return;
                    Diagnostics.Log($"Play check: {DescribeDevice(state, device)} didn't play {uri} within its album (playing {Describe(state)}); asking for the track alone");
                }

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
                    device = fallback.Id;
                    await c.PlayUrisAsync(new[] { uri }, device, positionMs);
                }
                state = await WaitForTrackAsync(c, uri);
                if (Done(state, uri))
                {
                    if (albumFirst)
                        RememberAlbumPlay(device, false, state);
                    return;
                }

                if (!albumFirst)
                {
                    Diagnostics.Log($"Play check: asked {DescribeDevice(state, device)} for {uri}, it's playing {Describe(state)}. Asking for it within its album");
                    state = await TryAlbumAsync(c, uri, device, positionMs);
                    if (Done(state, uri))
                    {
                        RememberAlbumPlay(device, true, state);
                        return;
                    }
                }

                // Last resort: wake the device with a transfer, then ask for the track once more
                Diagnostics.Log($"Play check: still playing {Describe(state)}. Waking the device and asking again");
                try { await c.TransferPlaybackAsync(device, play: false); }
                catch (SpotifyApiException ex) { Diagnostics.Log("Play check: waking the device failed: " + ex.Message); }
                await Task.Delay(800);
                await c.PlayUrisAsync(new[] { uri }, device, positionMs);
                state = await WaitForTrackAsync(c, uri);
                if (Done(state, uri))
                    return;

                Diagnostics.Log($"Play check: gave up; {DescribeDevice(state, device)} is playing {Describe(state)}");
                owner.monitor?.RequestRefresh();
                throw new SpotifyApiException(HttpStatusCode.Conflict,
                    $"Spotify on {DescribeDevice(state, device)} played its own last song instead of the one asked for. " +
                    "Try restarting the Spotify app, or pick another device", "WRONG_TRACK");
            }

            private bool Done(PlaybackState state, string uri)
            {
                if (!IsTrack(state?.Item, uri))
                    return false;
                owner.monitor?.RequestRefresh();
                return true;
            }

            /// <summary>Plays the track within its album and waits to see it playing. Returns the last state seen.</summary>
            private async Task<PlaybackState> TryAlbumAsync(SpotifyClient c, string uri, string device, int positionMs)
            {
                var id = uri.Substring(uri.LastIndexOf(':') + 1);
                if (!owner.albumUris.TryGetValue(id, out var album))
                {
                    album = (await c.GetTrackAsync(id))?.Album?.Uri;
                    owner.albumUris[id] = album;
                }
                if (album == null)
                    return null;
                try
                {
                    await c.PlayInContextAsync(album, uri, device, positionMs);
                }
                catch (SpotifyApiException)
                {
                    // e.g. the album isn't available here, or the device went away; asking for the
                    // lone track may still work, and finds another device if needed
                    return null;
                }
                return await WaitForTrackAsync(c, uri);
            }

            private void RememberAlbumPlay(string device, bool albumOnly, PlaybackState state)
            {
                var list = owner.Settings.AlbumPlayDevices ??= new List<string>();
                if (albumOnly == list.Contains(device))
                    return;
                if (albumOnly)
                    list.Add(device);
                else
                    list.Remove(device);
                Diagnostics.Log(albumOnly
                    ? $"Play check: {DescribeDevice(state, device)} only plays tracks asked for within their album; asking it that way from now on"
                    : $"Play check: {DescribeDevice(state, device)} plays lone tracks again; asking it that way from now on");
                owner.SaveSettings();
            }

            /// <summary>Polls Spotify for up to a few seconds until it reports this track; returns the last state seen.</summary>
            private async Task<PlaybackState> WaitForTrackAsync(SpotifyClient c, string uri)
            {
                PlaybackState state = null;
                for (var i = 0; i < 6; i++)
                {
                    await Task.Delay(500);
                    try { state = await c.GetPlaybackStateAsync(); }
                    catch (SpotifyApiException) { continue; }
                    if (IsTrack(state?.Item, uri))
                        break;
                }
                return state;
            }

            // Spotify may play a relinked copy (another release of the same recording), so the
            // title and length count as a match too
            private bool IsTrack(Track item, string uri)
            {
                if (item == null)
                    return false;
                if (item.Uri == uri)
                    return true;
                var record = owner.Store.GetTrack(uri.Substring(uri.LastIndexOf(':') + 1));
                return record != null && string.Equals(item.Name, record.Title, StringComparison.OrdinalIgnoreCase) &&
                       Math.Abs(item.DurationMs - record.DurationMs) < 2000;
            }

            private static string Describe(PlaybackState state) =>
                state?.Item == null ? "nothing" : $"\"{state.Item.Name}\" ({(state.IsPlaying ? "playing" : "paused")})";

            private static string DescribeDevice(PlaybackState state, string deviceId) =>
                state?.Device?.Name ?? "device " + deviceId;

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
                case SpotifyApiException api when api.IsRestrictionViolated:
                    StatusMessage?.Invoke(ExplainRestriction());
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

        /// <summary>
        /// Why Spotify answered "Restriction violated", as far as the last playback state says:
        /// the device doesn't accept remote control, or what's playing doesn't allow that action.
        /// </summary>
        private string ExplainRestriction()
        {
            var state = LastSnapshot?.State;
            var device = state?.Device?.Name ?? "the active device";
            if (state?.Device?.IsRestricted == true)
                return $"Spotify on {device} isn't accepting remote control right now. Try restarting the Spotify app there, or pick another device.";
            var blocked = state?.Actions?.Disallows?.ToString();
            if (!string.IsNullOrEmpty(blocked))
                return $"Spotify on {device} doesn't allow {blocked} for what's playing right now.";
            return $"Spotify on {device} refused that (\"Restriction violated\"). If it keeps happening, restart the Spotify app or pick another device.";
        }

        /// <summary>The connected account, loading it if that didn't work at startup. Null if it can't be loaded.</summary>
        public async Task<UserProfile> GetUserAsync()
        {
            if (User != null || client == null)
                return User;
            await LoadUserAsync(report: false);
            return User;
        }

        private async Task LoadUserAsync(bool report = true)
        {
            var c = client;
            if (c == null)
                return;
            try
            {
                User = await c.GetCurrentUserAsync();
                if (User == null)
                    Diagnostics.Log("Loading your Spotify account: Spotify sent nothing back");
                ConnectionChanged?.Invoke();
                if (User?.IsPremium == false)
                    StatusMessage?.Invoke("This account isn't Premium. Spotify only allows playback control for Premium accounts.");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Loading your Spotify account", ex);
                if (report)
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
