using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SpotiBee.Spotify
{
    /// <summary>
    /// Polls Spotify's playback state. The Web API has no push events, so this is the
    /// only way to follow changes made in the Spotify app or on other devices.
    /// </summary>
    public sealed class PlaybackMonitor : IDisposable
    {
        private static readonly TimeSpan PlayingInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ErrorInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan TrackEndSlack = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

        private readonly SpotifyClient client;
        private CancellationTokenSource stop;
        private CancellationTokenSource wake = new CancellationTokenSource();
        private readonly object wakeLock = new object();
        private string lastTrackUri;

        public PlaybackMonitor(SpotifyClient client)
        {
            this.client = client;
        }

        /// <summary>Raised on a background thread after every successful poll. State is null when nothing is playing.</summary>
        public event Action<PlaybackSnapshot> StateUpdated;

        /// <summary>Raised on a background thread when the playing item changes (including to nothing).</summary>
        public event Action<Track> TrackChanged;

        /// <summary>Raised on a background thread when a poll fails.</summary>
        public event Action<Exception> PollFailed;

        public bool IsRunning => stop != null;

        /// <summary>Poll every second while playing, for tighter sync when MusicBee is driving Spotify.</summary>
        public bool FastPolling { get; set; }

        public void Start()
        {
            if (stop != null)
                return;
            stop = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(stop.Token));
        }

        public void Stop()
        {
            stop?.Cancel();
            stop = null;
        }

        /// <summary>Poll soon, e.g. right after sending a command, instead of waiting for the next interval.</summary>
        public void RequestRefresh()
        {
            lock (wakeLock)
                wake.Cancel();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            // Give Spotify a moment to apply a command before re-reading state
            var afterWakeDelay = TimeSpan.FromMilliseconds(350);

            while (!ct.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var state = await client.GetPlaybackStateAsync(ct).ConfigureAwait(false);
                    var snapshot = new PlaybackSnapshot(state, DateTime.UtcNow - TimeSpan.FromTicks(stopwatch.Elapsed.Ticks / 2));

                    var uri = state?.Item?.Uri;
                    if (uri != lastTrackUri)
                    {
                        lastTrackUri = uri;
                        TrackChanged?.Invoke(state?.Item);
                    }
                    StateUpdated?.Invoke(snapshot);

                    delay = state != null && state.IsPlaying
                        ? (FastPolling ? MinInterval : PlayingInterval)
                        : IdleInterval;

                    // Poll just after the track should end so track changes show up promptly
                    var remaining = snapshot.Remaining;
                    if (state != null && state.IsPlaying && remaining.HasValue && remaining.Value + TrackEndSlack < delay)
                        delay = TimeSpan.FromTicks(Math.Max((remaining.Value + TrackEndSlack).Ticks, MinInterval.Ticks));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    PollFailed?.Invoke(ex);
                    delay = ex is SpotifyApiException api && api.RetryAfter.HasValue
                        ? api.RetryAfter.Value
                        : ErrorInterval;
                    if (ex is SpotifyAuthException auth && auth.IsInvalidGrant)
                        return;
                }

                CancellationTokenSource currentWake;
                lock (wakeLock)
                {
                    if (wake.IsCancellationRequested)
                        wake = new CancellationTokenSource();
                    currentWake = wake;
                }

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, currentWake.Token);
                    await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    // Woken early by RequestRefresh
                    try { await Task.Delay(afterWakeDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public void Dispose() => Stop();
    }

    /// <summary>A playback state plus the time it was sampled, so progress can be interpolated between polls.</summary>
    public sealed class PlaybackSnapshot
    {
        public PlaybackSnapshot(PlaybackState state, DateTime sampledUtc)
        {
            State = state;
            SampledUtc = sampledUtc;
        }

        public PlaybackState State { get; }
        public DateTime SampledUtc { get; }

        public bool IsPlaying => State?.IsPlaying == true;

        public TimeSpan Position
        {
            get
            {
                if (State?.ProgressMs == null)
                    return TimeSpan.Zero;
                var position = TimeSpan.FromMilliseconds(State.ProgressMs.Value);
                if (IsPlaying)
                    position += DateTime.UtcNow - SampledUtc;
                var duration = Duration;
                return duration > TimeSpan.Zero && position > duration ? duration : position;
            }
        }

        public TimeSpan Duration => TimeSpan.FromMilliseconds(State?.Item?.DurationMs ?? 0);

        public TimeSpan? Remaining => State?.Item == null || State.ProgressMs == null
            ? (TimeSpan?)null
            : Duration - Position;
    }
}
