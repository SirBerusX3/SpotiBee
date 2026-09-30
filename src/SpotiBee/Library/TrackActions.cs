using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    /// <summary>
    /// Turns Spotify tracks picked in search into MusicBee files so they can be played, queued or
    /// added to playlists: the user's own copy if they have it, otherwise a placeholder.
    /// Keep one instance per window so the library scan happens only once.
    /// </summary>
    public sealed class TrackActions
    {
        private readonly TrackStore store;
        private readonly PlaylistImporter importer;

        public TrackActions(ISpotifyPlaylists spotify, IMusicBeeLibrary musicBee, TrackStore store, PlaceholderWriter placeholders)
        {
            this.store = store;
            importer = new PlaylistImporter(spotify, musicBee, store, placeholders);
        }

        /// <summary>MusicBee files for the tracks, in the same order. Tracks that can't be represented are left out.</summary>
        public async Task<string[]> ToFilesAsync(IList<Track> tracks, IProgress<ImportProgress> progress, CancellationToken ct)
        {
            var entries = tracks.Where(t => t?.Id != null && t.IsTrack && !t.IsLocal)
                                .GroupBy(t => t.Id).Select(g => (g.First(), false)).ToList();
            var materialized = await importer.MaterializeAsync(entries, null, progress, ct);
            store.Save();
            return tracks.Where(t => t?.Id != null)
                         .Select(t => materialized.FileById.TryGetValue(t.Id, out var file) ? file : null)
                         .Where(f => f != null)
                         .ToArray();
        }

        /// <summary>True if the user already has this track as a local file or placeholder.</summary>
        public bool IsInLibrary(Track track, out bool isLocal)
        {
            var record = track?.Id == null ? null : store.GetTrack(track.Id);
            isLocal = !string.IsNullOrEmpty(record?.LocalPath) && System.IO.File.Exists(record.LocalPath);
            return isLocal || (!string.IsNullOrEmpty(record?.PlaceholderPath) && System.IO.File.Exists(record.PlaceholderPath));
        }
    }
}
