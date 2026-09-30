using System;
using System.Threading.Tasks;
using SpotiBee.Library;
using SpotiBee.Spotify;
using static MusicBeePlugin.Plugin;

namespace SpotiBee.Playback
{
    public enum PlaybackMode
    {
        /// <summary>Local files play in MusicBee; placeholders play through Spotify.</summary>
        LocalFirst = 0,
        /// <summary>Everything Spotify has plays through Spotify; local files are the fallback.</summary>
        SpotifyFirst = 1,
        /// <summary>Never use Spotify; placeholders are skipped.</summary>
        LocalOnly = 2,
    }

    public enum PlaybackRoute
    {
        /// <summary>SpotiBee isn't involved in what's playing.</summary>
        MusicBee,
        /// <summary>MusicBee is playing a (muted or silent) file as a clock while Spotify makes the sound.</summary>
        Spotify,
        /// <summary>The user started something else in Spotify, so MusicBee paused and let go.</summary>
        HandedOver,
    }

    /// <summary>What the router needs from MusicBee's player.</summary>
    public interface IMusicBeePlayer
    {
        string NowPlayingFile { get; }
        PlayState PlayState { get; }
        int PositionMs { get; set; }
        int DurationMs { get; }
        float Volume { get; set; }
        bool Muted { get; set; }
        bool ScrobbleEnabled { get; set; }
        void PlayPause();
        void Next();
        void Previous();
    }

    /// <summary>What the router needs from Spotify. Unlike the UI commands, these throw on failure.</summary>
    public interface ISpotifyPlayback
    {
        bool IsConnected { get; }
        Task PlayTrackAsync(string uri, int positionMs);
        Task PauseAsync();
        Task ResumeAsync();
        Task SeekAsync(int positionMs);
        Task SetVolumeAsync(int percent);
        Task EnsureRepeatOffAsync();
        void SetFastPolling(bool fast);
    }

    /// <summary>
    /// Decides, track by track, whether MusicBee or Spotify makes the sound, and keeps the two
    /// in step while Spotify is playing: play/pause, seeking, drift, volume, scrobbling and fallback.
    /// All members must be called on MusicBee's UI thread.
    /// </summary>
    public sealed class PlaybackRouter
    {
        private static readonly TimeSpan CommandGrace = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan MirrorGrace = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan FailureTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan UnavailableBackoff = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ScrobbleRestoreDelay = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan VolumeDebounce = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan VolumeEchoWindow = TimeSpan.FromSeconds(3);
        private const int SeekThresholdMs = 1500;
        private const int DriftThresholdMs = 1500;
        private const int NearEndMs = 3000;
        private const int MaxConsecutiveSkips = 25;

        private readonly IMusicBeePlayer mb;
        private readonly ISpotifyPlayback spotify;
        private readonly TrackStore store;
        private readonly Func<string, bool> isPlaceholderPath;
        private readonly PluginSettings settings;
        private readonly Func<DateTime> now;

        private string currentFile;
        private TrackRecord currentRecord;
        private bool currentIsPlaceholder;
        private int generation;
        private bool startPending;

        private DateTime graceUntil;
        private int lastMbPosition;
        private DateTime lastMbPositionAt;
        private DateTime ignoreSeeksUntil;

        private DateTime? failingSince;
        private int lastSpotifyProgress = -1;
        private DateTime lastSpotifyProgressChangeAt;
        private bool lastSpotifyPlaying;
        private int lastSpotifyRemainingMs = int.MaxValue;
        private DateTime spotifyUnavailableUntil;

        private int? pendingVolume;
        private DateTime pendingVolumeAt;
        private DateTime lastVolumeSentAt;
        private float? volumeWeSet;
        private string volumeWarnedDevice;
        private string currentDeviceId;

        private PlayState? expectedMbState;
        private DateTime? scrobbleRestoreAt;
        private int consecutiveSkips;
        private DateTime lastSkipAt;

