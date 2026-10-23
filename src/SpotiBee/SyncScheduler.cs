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
    /// to pick up changes made in Spotify, and on demand. One sync runs at a time. Paused links
    /// only sync when asked to directly. Also notices new Spotify playlists.
    /// Must be used on MusicBee's UI thread.
    /// </summary>
    public sealed class SyncScheduler : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan OwnWriteEcho = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan SoonDelay = TimeSpan.FromSeconds(2);

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

        /// <summary>
        /// Spotify playlists the user can import that weren't there before, found while syncing.
        /// Not raised when they're imported automatically.
        /// </summary>
        public event Action<IReadOnlyList<Playlist>> NewPlaylistsFound;

        public void Start() => timer.Start();

        /// <summary>MusicBee reported a playlist change (PlaylistUpdated/Deleted).</summary>
        public void OnMusicBeePlaylistChanged(string url)
        {
            if (string.IsNullOrEmpty(url) || controller.Settings.DisableAutoSync)
                return;
            if (ignoreUntil.TryGetValue(url, out var until) && DateTime.UtcNow < until)
                return;   // our own write echoing back
            var link = controller.Store.FindPlaylistByMusicBeeUrl(url);
            if (link == null || link.SyncPaused)
                return;
            pendingByUrl[url] = DateTime.UtcNow + ChangeDebounce;
        }

        /// <summary>
        /// SpotiBee changed this playlist on Spotify (e.g. from the panel): bring MusicBee's copy in
        /// line in a moment rather than at the next poll. Changes made close together share one sync.
        /// </summary>
        public void SyncSoon(PlaylistRecord link)
        {
            if (link?.MusicBeePlaylistUrl == null || link.SyncPaused || controller.Settings.DisableAutoSync)
                return;
            pendingByUrl[link.MusicBeePlaylistUrl] = DateTime.UtcNow + SoonDelay;
        }

        /// <summary>
        /// Sync every linked playlist that isn't paused, and look for new Spotify playlists.
        /// Returns null if a sync is already running or not connected.
        /// </summary>
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
                var listing = (await controller.Client.GetMyPlaylistsAsync(ct)).Where(p => p?.Id != null).ToList();
                var snapshots = listing.ToDictionary(p => p.Id, p => p.SnapshotId);
                await LookForNewPlaylistsAsync(listing, progress, ct);

                foreach (var link in links)
                {
                    ct.ThrowIfCancellationRequested();
                    if (link.SyncPaused)
                        continue;
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
                    outcomes.Add(await SyncLinkAsync(sync, link, link.IsLikedSongs ? null : snapshots[link.SpotifyId], progress, ct));
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

        /// <summary>Syncs one linked playlist, even a paused one (the import and linked playlists windows).</summary>
        public async Task<SyncOutcome> SyncOneAsync(PlaylistRecord link, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var sync = Create() ?? throw new InvalidOperationException("Not connected to Spotify.");
            while (running)
                await Task.Delay(250, ct);
            running = true;
            try
            {
                return await SyncLinkAsync(sync, link, null, progress, ct);
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
                        if (link != null && !link.SyncPaused)
                            outcomes.Add(await SyncLinkAsync(sync, link, null, null, CancellationToken.None));
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

        /// <summary>Syncs, and records a failure on the link so the linked playlists window can show it.</summary>
        private async Task<SyncOutcome> SyncLinkAsync(PlaylistSync sync, PlaylistRecord link, string knownSnapshot,
            IProgress<ImportProgress> progress, CancellationToken ct)
        {
            try
            {
                return await sync.SyncAsync(link, knownSnapshot, progress, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                link.LastResult = "Failed: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
                try { controller.Store.Save(); }
                catch (Exception saveError) { Diagnostics.Log("Saving library", saveError); }
                throw;
            }
        }

        /// <summary>
        /// Announces (or imports, if the user chose that) Spotify playlists that appeared since the
        /// last look. The first listing only records what's there, so existing playlists never count as new.
        /// </summary>
        private async Task LookForNewPlaylistsAsync(List<Playlist> listing, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var store = controller.Store;
            // Normally loaded at connect, but don't depend on that having worked
            var me = (await controller.GetUserAsync())?.Id;
            if (me == null)
                return;
            if (!store.HasSeenPlaylists)
            {
                store.MarkPlaylistsSeen(listing.Select(p => p.Id));
                store.Save();
                Diagnostics.Log($"New playlists: recorded the {listing.Count} existing Spotify playlists; only later ones count as new");
                return;
            }

            var unseen = listing.Where(p => !store.HasSeenPlaylist(p.Id)).ToList();
            if (unseen.Count == 0)
                return;
            // Someone else's playlists can't be imported, and linked ones (e.g. sent from MusicBee) are already here
            var fresh = unseen.Where(p => store.GetPlaylist(p.Id) == null && (p.Owner?.Id == me || p.Collaborative)).ToList();
            foreach (var p in unseen.Except(fresh))
                Diagnostics.Log($"New playlists: ignoring \"{p.Name}\" ({(store.GetPlaylist(p.Id) != null ? "already linked" : "owned by " + p.Owner?.Id)})");
            store.MarkPlaylistsSeen(unseen.Except(fresh).Select(p => p.Id));
            store.Save();
            if (fresh.Count == 0)
                return;

            Diagnostics.Log($"New playlists: found {string.Join(", ", fresh.Select(p => "\"" + p.Name + "\""))}" +
                            (controller.Settings.AutoImportNewPlaylists ? " (importing)" : " (showing a notice)"));
            if (!controller.Settings.AutoImportNewPlaylists)
            {
                NewPlaylistsFound?.Invoke(fresh);
                return;
            }

            var importer = CreateImporter();
            foreach (var playlist in fresh)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await importer.ImportAsync(ImportSource.From(playlist), progress, ct);
                    controller.ShowStatus($"Imported new Spotify playlist \"{playlist.Name}\": {result.Summary}");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Diagnostics.Log($"Importing new playlist {playlist.Id}", ex);
                    controller.ShowStatus($"Couldn't import new Spotify playlist \"{playlist.Name}\". " +
                                          "Try it from Import Spotify Playlists.");
                }
                // Imported or not, don't try again every few minutes
                store.MarkPlaylistsSeen(new[] { playlist.Id });
                store.Save();
            }
        }

        private PlaylistImporter CreateImporter()
        {
            var placeholders = new PlaceholderWriter(controller.Http, controller.Settings.EffectivePlaceholderFolder);
            return new PlaylistImporter(controller.Client, musicBee, controller.Store, placeholders);
        }

        private PlaylistSync Create()
        {
            var client = controller.Client;
            if (client == null)
                return null;
            var importer = CreateImporter();
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
