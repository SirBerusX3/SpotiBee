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

    /// <summary>
    /// Turns a Spotify playlist into a MusicBee playlist in the "Spotify" folder. Tracks the user
    /// owns locally use the local file; everything else gets a silent placeholder added to the library.
    /// Re-importing updates the same MusicBee playlist in place.
    /// </summary>
    public sealed class PlaylistImporter
    {
        public const string PlaylistFolder = "Spotify";
        private const int ParallelPlaceholderWrites = 4;

        private readonly SpotifyClient spotify;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;
        private readonly PlaceholderWriter placeholders;
        private LocalMatcher matcher;

        public PlaylistImporter(SpotifyClient spotify, IMusicBeeLibrary musicBee, TrackStore store, PlaceholderWriter placeholders)
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

            progress?.Report(new ImportProgress($"Reading \"{source.Name}\" from Spotify…"));
            var fetchProgress = new Progress<int>(n => progress?.Report(new ImportProgress($"Reading \"{source.Name}\" from Spotify… {n} tracks")));
            var entries = source.IsLikedSongs
                ? (await spotify.GetSavedTracksAsync(fetchProgress, ct)).Select(s => (Track: s.Track, IsLocal: false)).ToList()
                : (await spotify.GetPlaylistItemsAsync(source.Id, fetchProgress, ct)).Select(i => (i.Track, i.IsLocal)).ToList();

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
                    record.LocalPath = matcher.Match(track)?.Path;
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

            // Pass 3: write the MusicBee playlist in Spotify's order
            var files = ordered.Select(o => o.Record.PreferredPath).ToList();
            foreach (var (index, path) in looseLocalFiles)
                files.Insert(Math.Min(index, files.Count), path);
            result.Tracks = files.Count;

            progress?.Report(new ImportProgress("Updating the MusicBee playlist…"));
            var name = PlaceholderWriter.SafeName(source.Name);
            var existing = store.GetPlaylist(source.Id);
            var playlistUrl = existing?.MusicBeePlaylistUrl;
            if (!musicBee.PlaylistExists(playlistUrl))
                playlistUrl = musicBee.FindPlaylist(PlaylistFolder, name);
            if (playlistUrl == null)
                playlistUrl = musicBee.CreatePlaylist(PlaylistFolder, name, files.ToArray());
            else
                musicBee.SetPlaylistFiles(playlistUrl, files.ToArray());

            store.SetPlaylist(new PlaylistRecord
            {
                SpotifyId = source.Id,
                Name = source.Name,
                MusicBeePlaylistUrl = playlistUrl,
                LastImportedUtc = DateTime.UtcNow,
                TrackIds = ordered.Select(o => o.Record.SpotifyId).ToList(),
                SnapshotId = source.IsLikedSongs ? null : (await TryGetSnapshotAsync(source.Id, ct)),
            });
            store.Save();

            progress?.Report(new ImportProgress($"Imported \"{source.Name}\": {result.Summary}"));
            return result;
        }

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

        private static void UpdateMetadata(TrackRecord record, Track track)
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
