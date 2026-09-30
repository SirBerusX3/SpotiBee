using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    public sealed class ImportSource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsLikedSongs => Id == PlaylistRecord.LikedSongsId;

        public static ImportSource LikedSongs() => new ImportSource { Id = PlaylistRecord.LikedSongsId, Name = "Liked Songs" };
        public static ImportSource From(Playlist playlist) => new ImportSource { Id = playlist.Id, Name = playlist.Name };
    }

    public sealed class ImportResult
    {
        public string Name { get; set; }
        public int Tracks { get; set; }
        public int LocalMatches { get; set; }
        public int PlaceholdersCreated { get; set; }
        public int PlaceholdersReused { get; set; }
        public int SkippedUnavailable { get; set; }
        public int SkippedEpisodes { get; set; }
        public int SkippedSpotifyLocalFiles { get; set; }

        public int Skipped => SkippedUnavailable + SkippedEpisodes + SkippedSpotifyLocalFiles;

        public string Summary
        {
            get
            {
                var parts = new List<string> { $"{Tracks} tracks" };
                if (LocalMatches > 0) parts.Add($"{LocalMatches} from your library");
                if (PlaceholdersCreated + PlaceholdersReused > 0) parts.Add($"{PlaceholdersCreated + PlaceholdersReused} via Spotify");
                if (Skipped > 0) parts.Add($"{Skipped} skipped");
                return string.Join(", ", parts);
            }
        }
    }

    public readonly struct ImportProgress
    {
        public ImportProgress(string message, int done = 0, int total = 0)
        {
            Message = message;
            Done = done;
            Total = total;
        }

        public string Message { get; }
        public int Done { get; }
        public int Total { get; }
    }

    /// <summary>Spotify tracks turned into MusicBee files, in playlist order.</summary>
    public sealed class Materialized
    {
        /// <summary>Spotify track IDs in order (real catalogue tracks only).</summary>
        public List<string> TrackIds { get; } = new List<string>();

        /// <summary>MusicBee files in order, including Spotify "local files" found in the library.</summary>
        public List<string> Files { get; } = new List<string>();

        public Dictionary<string, string> FileById { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Turns a Spotify playlist into a MusicBee playlist in the "Spotify" folder. Tracks the user
    /// owns locally use the local file; everything else gets a silent placeholder added to the library.
    /// Re-importing updates the same MusicBee playlist in place.
    /// </summary>
    public sealed class PlaylistImporter
    {
        public const string PlaylistFolder = "Spotify";
        private const int ParallelPlaceholderWrites = 4;

        private readonly ISpotifyPlaylists spotify;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;
        private readonly PlaceholderWriter placeholders;
        private LocalMatcher matcher;

        public PlaylistImporter(ISpotifyPlaylists spotify, IMusicBeeLibrary musicBee, TrackStore store, PlaceholderWriter placeholders)
        {
            this.spotify = spotify;
            this.musicBee = musicBee;
            this.store = store;
            this.placeholders = placeholders;
        }

        // Continuations deliberately resume on the caller's (UI) thread, so MusicBee API calls
        // that change the library or playlists happen there. Only file writing and the library
        // scan run on the thread pool.
        public async Task<ImportResult> ImportAsync(ImportSource source, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var result = new ImportResult { Name = source.Name };

            // Read the version first: if the playlist changes mid-import, the next sync notices
            var snapshot = source.IsLikedSongs ? null : await TryGetSnapshotAsync(source.Id, ct);
            var entries = await FetchAsync(source, progress, ct);
            var materialized = await MaterializeAsync(entries, result, progress, ct);
            result.Tracks = materialized.Files.Count;

            progress?.Report(new ImportProgress("Updating the MusicBee playlist…"));
            var name = PlaceholderWriter.SafeName(source.Name);
            var existing = store.GetPlaylist(source.Id);
            var playlistUrl = existing?.MusicBeePlaylistUrl;
            if (!musicBee.PlaylistExists(playlistUrl))
                playlistUrl = musicBee.FindPlaylist(PlaylistFolder, name);
            if (playlistUrl == null)
                playlistUrl = musicBee.CreatePlaylist(PlaylistFolder, name, materialized.Files.ToArray());
            else
                musicBee.SetPlaylistFiles(playlistUrl, materialized.Files.ToArray());

            store.SetPlaylist(new PlaylistRecord
            {
                SpotifyId = source.Id,
                Name = source.Name,
                MusicBeeName = musicBee.GetPlaylistName(playlistUrl) ?? name,
                MusicBeePlaylistUrl = playlistUrl,
                LastImportedUtc = DateTime.UtcNow,
                LastSyncedUtc = DateTime.UtcNow,
                TrackIds = materialized.TrackIds,
                SnapshotId = snapshot,
                Unmatched = new List<string>(),
            });
            store.Save();

            progress?.Report(new ImportProgress($"Imported \"{source.Name}\": {result.Summary}"));
            return result;
        }

        public async Task<List<(Track Track, bool IsLocal)>> FetchAsync(ImportSource source, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            progress?.Report(new ImportProgress($"Reading \"{source.Name}\" from Spotify…"));
            var fetchProgress = new Progress<int>(n => progress?.Report(new ImportProgress($"Reading \"{source.Name}\" from Spotify… {n} tracks")));
            return source.IsLikedSongs
                ? (await spotify.GetSavedTracksAsync(fetchProgress, ct)).Select(s => (s.Track, false)).ToList()
                : (await spotify.GetPlaylistItemsAsync(source.Id, fetchProgress, ct)).Select(i => (i.Track, i.IsLocal)).ToList();
        }

        /// <summary>
        /// Resolves Spotify entries to MusicBee files: the user's own copy when there is one,
        /// otherwise a placeholder (created and added to the library if missing).
        /// </summary>
        public async Task<Materialized> MaterializeAsync(IList<(Track Track, bool IsLocal)> entries, ImportResult result,
            IProgress<ImportProgress> progress, CancellationToken ct)
        {
            result ??= new ImportResult();
            if (matcher == null)
            {
                progress?.Report(new ImportProgress("Scanning your MusicBee library for local copies…"));
                matcher = await Task.Run(() => new LocalMatcher(
                    musicBee.GetMusicTracks().Where(t => !placeholders.IsPlaceholderPath(t.Path)).ToList()), ct);
            }

            // Pass 1: resolve every entry to a record, noting which need a placeholder written
            var ordered = new List<(TrackRecord Record, Track Track)>();
            var needPlaceholder = new Dictionary<string, (TrackRecord Record, Track Track)>(StringComparer.Ordinal);
            var looseLocalFiles = new List<(int Index, string Path)>();

            foreach (var (track, isLocal) in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (track == null)
                {
                    result.SkippedUnavailable++;
                    continue;
                }
                if (!track.IsTrack)
                {
                    result.SkippedEpisodes++;
                    continue;
                }
                if (isLocal || track.IsLocal)
                {
                    // A file from the user's computer added to Spotify: it has no Spotify ID, but may be in MusicBee
                    var local = matcher.Match(track);
                    if (local != null)
                        looseLocalFiles.Add((ordered.Count + looseLocalFiles.Count, local.Path));
                    else
                        result.SkippedSpotifyLocalFiles++;
                    continue;
                }
                if (string.IsNullOrEmpty(track.Id))
                {
                    result.SkippedUnavailable++;
                    continue;
                }

                var record = store.GetOrAddTrack(track.Id);
                UpdateMetadata(record, track);

                if (!record.LocalPathPinned)
                    record.LocalPath = matcher.Match(track)?.Path ?? KeepIfStillPresent(record.LocalPath);
                if (!string.IsNullOrEmpty(record.LocalPath) && !File.Exists(record.LocalPath))
                    record.LocalPath = null;

                if (!string.IsNullOrEmpty(record.LocalPath))
                    result.LocalMatches++;
                else if (!string.IsNullOrEmpty(record.PlaceholderPath) && File.Exists(record.PlaceholderPath))
                    result.PlaceholdersReused++;
                else
                    needPlaceholder[track.Id] = (record, track);

                ordered.Add((record, track));
            }

            // Pass 2: write missing placeholders in parallel (artwork downloads dominate), then add them to MusicBee
            if (needPlaceholder.Count > 0)
            {
                var written = 0;
                var total = needPlaceholder.Count;
                progress?.Report(new ImportProgress($"Creating placeholders… 0 of {total}", 0, total));
                using var gate = new SemaphoreSlim(ParallelPlaceholderWrites);
                var tasks = needPlaceholder.Values.Select(async pending =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var path = await placeholders.CreateAsync(pending.Track, ct).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref written);
                        progress?.Report(new ImportProgress($"Creating placeholders… {n} of {total}", n, total));
                        return (pending.Record, Path: path);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();
                var created = await Task.WhenAll(tasks);

                progress?.Report(new ImportProgress("Adding placeholders to your MusicBee library…"));
                foreach (var (record, path) in created)
                {
                    record.PlaceholderPath = musicBee.AddToLibrary(path);
                    result.PlaceholdersCreated++;
                }
            }
            store.InvalidatePathIndex();

            var materialized = new Materialized();
            foreach (var (record, _) in ordered)
            {
                materialized.TrackIds.Add(record.SpotifyId);
                materialized.Files.Add(record.PreferredPath);
                materialized.FileById[record.SpotifyId] = record.PreferredPath;
            }
            foreach (var (index, path) in looseLocalFiles)
                materialized.Files.Insert(Math.Min(index, materialized.Files.Count), path);
            return materialized;
        }

        /// <summary>
        /// A local file paired by searching Spotify (see TrackResolver) may not match the name-based
        /// library scan; keep that pairing rather than dropping it.
        /// </summary>
        private static string KeepIfStillPresent(string path) =>
            !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;

        private async Task<string> TryGetSnapshotAsync(string playlistId, CancellationToken ct)
        {
            try
            {
                return (await spotify.GetPlaylistAsync(playlistId, ct))?.SnapshotId;
            }
            catch (SpotifyApiException)
            {
                return null;   // only used to detect changes later; not worth failing the import
            }
        }

        internal static void UpdateMetadata(TrackRecord record, Track track)
        {
            record.Uri = track.Uri;
            record.Title = track.Name;
            record.Artist = track.ArtistNames;
            record.Album = track.AlbumName;
            record.DurationMs = track.DurationMs;
            record.Isrc = track.ExternalIds?.Isrc;
        }
    }
}