        public PlaybackRouter(IMusicBeePlayer mb, ISpotifyPlayback spotify, TrackStore store,
            Func<string, bool> isPlaceholderPath, PluginSettings settings, Func<DateTime> clock = null)
        {
            this.mb = mb;
            this.spotify = spotify;
            this.store = store;
            this.isPlaceholderPath = isPlaceholderPath;
            this.settings = settings;
            now = clock ?? (() => DateTime.UtcNow);
        }

        public event Action<string> StatusMessage;
        public event Action StateChanged;

        public PlaybackRoute Route { get; private set; } = PlaybackRoute.MusicBee;

        /// <summary>True while MusicBee's queue is driving Spotify, so transport buttons should act on MusicBee.</summary>
        public bool IsDriving => Route == PlaybackRoute.Spotify;

        public bool CurrentIsPlaceholder => currentIsPlaceholder;

        public PlaybackMode Mode
        {
            get => (PlaybackMode)settings.PlaybackMode;
            set
            {
                if (Mode == value)
                    return;
                settings.PlaybackMode = (int)value;
                SaveSettings();
                Status("Playback mode: " + Describe(value));
                Reroute();
                StateChanged?.Invoke();
            }
        }

        public void CycleMode() => Mode = Mode switch
        {
            PlaybackMode.LocalFirst => PlaybackMode.SpotifyFirst,
            PlaybackMode.SpotifyFirst => PlaybackMode.LocalOnly,
            _ => PlaybackMode.LocalFirst,
        };

        public static string Describe(PlaybackMode mode) => mode switch
        {
            PlaybackMode.SpotifyFirst => "Spotify first",
            PlaybackMode.LocalOnly => "Local only",
            _ => "Local first",
        };

        // --- Transport while driving -------------------------------------

        public void PlayPause() => mb.PlayPause();
        public void Next() => mb.Next();
        public void Previous() => mb.Previous();

        public void Seek(int positionMs)
        {
            ignoreSeeksUntil = now() + TimeSpan.FromSeconds(1);
            mb.PositionMs = positionMs;
            RememberMbPosition(positionMs);
            graceUntil = now() + CommandGrace;
            _ = Try(() => spotify.SeekAsync(positionMs), generation);
        }

        // --- MusicBee events ---------------------------------------------

        /// <summary>Call once at startup: undoes a mute or scrobble change left behind if MusicBee closed unexpectedly.</summary>
        public void RecoverFromPreviousSession()
        {
            if (settings.MuteAppliedBySpotiBee)
            {
                mb.Muted = false;
                settings.MuteAppliedBySpotiBee = false;
            }
            if (settings.ScrobbleSuppressedBySpotiBee)
            {
                mb.ScrobbleEnabled = true;
                settings.ScrobbleSuppressedBySpotiBee = false;
            }
            SaveSettings();
        }

        public void OnTrackChanged(string file)
        {
            var previousRoute = Route;
            generation++;
            currentFile = file;
            expectedMbState = null;
            // A new track starts near 0; don't mistake that for a seek
            RememberMbPosition(mb.PositionMs);
            ignoreSeeksUntil = now() + TimeSpan.FromSeconds(1);

            var decision = Decide(file, out currentRecord, out currentIsPlaceholder);
            if (decision != Decision.Skip)
                consecutiveSkips = 0;

            switch (decision)
            {
                case Decision.MusicBee:
                    EnterMusicBee(pauseSpotify: previousRoute == PlaybackRoute.Spotify);
                    break;
                case Decision.Skip:
                    EnterMusicBee(pauseSpotify: previousRoute == PlaybackRoute.Spotify);
                    SkipCurrent(Mode == PlaybackMode.LocalOnly
                        ? "Local only mode: skipping Spotify track"
                        : "Spotify isn't available: skipping Spotify track");
                    break;
                case Decision.Spotify:
                    EnterSpotify(mb.PositionMs);
                    break;
            }
            StateChanged?.Invoke();
        }

