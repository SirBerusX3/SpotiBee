using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    public sealed class SyncOutcome
    {
        public string Name { get; set; }
        public int SentToSpotify { get; set; }
        public int RemovedFromSpotify { get; set; }
        public int AddedToMusicBee { get; set; }
        public int RemovedFromMusicBee { get; set; }
        public List<string> Unmatched { get; set; } = new List<string>();
        public bool Unlinked { get; set; }
        public string Note { get; set; }

        public bool Changed => SentToSpotify + RemovedFromSpotify + AddedToMusicBee + RemovedFromMusicBee > 0;

        public string Summary
        {
            get
            {
                if (Unlinked)
                    return Note ?? "Unlinked";
                var parts = new List<string>();
                if (SentToSpotify > 0) parts.Add($"{SentToSpotify} added to Spotify");
                if (RemovedFromSpotify > 0) parts.Add($"{RemovedFromSpotify} removed from Spotify");
                if (AddedToMusicBee > 0) parts.Add($"{AddedToMusicBee} added in MusicBee");
                if (RemovedFromMusicBee > 0) parts.Add($"{RemovedFromMusicBee} removed in MusicBee");
                if (parts.Count == 0) parts.Add("up to date");
                if (Unmatched.Count > 0) parts.Add($"{Unmatched.Count} not on Spotify");
                if (Note != null) parts.Add(Note);
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>
    /// Keeps a linked MusicBee playlist and Spotify playlist (or Liked Songs) in step, both ways.
    /// Uses the track list from the last sync as the common ancestor: whichever side changed wins,
    /// and when both changed, removals from either side apply and additions from both are kept.
    /// Local files Spotify doesn't have stay in the MusicBee playlist, near where they were.
    /// </summary>
    public sealed class PlaylistSync
    {
        public const string CreatedDescription = "Synced from MusicBee by SpotiBee";

        private readonly ISpotifyPlaylists spotify;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;
        private readonly PlaylistImporter importer;
        private readonly TrackResolver resolver;

        public PlaylistSync(ISpotifyPlaylists spotify, IMusicBeeLibrary musicBee, TrackStore store,
            PlaylistImporter importer, TrackResolver resolver)
        {
            this.spotify = spotify;
            this.musicBee = musicBee;
            this.store = store;
            this.importer = importer;
            this.resolver = resolver;
        }

        /// <summary>Called after the MusicBee playlist is written, so the resulting change notification can be ignored.</summary>
        public event Action<string> WroteMusicBeePlaylist;

        /// <summary>Creates a Spotify playlist from a MusicBee playlist and links the two.</summary>
        public async Task<SyncOutcome> SendToSpotifyAsync(string musicBeeUrl, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var name = musicBee.GetPlaylistName(musicBeeUrl) ?? "MusicBee playlist";
            progress?.Report(new ImportProgress($"Creating \"{name}\" on Spotify…"));
            var created = await spotify.CreatePlaylistAsync(name, CreatedDescription, ct);
            var link = new PlaylistRecord
            {
                SpotifyId = created.Id,
                Name = name,
                MusicBeeName = name,
                MusicBeePlaylistUrl = musicBeeUrl,
                SnapshotId = created.SnapshotId,
                TrackIds = new List<string>(),
                LastImportedUtc = DateTime.UtcNow,
            };
            store.SetPlaylist(link);
            store.Save();
            // With an empty ancestor and an untouched Spotify side, this pushes everything
            return await SyncAsync(link, created.SnapshotId, progress, ct);
        }

        /// <param name="knownSnapshot">Spotify's current snapshot ID if already known (e.g. from a playlist listing).</param>
        public async Task<SyncOutcome> SyncAsync(PlaylistRecord link, string knownSnapshot, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var outcome = new SyncOutcome { Name = link.MusicBeeName ?? link.Name };

            // Find the MusicBee side; it may have been renamed or moved
            var url = link.MusicBeePlaylistUrl;
            if (!musicBee.PlaylistExists(url))
                url = musicBee.FindPlaylist(null, link.MusicBeeName ?? link.Name);
            if (url == null)
            {
                store.RemovePlaylist(link.SpotifyId);
                store.Save();
                outcome.Unlinked = true;
                outcome.Note = "The MusicBee playlist is gone, so it's no longer synced (the Spotify playlist was left alone)";
                return outcome;
            }
            link.MusicBeePlaylistUrl = url;
            link.MusicBeeName = musicBee.GetPlaylistName(url) ?? link.MusicBeeName;
            outcome.Name = link.MusicBeeName;
            var isAuto = musicBee.IsAutoPlaylist(url);

            // MusicBee side: resolve every file to a Spotify track where possible
            progress?.Report(new ImportProgress($"Matching \"{outcome.Name}\" with Spotify…"));
            var mbFiles = musicBee.GetPlaylistFiles(url);
            var mbSeq = new List<string>();
            var mbFileById = new Dictionary<string, string>(StringComparer.Ordinal);
            var loose = new List<(string File, string AnchorId)>();
            string lastId = null;
            for (var i = 0; i < mbFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ImportProgress($"Matching \"{outcome.Name}\" with Spotify… {i + 1} of {mbFiles.Length}", i + 1, mbFiles.Length));
                var id = await resolver.ResolveAsync(mbFiles[i], ct);
                if (id == null)
                {
                    loose.Add((mbFiles[i], lastId));
                    outcome.Unmatched.Add(Describe(mbFiles[i]));
                    continue;
                }
                mbSeq.Add(id);
                if (!mbFileById.ContainsKey(id))
                    mbFileById[id] = mbFiles[i];
                lastId = id;
            }

            var baseSeq = link.TrackIds ?? new List<string>();
            var liked = link.IsLikedSongs;
            var mbChanged = liked ? !SameSet(mbSeq, baseSeq) : !mbSeq.SequenceEqual(baseSeq);

            // Spotify side: only fetched when it may have changed or has to be written
            List<(Track Track, bool IsLocal)> spEntries = null;
            List<string> spSeq;
            bool spChanged;
            if (liked)
            {
                // Newest likes come first, so the first page plus the total reveals most changes cheaply
                var firstPage = await spotify.GetSavedTracksPageAsync(50, ct);
                var firstIds = (firstPage?.Items ?? new SavedTrack[0]).Select(s => s.Track?.Id).Where(id => id != null).ToList();
                var looksSame = firstPage != null && firstPage.Total == baseSeq.Count &&
                                firstIds.SequenceEqual(baseSeq.Take(firstIds.Count));
                if (looksSame && !mbChanged)
                {
                    spSeq = baseSeq;
                    spChanged = false;
                }
                else
                {
                    spEntries = await importer.FetchAsync(ImportSource.LikedSongs(), progress, ct);
                    spSeq = TrackIdsOf(spEntries);
                    spChanged = !SameSet(spSeq, baseSeq);
                }
            }
            else
            {
                var snapshot = knownSnapshot ?? (await spotify.GetPlaylistAsync(link.SpotifyId, ct))?.SnapshotId;
                spChanged = snapshot == null || snapshot != link.SnapshotId;
                if (spChanged || mbChanged)
                {
                    spEntries = await importer.FetchAsync(new ImportSource { Id = link.SpotifyId, Name = link.Name }, progress, ct);
                    spSeq = TrackIdsOf(spEntries);
                    link.HasUnsyncableItems = spEntries.Any(e => e.Track == null || e.IsLocal || e.Track.IsLocal || !e.Track.IsTrack);
                    // A new snapshot with the same tracks (e.g. a description edit) isn't a track change
                    spChanged = !spSeq.SequenceEqual(baseSeq);
                    link.SnapshotId = snapshot;
                }
                else
                {
                    spSeq = baseSeq;
                }
            }

            link.Unmatched = outcome.Unmatched;
            if (!mbChanged && !spChanged)
            {
                link.LastSyncedUtc = DateTime.UtcNow;
                store.Save();
                return outcome;
            }

            List<string> final;
            if (isAuto)
                final = mbSeq;          // auto-playlists are filters; MusicBee's side can't be written
            else if (!mbChanged)
                final = spSeq;
            else if (!spChanged)
                final = mbSeq;
            else
                final = Merge(baseSeq, mbSeq, spSeq, additionsFirst: liked);
            if (isAuto && spChanged && !mbChanged)
                outcome.Note = "auto-playlist: Spotify-side changes can't be applied in MusicBee";

            // Write Spotify
            if (liked)
            {
                var adds = final.Except(spSeq).Distinct().ToList();
                var removes = spSeq.Except(final).Distinct().ToList();
                if (adds.Count > 0)
                    await spotify.SaveToLibraryAsync(adds.Select(ToUri), ct);
                if (removes.Count > 0)
                    await spotify.RemoveFromLibraryAsync(removes.Select(ToUri), ct);
                outcome.SentToSpotify = adds.Count;
                outcome.RemovedFromSpotify = removes.Count;
            }
            else if (!final.SequenceEqual(spSeq))
            {
                progress?.Report(new ImportProgress($"Updating \"{link.Name}\" on Spotify…"));
                var (added, removed, snapshot) = await WriteSpotifyAsync(link.SpotifyId, spSeq, final, link.HasUnsyncableItems, ct);
                outcome.SentToSpotify = added;
                outcome.RemovedFromSpotify = removed;
                if (snapshot != null)
                    link.SnapshotId = snapshot;
            }

            // Write MusicBee
            if (!isAuto && !final.SequenceEqual(mbSeq))
            {
                var fileById = new Dictionary<string, string>(mbFileById, StringComparer.Ordinal);
                var needed = new HashSet<string>(final.Where(id => !fileById.ContainsKey(id)), StringComparer.Ordinal);
                if (needed.Count > 0 && spEntries != null)
                {
                    var toMaterialize = spEntries.Where(e => e.Track?.Id != null && needed.Contains(e.Track.Id))
                        .GroupBy(e => e.Track.Id).Select(g => g.First()).ToList();
                    var materialized = await importer.MaterializeAsync(toMaterialize, null, progress, ct);
                    foreach (var pair in materialized.FileById)
                        fileById[pair.Key] = pair.Value;
                }

                var files = BuildMusicBeeFiles(final, fileById, loose);
                outcome.AddedToMusicBee = final.Except(mbSeq).Count();
                outcome.RemovedFromMusicBee = mbSeq.Except(final).Count();
                musicBee.SetPlaylistFiles(url, files.ToArray());
                WroteMusicBeePlaylist?.Invoke(url);
            }

            link.TrackIds = final;
            link.LastSyncedUtc = DateTime.UtcNow;
            store.SetPlaylist(link);
            store.Save();
            return outcome;
        }

        /// <summary>
        /// Brings Spotify from <paramref name="current"/> to <paramref name="final"/> with targeted
        /// adds and removes, so Spotify keeps each song's "date added". Falls back to replacing the
        /// whole list only when the order changed, and never when the playlist holds items SpotiBee
        /// can't represent (podcast episodes, Spotify local files), which a replace would delete.
        /// </summary>
        internal async Task<(int Added, int Removed, string Snapshot)> WriteSpotifyAsync(string playlistId,
            IList<string> current, IList<string> final, bool hasUnsyncableItems, CancellationToken ct)
        {
            string snapshot = null;
            var sim = current.ToList();

            // Removing a URI removes every copy, so anything whose count drops goes entirely (and is re-added below)
            var finalCounts = Counts(final);
            var toRemove = Counts(sim).Where(c => c.Value > (finalCounts.TryGetValue(c.Key, out var n) ? n : 0))
                                      .Select(c => c.Key).ToList();
            foreach (var chunk in Chunks(toRemove, SpotifyClient.MaxPlaylistItemsPerRequest))
                snapshot = await spotify.RemovePlaylistItemsAsync(playlistId, chunk.Select(ToUri).ToList(), ct);
            var removedSet = new HashSet<string>(toRemove, StringComparer.Ordinal);
            sim.RemoveAll(removedSet.Contains);
            var removed = toRemove.Count(id => !finalCounts.ContainsKey(id));

            // What's still missing, as a multiset
            var missing = Counts(final);
            foreach (var id in sim)
                if (missing.ContainsKey(id) && --missing[id] == 0)
                    missing.Remove(id);
            var added = missing.Values.Sum();

            if (hasUnsyncableItems)
            {
                // Positions would be off by the hidden items, so append
                var appends = final.Where(id => missing.TryGetValue(id, out var n) && n > 0 && Take(missing, id)).ToList();
                foreach (var chunk in Chunks(appends, SpotifyClient.MaxPlaylistItemsPerRequest))
                    snapshot = await spotify.AddPlaylistItemsAsync(playlistId, chunk.Select(ToUri).ToList(), null, ct);
                return (added, removed, snapshot);
            }

            // Insert runs of missing items at their final positions
            for (var i = 0; i < final.Count;)
            {
                if (i < sim.Count && sim[i] == final[i])
                {
                    i++;
                    continue;
                }
                var run = new List<string>();
                var j = i;
                while (j < final.Count && run.Count < SpotifyClient.MaxPlaylistItemsPerRequest &&
                       missing.TryGetValue(final[j], out var n) && n > 0)
                {
                    Take(missing, final[j]);
                    run.Add(final[j]);
                    j++;
                }
                if (run.Count == 0)
                {
                    i++;       // an order difference; the replace below fixes it
                    continue;
                }
                var position = Math.Min(i, sim.Count);
                snapshot = await spotify.AddPlaylistItemsAsync(playlistId, run.Select(ToUri).ToList(), position, ct);
                sim.InsertRange(position, run);
                i += run.Count;
            }

            if (!sim.SequenceEqual(final))
                snapshot = await spotify.ReplacePlaylistItemsAsync(playlistId, final.Select(ToUri).ToList(), ct);
            return (added, removed, snapshot);
        }

        /// <summary>
        /// Three-way merge of ordered track lists. Removals from either side apply; the other
        /// side's order is kept; MusicBee's additions are placed after the track they followed there.
        /// </summary>
        public static List<string> Merge(IList<string> baseSeq, IList<string> mine, IList<string> theirs, bool additionsFirst = false)
        {
            var baseSet = new HashSet<string>(baseSeq, StringComparer.Ordinal);
            var mineSet = new HashSet<string>(mine, StringComparer.Ordinal);
            var removedByMine = new HashSet<string>(baseSet.Where(id => !mineSet.Contains(id)), StringComparer.Ordinal);

            var result = theirs.Where(id => !removedByMine.Contains(id)).ToList();
            var additions = mine.Where(id => !baseSet.Contains(id)).Distinct().Where(id => !result.Contains(id)).ToList();
            if (additionsFirst)
            {
                result.InsertRange(0, additions);
                return result;
            }

            foreach (var id in additions)
            {
                var index = mine.IndexOf(id);
                string anchor = null;
                for (var k = index - 1; k >= 0 && anchor == null; k--)
                    if (result.Contains(mine[k]))
                        anchor = mine[k];
                var insertAt = anchor == null ? 0 : result.LastIndexOf(anchor) + 1;
                result.Insert(insertAt, id);
            }
            return result;
        }

        /// <summary>Final track list to MusicBee files, with local-only files kept after the track they followed.</summary>
        internal static List<string> BuildMusicBeeFiles(IList<string> final, IDictionary<string, string> fileById,
            IList<(string File, string AnchorId)> loose)
        {
            var files = new List<string>();
            var finalSet = new HashSet<string>(final, StringComparer.Ordinal);
            files.AddRange(loose.Where(l => l.AnchorId == null).Select(l => l.File));

            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in final)
            {
                if (fileById.TryGetValue(id, out var file) && file != null)
                    files.Add(file);
                if (placed.Add(id))
                    files.AddRange(loose.Where(l => l.AnchorId == id).Select(l => l.File));
            }
            // Their anchor track was removed: keep them at the end rather than lose them
            files.AddRange(loose.Where(l => l.AnchorId != null && !finalSet.Contains(l.AnchorId)).Select(l => l.File));
            return files;
        }

        private string Describe(string file)
        {
            var t = musicBee.GetTrack(file);
            if (t == null || string.IsNullOrWhiteSpace(t.Title))
                return System.IO.Path.GetFileNameWithoutExtension(file);
            return string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{LocalMatcher.FirstArtist(t.Artist)} – {t.Title}";
        }

        private static List<string> TrackIdsOf(IEnumerable<(Track Track, bool IsLocal)> entries) =>
            entries.Where(e => e.Track?.Id != null && !e.IsLocal && !e.Track.IsLocal && e.Track.IsTrack)
                   .Select(e => e.Track.Id).ToList();

        private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
            new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);

        private static Dictionary<string, int> Counts(IEnumerable<string> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in ids)
                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
            return counts;
        }

        private static bool Take(Dictionary<string, int> counts, string id)
        {
            if (!counts.TryGetValue(id, out var n) || n <= 0)
                return false;
            if (n == 1)
                counts.Remove(id);
            else
                counts[id] = n - 1;
            return true;
        }

        private static IEnumerable<List<string>> Chunks(IList<string> items, int size)
        {
            for (var i = 0; i < items.Count; i += size)
                yield return items.Skip(i).Take(size).ToList();
        }

        private static string ToUri(string id) => "spotify:track:" + id;
    }
}
