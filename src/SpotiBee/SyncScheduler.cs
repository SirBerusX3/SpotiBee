using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee
{
    /// <summary>
    /// Decides when linked playlists sync: shortly after a change in MusicBee, every few minutes
    /// to pick up changes made in Spotify, and on demand. One sync runs at a time.
    /// Must be used on MusicBee's UI thread.
    /// </summary>
    public sealed class SyncScheduler : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan OwnWriteEcho = TimeSpan.FromSeconds(6);

        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Dictionary<string, DateTime> pendingByUrl = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ignoreUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime nextPoll = DateTime.UtcNow + FirstPollDelay;
        private bool running;
        private string lastError;

        public SyncScheduler(SpotiBeeController controller, IMusicBeeLibrary musicBee)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            timer.Tick += (s, e) => _ = OnTickAsync();
        }

        public bool IsRunning => running;

        public void Start() => timer.Start();

        /// <summary>MusicBee reported a playlist change (PlaylistUpdated/Deleted).</summary>
        public void OnMusicBeePlaylistChanged(string url)
        {
            if (string.IsNullOrEmpty(url) || controller.Settings.DisableAutoSync)
                return;
            if (ignoreUntil.TryGetValue(url, out var until) && DateTime.UtcNow < until)
                return;   // our own write echoing back
            if (controller.Store.FindPlaylistByMusicBeeUrl(url) == null)
                return;
            pendingByUrl[url] = DateTime.UtcNow + ChangeDebounce;
        }

        /// <summary>Sync every linked playlist now. Returns null if a sync is already running or not connected.</summary>
        public async Task<List<SyncOutcome>> SyncAllAsync(IProgress<ImportProgress> progress = null, CancellationToken ct = default)
        {
            var sync = Create();
            if (sync == null || running)
                return null;
            running = true;
            var outcomes = new List<SyncOutcome>();
            try
            {
                var links = controller.Store.Playlists.ToList();
                if (links.Count == 0)
                    return outcomes;

                // One listing gives every playlist's current version, so unchanged ones cost nothing more
                var snapshots = (await controller.Client.GetMyPlaylistsAsync(ct))
                    .Where(p => p?.Id != null).ToDictionary(p => p.Id, p => p.SnapshotId);

                foreach (var link in links)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!link.IsLikedSongs && !snapshots.ContainsKey(link.SpotifyId))
                    {
                        controller.Store.RemovePlaylist(link.SpotifyId);
                        controller.Store.Save();
                        outcomes.Add(new SyncOutcome
                        {
                            Name = link.MusicBeeName ?? link.Name,
                            Unlinked = true,
                            Note = $"\"{link.Name}\" is no longer in your Spotify playlists, so it's no longer synced",
                        });
                        continue;
                    }
                    outcomes.Add(await sync.SyncAsync(link, link.IsLikedSongs ? null : snapshots[link.SpotifyId], progress, ct));
                }
                lastError = null;
                Report(outcomes);
                return outcomes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportError(ex);
                return outcomes;
            }
            finally
            {
                running = false;
            }
        }

        /// <summary>Creates Spotify playlists from MusicBee playlists and links them.</summary>
        public async Task<SyncOutcome> SendToSpotifyAsync(string musicBeeUrl, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var sync = Create() ?? throw new InvalidOperationException("Not connected to Spotify.");
            while (running)
                await Task.Delay(250, ct);
            running = true;
            try
            {
                return await sync.SendToSpotifyAsync(musicBeeUrl, progress, ct);
            }
            finally
            {
                running = false;
            }
        }

        /// <summary>Syncs one linked playlist (used by the import window for already-linked playlists).</summary>
        public async Task<SyncOutcome> SyncOneAsync(PlaylistRecord link, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var sync = Create() ?? throw new InvalidOperationException("Not connected to Spotify.");
            while (running)
                await Task.Delay(250, ct);
            running = true;
            try
            {
                return await sync.SyncAsync(link, null, progress, ct);
            }
            finally
            {
                running = false;
            }
        }

        private async Task OnTickAsync()
        {
            if (running || controller.Client == null || controller.Settings.DisableAutoSync)
                return;
            var now = DateTime.UtcNow;

            var due = pendingByUrl.Where(p => p.Value <= now).Select(p => p.Key).ToList();
            if (due.Count > 0)
            {
                foreach (var url in due)
                    pendingByUrl.Remove(url);
                var sync = Create();
                running = true;
                try
                {
                    var outcomes = new List<SyncOutcome>();
                    foreach (var url in due)
                    {
                        var link = controller.Store.FindPlaylistByMusicBeeUrl(url);
                        if (link != null)
                            outcomes.Add(await sync.SyncAsync(link, null, null, CancellationToken.None));
                    }
                    lastError = null;
                    Report(outcomes);
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                }
                finally
                {
                    running = false;
                }
                return;
            }

            if (now >= nextPoll)
            {
                nextPoll = now + PollInterval;
                try { await SyncAllAsync(); }
                catch (OperationCanceledException) { }
            }
        }

        private PlaylistSync Create()
        {
            var client = controller.Client;
            if (client == null)
                return null;
            var placeholders = new PlaceholderWriter(controller.Http, controller.Settings.EffectivePlaceholderFolder);
            var importer = new PlaylistImporter(client, musicBee, controller.Store, placeholders);
            var sync = new PlaylistSync(client, musicBee, controller.Store, importer, new TrackResolver(client, musicBee, controller.Store));
            sync.WroteMusicBeePlaylist += url => ignoreUntil[url] = DateTime.UtcNow + OwnWriteEcho;
            return sync;
        }

        private void Report(IEnumerable<SyncOutcome> outcomes)
        {
            foreach (var o in outcomes.Where(o => o.Changed || o.Unlinked))
                controller.ShowStatus(o.Unlinked ? o.Note : $"Synced \"{o.Name}\": {o.Summary}");
        }

        private void ReportError(Exception ex)
        {
            var message = ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message;
            Diagnostics.Log("Playlist sync", ex);
            // Don't repeat the same failure every few minutes
            if (message != lastError)
                controller.ShowStatus("Playlist sync failed: " + message);
            lastError = message;
        }

        public void Dispose() => timer.Dispose();
    }
}