        public void OnPlayStateChanged()
        {
            var state = mb.PlayState;
            if (expectedMbState.HasValue)
            {
                var expected = expectedMbState.Value;
                expectedMbState = null;
                if (state == expected)
                    return;   // the echo of our own mirroring of a Spotify-side play/pause
            }

            if (Route == PlaybackRoute.Spotify)
            {
                switch (state)
                {
                    case PlayState.Playing:
                        if (startPending)
                            StartSpotify(mb.PositionMs);
                        else
                        {
                            graceUntil = now() + MirrorGrace;
                            _ = Try(spotify.ResumeAsync, generation, failover: true);
                        }
                        break;
                    case PlayState.Paused:
                        if (!startPending)
                        {
                            graceUntil = now() + MirrorGrace;
                            _ = Try(spotify.PauseAsync, generation);
                        }
                        break;
                    case PlayState.Stopped:
                        EnterMusicBee(pauseSpotify: true);
                        StateChanged?.Invoke();
                        break;
                }
            }
            else if (Route == PlaybackRoute.HandedOver && state == PlayState.Playing)
            {
                // Pressing play in MusicBee takes control back from the Spotify app
                Status("Taking back control of Spotify from MusicBee.");
                Route = PlaybackRoute.MusicBee;
                Reroute();
                StateChanged?.Invoke();
            }
        }

        public void OnVolumeChanged()
        {
            if (volumeWeSet.HasValue && Math.Abs(mb.Volume - volumeWeSet.Value) < 0.005f)
                return;   // our own update echoing back
            volumeWeSet = null;
            if (Route != PlaybackRoute.Spotify)
                return;
            pendingVolume = ToPercent(mb.Volume);
            pendingVolumeAt = now() + VolumeDebounce;
        }

        public void OnMuteChanged()
        {
            // Unmuting a local file that's playing through Spotify means "let me hear it here"
            if (Route == PlaybackRoute.Spotify && settings.MuteAppliedBySpotiBee && !mb.Muted)
            {
                settings.MuteAppliedBySpotiBee = false;
                SaveSettings();
                Status("Unmuted: playing your local file instead of Spotify.");
                EnterMusicBee(pauseSpotify: true);
                StateChanged?.Invoke();
            }
        }

        public void OnScrobbleChanged()
        {
            // The user switched scrobbling back on themselves; don't flip it again later
            if (settings.ScrobbleSuppressedBySpotiBee && mb.ScrobbleEnabled)
            {
                settings.ScrobbleSuppressedBySpotiBee = false;
                scrobbleRestoreAt = null;
                SaveSettings();
            }
        }

        /// <summary>Call every ~500 ms on the UI thread.</summary>
        public void Tick()
        {
            var t = now();

            if (scrobbleRestoreAt.HasValue && t >= scrobbleRestoreAt.Value)
            {
                scrobbleRestoreAt = null;
                RestoreScrobbleNow();
            }

            if (pendingVolume.HasValue && t >= pendingVolumeAt)
            {
                var percent = pendingVolume.Value;
                pendingVolume = null;
                SendVolume(percent);
            }

            if (Route != PlaybackRoute.Spotify || mb.PlayState != PlayState.Playing)
            {
                RememberMbPosition(mb.PositionMs);
                return;
            }

            // Seeking in MusicBee has no notification; spot it as a jump in position
            var position = mb.PositionMs;
            var expected = lastMbPosition + (int)(t - lastMbPositionAt).TotalMilliseconds;
            if (t >= ignoreSeeksUntil && Math.Abs(position - expected) > SeekThresholdMs && !startPending)
            {
                graceUntil = t + CommandGrace;
                _ = Try(() => spotify.SeekAsync(position), generation);
            }
            RememberMbPosition(position);

            if (InGrace)
                return;
            if (failingSince.HasValue && t - failingSince.Value > FailureTimeout)
                FailOver("Lost connection to Spotify");
            else if (lastSpotifyPlaying && lastSpotifyProgress >= 0 && t - lastSpotifyProgressChangeAt > FailureTimeout)
                FailOver("Spotify playback stalled");
        }

        // --- Spotify events ----------------------------------------------

        public void OnSpotifyPollFailed()
        {
            if (Route == PlaybackRoute.Spotify && failingSince == null)
                failingSince = now();
        }

