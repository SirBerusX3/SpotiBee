using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    /// <summary>
    /// Works out which Spotify track a MusicBee file is: from SpotiBee's own records, a placeholder's
    /// SPOTIFY_URI tag, or by searching Spotify for a local file (title + artist, confirmed by length).
    /// </summary>
    public sealed class TrackResolver
    {
        /// <summary>Don't search again for a file Spotify didn't have until this long has passed.</summary>
        private static readonly TimeSpan UnmatchedRetry = TimeSpan.FromDays(7);

        private readonly ISpotifySearch spotify;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;

        public TrackResolver(ISpotifySearch spotify, IMusicBeeLibrary musicBee, TrackStore store)
        {
            this.spotify = spotify;
            this.musicBee = musicBee;
            this.store = store;
        }

        /// <summary>Returns the Spotify track ID, or null if Spotify doesn't have this file's track.</summary>
        public async Task<string> ResolveAsync(string file, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(file))
                return null;

            var known = store.FindByPath(file);
            if (known != null)
                return known.SpotifyId;

            var uri = ReadSpotifyUriTag(file);
            if (uri != null)
                return IdFromUri(uri);

            if (store.WasRecentlyUnmatched(file, UnmatchedRetry))
                return null;

            var local = musicBee.GetTrack(file);
            if (local == null || string.IsNullOrWhiteSpace(local.Title))
                return null;

            var match = await SearchAsync(local, ct);
            if (match == null)
            {
                store.SetUnmatched(file, true);
                return null;
            }

            // Remember the pairing so playback routing and future syncs know this file is that track
            var record = store.GetOrAddTrack(match.Id);
            record.Uri = match.Uri;
            record.Title = match.Name;
            record.Artist = match.ArtistNames;
            record.Album = match.AlbumName;
            record.DurationMs = match.DurationMs;
            record.Isrc = match.ExternalIds?.Isrc;
            if (!record.LocalPathPinned)
                record.LocalPath = file;
            store.SetUnmatched(file, false);
            store.InvalidatePathIndex();
            return match.Id;
        }

        private async Task<Track> SearchAsync(LocalTrack local, CancellationToken ct)
        {
            var title = LocalMatcher.CleanTitle(local.Title);
            var artist = LocalMatcher.FirstArtist(string.IsNullOrWhiteSpace(local.Artist) ? local.AlbumArtist : local.Artist);
            // A matcher over just this one file answers "is this Spotify track that file?"
            var matcher = new LocalMatcher(new[] { local });

            var queries = new List<string>();
            if (artist.Length > 0)
                queries.Add($"track:\"{Quote(title)}\" artist:\"{Quote(artist)}\"");
            queries.Add((artist + " " + title).Trim());

            foreach (var query in queries.Distinct())
            {
                var candidates = await spotify.SearchTracksAsync(query, 10, ct);
                var matching = candidates.Where(c => c.Id != null && matcher.Match(c) != null).ToList();
                if (matching.Count == 0)
                    continue;
                // Prefer the same album, then the closest length
                var album = LocalMatcher.NormalizeTitle(local.Album);
                return matching
                    .OrderByDescending(c => album.Length > 0 && LocalMatcher.NormalizeTitle(c.Album?.Name) == album)
                    .ThenBy(c => local.Duration.HasValue ? Math.Abs(c.DurationMs - local.Duration.Value.TotalMilliseconds) : 0)
                    .First();
            }
            return null;
        }

        private static string Quote(string s) => s.Replace("\"", "");

        public static string IdFromUri(string uri) =>
            uri != null && uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri.Substring("spotify:track:".Length) : null;

        /// <summary>Placeholders carry SPOTIFY_URI in their Opus tags, near the start of the file.</summary>
        internal static string ReadSpotifyUriTag(string file)
        {
            if (!file.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                var head = new byte[16384];
                int read;
                using (var fs = File.OpenRead(file))
                    read = fs.Read(head, 0, head.Length);
                var text = Encoding.UTF8.GetString(head, 0, read);
                const string key = "SPOTIFY_URI=spotify:track:";
                var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    return null;
                var start = at + "SPOTIFY_URI=".Length;
                var end = start;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == ':'))
                    end++;
                return text.Substring(start, end - start);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>The Spotify search the resolver needs, separated so it can be tested offline.</summary>
    public interface ISpotifySearch
    {
        Task<Track[]> SearchTracksAsync(string query, int limit = 10, CancellationToken ct = default);
    }
}
