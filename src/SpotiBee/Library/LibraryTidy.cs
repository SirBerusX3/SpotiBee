using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpotiBee.Library
{
    /// <summary>
    /// Keeps placeholders from piling up: swaps in the user's own file when they add a song they
    /// previously only had as a placeholder, and finds placeholders no playlist uses any more.
    /// </summary>
    public sealed class LibraryTidy
    {
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;
        private readonly Func<string, bool> isPlaceholderPath;
        private readonly string placeholderRoot;

        public LibraryTidy(IMusicBeeLibrary musicBee, TrackStore store, Func<string, bool> isPlaceholderPath, string placeholderRoot)
        {
            this.musicBee = musicBee;
            this.store = store;
            this.isPlaceholderPath = isPlaceholderPath;
            this.placeholderRoot = placeholderRoot;
        }

        public sealed class Orphan
        {
            public TrackRecord Record { get; set; }
            public string Path { get; set; }
            public long Bytes { get; set; }
        }

        /// <summary>
        /// For files just added to the library: where one is the same recording as a placeholder
        /// (same title, artist and length), use it instead in every MusicBee playlist.
        /// Returns how many placeholders were replaced.
        /// </summary>
        public int UpgradePlaceholders(IEnumerable<string> addedFiles) =>
            UpgradePlaceholders(addedFiles.Where(f => !string.IsNullOrEmpty(f) && !isPlaceholderPath(f))
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .Select(musicBee.GetTrack)
                                          .Where(t => t != null)
                                          .ToList());

        /// <summary>
        /// Checks the whole library for local copies of placeholders. Doesn't rely on MusicBee's
        /// "file added" notification, which isn't sent for every way of adding files.
        /// </summary>
        public int UpgradePlaceholdersFromLibrary() =>
            store.AllTracks.Any(NeedsLocalCopy)
                ? UpgradePlaceholders(musicBee.GetMusicTracks().Where(t => !isPlaceholderPath(t.Path)).ToList())
                : 0;

        private static bool NeedsLocalCopy(TrackRecord record) =>
            !string.IsNullOrEmpty(record.PlaceholderPath) && !record.LocalPathPinned &&
            (string.IsNullOrEmpty(record.LocalPath) || !File.Exists(record.LocalPath));

        private int UpgradePlaceholders(List<LocalTrack> candidates)
        {
            if (candidates.Count == 0)
                return 0;
            var matcher = new LocalMatcher(candidates);

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in store.AllTracks.Where(NeedsLocalCopy))
            {
                // Same recording if there is one, otherwise a close-length edit (flagged, so
                // Spotify-first playback plays it locally rather than using it as Spotify's clock)
                var track = new Spotify.Track
                {
                    Name = record.Title,
                    DurationMs = record.DurationMs,
                    Album = new Spotify.Album { Name = record.Album },
                    Artists = (record.Artist ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(a => new Spotify.Artist { Name = a }).ToArray(),
                };
                var match = matcher.MatchExactOrClose(track, out var lengthDiffers);
                if (match == null)
                    continue;
                record.LocalPath = match.Path;
                record.LocalLengthDiffers = lengthDiffers;
                replacements[record.PlaceholderPath] = match.Path;
            }
            Diagnostics.Log($"Placeholder upgrade: checked {candidates.Count} local file(s), {replacements.Count} placeholder(s) replaced");
            if (replacements.Count == 0)
                return 0;

            foreach (var (url, _) in musicBee.GetPlaylists())
            {
                if (musicBee.IsAutoPlaylist(url))
                    continue;
                var files = musicBee.GetPlaylistFiles(url);
                if (!files.Any(replacements.ContainsKey))
                    continue;
                musicBee.SetPlaylistFiles(url, files.Select(f => replacements.TryGetValue(f, out var local) ? local : f).ToArray());
            }
            store.InvalidatePathIndex();
            store.Save();
            return replacements.Count;
        }

        /// <summary>
        /// Placeholder files that no regular MusicBee playlist contains. Auto-playlists don't count:
        /// they're filters over the library ("Recently Added" holds every new placeholder), so
        /// counting them would make every placeholder look used.
        /// </summary>
        public List<Orphan> FindOrphans(string nowPlaying)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (url, _) in musicBee.GetPlaylists())
                if (!musicBee.IsAutoPlaylist(url))
                    used.UnionWith(musicBee.GetPlaylistFiles(url));
            if (!string.IsNullOrEmpty(nowPlaying))
                used.Add(nowPlaying);

            return store.AllTracks
                .Where(r => !string.IsNullOrEmpty(r.PlaceholderPath) && isPlaceholderPath(r.PlaceholderPath) &&
                            !used.Contains(r.PlaceholderPath) && File.Exists(r.PlaceholderPath))
                .Select(r => new Orphan { Record = r, Path = r.PlaceholderPath, Bytes = new FileInfo(r.PlaceholderPath).Length })
                .ToList();
        }

        /// <summary>Deletes orphaned placeholder files and any folders left empty. Returns how many were deleted.</summary>
        public int Delete(IEnumerable<Orphan> orphans)
        {
            var deleted = 0;
            foreach (var orphan in orphans)
            {
                try
                {
                    File.Delete(orphan.Path);
                    orphan.Record.PlaceholderPath = null;
                    deleted++;
                    RemoveEmptyFolders(Path.GetDirectoryName(orphan.Path));
                }
                catch (IOException) { /* in use; leave it for next time */ }
                catch (UnauthorizedAccessException) { }
            }
            store.InvalidatePathIndex();
            store.Save();
            return deleted;
        }

        private void RemoveEmptyFolders(string folder)
        {
            var root = placeholderRoot.TrimEnd('\\');
            while (!string.IsNullOrEmpty(folder) && folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }
    }
}