        public void OnSnapshot(PlaybackSnapshot snapshot)
        {
            if (Route != PlaybackRoute.Spotify)
                return;
            failingSince = null;
            var t = now();
            var state = snapshot?.State;
            currentDeviceId = state?.Device?.Id;

            if (!IsExpectedItem(state?.Item))
            {
                if (InGrace)
                    return;
                if (IsNearEnd())
                {
                    // Spotify moved on by itself (autoplay/queue) as the track finished
                    if (mb.DurationMs - mb.PositionMs > 1000)
                        mb.Next();
                    return;
                }
                HandOver();
                return;
            }

            var progress = state.ProgressMs ?? 0;
            if (progress != lastSpotifyProgress)
            {
                lastSpotifyProgress = progress;
                lastSpotifyProgressChangeAt = t;
            }
            lastSpotifyPlaying = state.IsPlaying;
            lastSpotifyRemainingMs = (int)(snapshot.Duration - snapshot.Position).TotalMilliseconds;

            if (InGrace)
                return;

            // Play/pause pressed in the Spotify app (or on a speaker): mirror it into MusicBee
            if (!state.IsPlaying && mb.PlayState == PlayState.Playing)
            {
                if (IsNearEnd())
                    return;   // Spotify finished the track a moment early; MusicBee is about to advance
                MirrorToMusicBee(PlayState.Paused);
                return;
            }
            if (state.IsPlaying && mb.PlayState == PlayState.Paused)
            {
                MirrorToMusicBee(PlayState.Playing);
                return;
            }

            // Spotify's position is the truth, since it's what's audible; pull MusicBee into line
            if (mb.PlayState == PlayState.Playing && !IsNearEnd())
            {
                var spotifyPosition = (int)snapshot.Position.TotalMilliseconds;
                if (Math.Abs(spotifyPosition - mb.PositionMs) > DriftThresholdMs)
                {
                    ignoreSeeksUntil = t + TimeSpan.FromSeconds(1);
                    mb.PositionMs = spotifyPosition;
                    RememberMbPosition(spotifyPosition);
                }
            }

            // Volume changed in the Spotify app: move MusicBee's slider to match
            var volume = state.Device?.VolumePercent;
            if (volume.HasValue && !pendingVolume.HasValue && t - lastVolumeSentAt > VolumeEchoWindow &&
                Math.Abs(volume.Value - ToPercent(mb.Volume)) > 2)
            {
                volumeWeSet = volume.Value / 100f;
                mb.Volume = volumeWeSet.Value;
            }
        }

        // --- Routing -----------------------------------------------------

        private enum Decision { MusicBee, Spotify, Skip }

        private Decision Decide(string file, out TrackRecord record, out bool isPlaceholder)
        {
            record = store.FindByPath(file);
            isPlaceholder = !string.IsNullOrEmpty(file) &&
                            (isPlaceholderPath(file) || string.Equals(file, record?.PlaceholderPath, StringComparison.OrdinalIgnoreCase));
            if (record == null)
                return isPlaceholder ? Decision.Skip : Decision.MusicBee;

            var spotifyOk = spotify.IsConnected && now() >= spotifyUnavailableUntil && !string.IsNullOrEmpty(record.Uri);
            switch (Mode)
            {
                case PlaybackMode.LocalOnly:
                    return isPlaceholder ? Decision.Skip : Decision.MusicBee;
                case PlaybackMode.SpotifyFirst:
                    if (spotifyOk)
                        return Decision.Spotify;
                    return isPlaceholder ? Decision.Skip : Decision.MusicBee;
                default:
                    if (!isPlaceholder)
                        return Decision.MusicBee;
                    return spotifyOk ? Decision.Spotify : Decision.Skip;
            }
        }

