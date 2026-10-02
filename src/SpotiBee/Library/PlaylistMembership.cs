using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    /// <summary>
    /// Which tracks are in each Spotify playlist the user can edit, so menus can tick the playlists
    /// a track is already in. Kept per playlist version (snapshot ID): after the first look, only
    /// playlists that changed are fetched again. Saved between sessions.
    /// Must be used on one thread (MusicBee's UI thread).
    /// </summary>
    public sealed class PlaylistMembership
    {
        /// <summary>Opening a menu twice in a row shouldn't list the playlists twice.</summary>
        private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

        private readonly string filePath;
        private List<Entry> playlists = new List<Entry>();
        private DateTime refreshedUtc;
        private Task refreshing;

        public PlaylistMembership(string filePath)
        {
            this.filePath = filePath;
            Load();
        }

        /// <summary>Editable playlists, in Spotify's order.</summary>
        public IReadOnlyList<Entry> Playlists => playlists;

        public bool HasLoaded => refreshedUtc != default || playlists.Count > 0;

        /// <summary>Goes up whenever what the menus would show changes.</summary>
        public int Version { get; private set; }

        /// <summary>Brings the cache up to date. Concurrent callers share one refresh.</summary>
        public Task RefreshAsync(ISpotifyPlaylists spotify, string userId, TrackStore store, bool force = false)
        {
            if (refreshing != null)
                return refreshing;
            if (!force && DateTime.UtcNow - refreshedUtc < FreshFor)
                return Task.CompletedTask;
            var task = RefreshCoreAsync(spotify, userId, store);
            // A refresh that failed straight away has already cleared this; don't leave it stuck
            refreshing = task.IsCompleted ? null : task;
            return task;
        }

        private async Task RefreshCoreAsync(ISpotifyPlaylists spotify, string userId, TrackStore store)
        {
            try
            {
                var listed = await spotify.GetMyPlaylistsAsync();
                var known = playlists.ToDictionary(p => p.Id, StringComparer.Ordinal);
                var fresh = new List<Entry>();
                foreach (var p in listed.Where(p => p?.Id != null))
                {
                    // Only the user's own and collaborative playlists can be changed (or even read, since Feb 2026)
                    if (userId != null && p.Owner?.Id != userId && !p.Collaborative)
                        continue;
                    if (!known.TryGetValue(p.Id, out var entry) || entry.SnapshotId != p.SnapshotId || entry.TrackIds == null)
                    {
                        entry = new Entry { Id = p.Id, SnapshotId = p.SnapshotId };
                        var link = store.GetPlaylist(p.Id);
                        if (link?.TrackIds != null && link.SnapshotId == p.SnapshotId)
                        {
                            // A synced playlist that hasn't changed since: SpotiBee already knows its tracks
                            entry.TrackIds = link.TrackIds.ToList();
                        }
                        else
                        {
                            try
                            {
                                var items = await spotify.GetPlaylistItemsAsync(p.Id);
                                entry.TrackIds = items.Select(i => i.Track?.Id).Where(id => id != null).ToList();
                            }
                            catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == HttpStatusCode.NotFound)
                            {
                                continue;   // not editable after all
                            }
                        }
                    }
                    entry.Name = p.Name;
                    entry.Count = p.ItemCount;
                    fresh.Add(entry);
                }
                if (Signature(fresh) != Signature(playlists))
                {
                    playlists = fresh;
                    Version++;
                    Save();
                }
                refreshedUtc = DateTime.UtcNow;
            }
            finally
            {
                refreshing = null;
            }
        }

        public bool Contains(Entry playlist, string trackId) => playlist.Ids.Contains(trackId);

        /// <summary>Records a change SpotiBee just made, so menus are right without fetching the playlist again.</summary>
        public void Added(Entry playlist, IEnumerable<string> trackIds, string newSnapshot)
        {
            var added = trackIds.ToList();
            playlist.TrackIds.AddRange(added);
            playlist.Count += added.Count;
            Changed(playlist, newSnapshot);
        }

        public void Removed(Entry playlist, IEnumerable<string> trackIds, string newSnapshot)
        {
            var removed = new HashSet<string>(trackIds, StringComparer.Ordinal);
            playlist.Count -= playlist.TrackIds.RemoveAll(removed.Contains);
            Changed(playlist, newSnapshot);
        }

        private void Changed(Entry playlist, string newSnapshot)
        {
            playlist.ResetIndex();
            playlist.Count = Math.Max(0, playlist.Count);
            // Without a new snapshot, fetch it again next time rather than trust a stale version
            playlist.SnapshotId = newSnapshot;
            Version++;
            Save();
        }

        private static string Signature(IEnumerable<Entry> list) =>
            string.Join("\n", list.Select(e => $"{e.Id}\t{e.SnapshotId}\t{e.Name}\t{e.Count}"));

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, Json.Stringify(playlists));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Saving the playlist cache", ex);
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(filePath) && Json.TryParse<List<Entry>>(File.ReadAllText(filePath), out var saved))
                    playlists = saved.Where(p => p?.Id != null && p.TrackIds != null).ToList();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Loading the playlist cache", ex);
            }
        }

        [DataContract]
        public sealed class Entry
        {
            [DataMember] public string Id { get; set; }
            [DataMember] public string Name { get; set; }
            [DataMember] public string SnapshotId { get; set; }
            [DataMember] public int Count { get; set; }
            [DataMember] public List<string> TrackIds { get; set; }

            private HashSet<string> ids;

            internal HashSet<string> Ids => ids ??= new HashSet<string>(TrackIds ?? new List<string>(), StringComparer.Ordinal);

            internal void ResetIndex() => ids = null;
        }
    }
}