        /// <summary>Re-applies the routing decision to the current track without restarting it (e.g. after a mode change).</summary>
        private void Reroute()
        {
            if (currentFile == null || mb.PlayState == PlayState.Stopped || mb.PlayState == PlayState.Undefined)
                return;
            var decision = Decide(currentFile, out currentRecord, out currentIsPlaceholder);
            switch (decision)
            {
                case Decision.Spotify when Route != PlaybackRoute.Spotify:
                    generation++;
                    EnterSpotify(mb.PositionMs);
                    break;
                case Decision.MusicBee when Route == PlaybackRoute.Spotify:
                    EnterMusicBee(pauseSpotify: true);
                    break;
                case Decision.Skip:
                    EnterMusicBee(pauseSpotify: Route == PlaybackRoute.Spotify);
                    SkipCurrent("Skipping Spotify track");
                    break;
            }
        }

        private void EnterSpotify(int positionMs)
        {
            Route = PlaybackRoute.Spotify;
            spotify.SetFastPolling(true);
            failingSince = null;
            lastSpotifyProgress = -1;
            lastSpotifyPlaying = false;
            lastSpotifyRemainingMs = int.MaxValue;
            lastSpotifyProgressChangeAt = now();

            // Placeholders are already silent; only a real local file needs hiding
            if (!currentIsPlaceholder && !mb.Muted)
            {
                mb.Muted = true;
                settings.MuteAppliedBySpotiBee = true;
                SaveSettings();
            }
            SuppressScrobble();

            // MusicBee's slider is the master volume while it's driving Spotify
            pendingVolume = ToPercent(mb.Volume);
            pendingVolumeAt = now();

            if (mb.PlayState == PlayState.Playing)
                StartSpotify(positionMs);
            else
                startPending = true;   // start when MusicBee actually starts playing
        }

        private void StartSpotify(int positionMs)
        {
            startPending = false;
            graceUntil = now() + CommandGrace;
            var gen = generation;
            var uri = currentRecord.Uri;
            _ = Try(async () =>
            {
                await spotify.PlayTrackAsync(uri, positionMs);
                if (gen != generation)
                    return;
                if (mb.PlayState != PlayState.Playing)
                    await spotify.PauseAsync();
                await spotify.EnsureRepeatOffAsync();
            }, gen, failover: true);
        }

        private void EnterMusicBee(bool pauseSpotify)
        {
            var wasSpotify = Route == PlaybackRoute.Spotify;
            Route = PlaybackRoute.MusicBee;
            startPending = false;
            pendingVolume = null;
            spotify.SetFastPolling(false);

            if (settings.MuteAppliedBySpotiBee)
            {
                settings.MuteAppliedBySpotiBee = false;
                SaveSettings();
                if (mb.Muted)
                    mb.Muted = false;
            }
            // Delay restoring scrobbles so MusicBee can't submit the Spotify track that just ended
            if (settings.ScrobbleSuppressedBySpotiBee && !scrobbleRestoreAt.HasValue)
                scrobbleRestoreAt = now() + ScrobbleRestoreDelay;

            if (pauseSpotify && wasSpotify)
                _ = Try(spotify.PauseAsync, generation);
        }

        private void HandOver()
        {
            EnterMusicBee(pauseSpotify: false);
            Route = PlaybackRoute.HandedOver;
            if (mb.PlayState == PlayState.Playing)
                mb.PlayPause();
            Status("Spotify is playing something else, so MusicBee paused. Press play in MusicBee to take back control.");
            StateChanged?.Invoke();
        }

        private void FailOver(string reason)
        {
            spotifyUnavailableUntil = now() + UnavailableBackoff;
            if (currentIsPlaceholder)
            {
                EnterMusicBee(pauseSpotify: false);
                StateChanged?.Invoke();
                SkipCurrent(reason + ": skipping to the next track you have locally");
            }
            else
            {
                Status(reason + ": playing your local file instead.");
                EnterMusicBee(pauseSpotify: true);
                StateChanged?.Invoke();
            }
        }

        private void SkipCurrent(string reason)
        {
            var t = now();
            consecutiveSkips = t - lastSkipAt < TimeSpan.FromSeconds(10) ? consecutiveSkips + 1 : 1;
            lastSkipAt = t;
            if (consecutiveSkips > MaxConsecutiveSkips)
            {
                consecutiveSkips = 0;
                if (mb.PlayState == PlayState.Playing)
                    mb.PlayPause();
                Status("Nothing playable: everything coming up needs Spotify. Paused.");
                return;
            }
            Status(reason + ".");
            mb.Next();
        }

        private void SuppressScrobble()
        {
            scrobbleRestoreAt = null;
            if (settings.AllowMusicBeeScrobblesForSpotify || settings.ScrobbleSuppressedBySpotiBee)
                return;
            if (mb.ScrobbleEnabled)
            {
                settings.ScrobbleSuppressedBySpotiBee = true;
                SaveSettings();
                mb.ScrobbleEnabled = false;
            }
        }

        private void RestoreScrobbleNow()
        {
            if (!settings.ScrobbleSuppressedBySpotiBee || Route == PlaybackRoute.Spotify)
                return;
            settings.ScrobbleSuppressedBySpotiBee = false;
            SaveSettings();
            mb.ScrobbleEnabled = true;
        }

        /// <summary>Undo everything SpotiBee changed in MusicBee. Call when the plugin closes.</summary>
        public void Shutdown()
        {
            if (Route == PlaybackRoute.Spotify)
                _ = Try(spotify.PauseAsync, generation);
            EnterMusicBee(pauseSpotify: false);
            scrobbleRestoreAt = null;
            if (settings.ScrobbleSuppressedBySpotiBee)
            {
                settings.ScrobbleSuppressedBySpotiBee = false;
                mb.ScrobbleEnabled = true;
                SaveSettings();
            }
        }

        // --- Helpers -----------------------------------------------------

        private bool InGrace => now() < graceUntil;

        private void MirrorToMusicBee(PlayState target)
        {
            graceUntil = now() + MirrorGrace;
            expectedMbState = target;
            mb.PlayPause();
        }

        /// <summary>
        /// Spotify can substitute a regional copy of a track with a different ID ("relinking"),
        /// so a title + length match counts as the same track.
        /// </summary>
        private bool IsExpectedItem(Track item)
        {
            if (item == null || currentRecord == null)
                return false;
            if (item.Uri == currentRecord.Uri)
                return true;
            return string.Equals(item.Name, currentRecord.Title, StringComparison.OrdinalIgnoreCase) &&
                   Math.Abs(item.DurationMs - currentRecord.DurationMs) < 2000;
        }

        private bool IsNearEnd() =>
            lastSpotifyRemainingMs < NearEndMs || mb.DurationMs - mb.PositionMs < NearEndMs;

        private void RememberMbPosition(int positionMs)
        {
            lastMbPosition = positionMs;
            lastMbPositionAt = now();
        }

        private void SendVolume(int percent)
        {
            lastVolumeSentAt = now();
            var device = currentDeviceId;
            _ = Try(async () =>
            {
                try
                {
                    await spotify.SetVolumeAsync(percent);
                }
                catch (SpotifyApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // Warn once per device; "" stands for a device we haven't seen an ID for yet
                    var key = device ?? "";
                    if (volumeWarnedDevice != key)
                    {
                        volumeWarnedDevice = key;
                        Status("This Spotify device doesn't allow remote volume control.");
                    }
                }
            }, generation);
        }

        /// <summary>
        /// Runs a Spotify command. Results from an older track (generation changed) are ignored.
        /// With failover, a failure while this track is still current switches away from Spotify.
        /// </summary>
        private async Task Try(Func<Task> action, int gen, bool failover = false)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                if (gen != generation || Route != PlaybackRoute.Spotify)
                    return;
                var reason = ex is SpotifyApiException api ? api.FriendlyMessage.TrimEnd('.') : "Couldn't reach Spotify";
                if (failover)
                    FailOver(reason);
                else
                    Status(reason + ".");
            }
        }

        private static int ToPercent(float volume) => (int)Math.Round(Math.Max(0, Math.Min(1, volume)) * 100);

        private void Status(string message) => StatusMessage?.Invoke(message);

        private void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception ex) { Status("Couldn't save SpotiBee settings: " + ex.Message); }
        }
    }
}
