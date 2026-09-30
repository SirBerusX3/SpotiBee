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

        /// <summary>Bumped when matching improves, so earlier misses are searched again.</summary>
        internal const int MatcherVersion = 2;

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

            if (store.WasRecentlyUnmatched(file, UnmatchedRetry, MatcherVersion))
                return null;

            var local = musicBee.GetTrack(file);
            if (local == null || string.IsNullOrWhiteSpace(local.Title))
                return null;

            var (match, lengthDiffers) = await SearchAsync(local, ct);
            if (match == null)
            {
                store.SetUnmatched(file, true, MatcherVersion);
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
            {
                record.LocalPath = file;
                record.LocalLengthDiffers = lengthDiffers;
            }
            store.SetUnmatched(file, false, MatcherVersion);
            store.InvalidatePathIndex();
            return match.Id;
        }

        /// <summary>Same recording: lengths agree this closely.</summary>
        internal static readonly TimeSpan ExactLength = TimeSpan.FromSeconds(4);

        /// <summary>
        /// Same song, different edit (compilation versions are often trimmed or faded early):
        /// good enough for a playlist, but not for using the local file as Spotify's clock.
        /// </summary>
        internal static TimeSpan CloseLength(TimeSpan length) =>
            TimeSpan.FromSeconds(Math.Max(25, length.TotalSeconds * 0.12));

        private async Task<(Track Track, bool LengthDiffers)> SearchAsync(LocalTrack local, CancellationToken ct)
        {
            var titles = LocalMatcher.TitleVariants(local.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var titleKeys = new HashSet<string>(titles.Select(LocalMatcher.NormalizeTitle).Where(k => k.Length > 0));
            var artists = new[] { local.Artist, local.AlbumArtist }.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            var artist = LocalMatcher.SearchArtist(artists.FirstOrDefault() ?? "");

            var queries = new List<string>();
            foreach (var title in titles)
                if (artist.Length > 0)
                    queries.Add($"track:\"{Quote(title)}\" artist:\"{Quote(artist)}\"");
            foreach (var title in titles)
                queries.Add((artist + " " + title).Trim());
            // Last resort for artist tags Spotify spells differently; the artist check below still applies
            queries.Add($"track:\"{Quote(titles.FirstOrDefault() ?? local.Title)}\"");

            var seen = new List<Track>();
            foreach (var query in queries.Distinct())
            {
                var candidates = await spotify.SearchTracksAsync(query, 10, ct);
                seen.AddRange(candidates.Where(c => c?.Id != null && seen.All(s => s.Id != c.Id)));
                var best = Best(local, titleKeys, artists, candidates);
                if (best.Track != null && !best.LengthDiffers)
                    return best;
                if (best.Track != null)
                {
                    // Keep looking for an exact-length version, but remember this one
                    var exact = await FindExactAsync(local, titleKeys, artists, queries.Skip(queries.IndexOf(query) + 1), seen, ct);
                    return exact ?? best;
                }
            }

            Diagnostics.Log($"No Spotify match for \"{local.Artist} – {local.Title}\" ({local.Duration:m\\:ss}). " +
                            "Closest results: " + (seen.Count == 0 ? "none" : string.Join("; ", seen.Take(5).Select(s =>
                                $"{s.ArtistNames} – {s.Name} ({TimeSpan.FromMilliseconds(s.DurationMs):m\\:ss})"))));
            return (null, false);
        }

        private async Task<(Track, bool)?> FindExactAsync(LocalTrack local, HashSet<string> titleKeys, List<string> artists,
            IEnumerable<string> remainingQueries, List<Track> seen, CancellationToken ct)
        {
            foreach (var query in remainingQueries.Distinct().Take(2))
            {
                var candidates = await spotify.SearchTracksAsync(query, 10, ct);
                seen.AddRange(candidates.Where(c => c?.Id != null && seen.All(s => s.Id != c.Id)));
                var best = Best(local, titleKeys, artists, candidates);
                if (best.Track != null && !best.LengthDiffers)
                    return best;
            }
            return null;
        }

        /// <summary>The best candidate: same title and artist, exact length before close length, then same album, then nearest length.</summary>
        internal static (Track Track, bool LengthDiffers) Best(LocalTrack local, HashSet<string> titleKeys,
            IList<string> localArtists, IEnumerable<Track> candidates)
        {
            var album = LocalMatcher.NormalizeTitle(local.Album);
            var scored = candidates
                .Where(c => c?.Id != null && c.IsTrack && !c.IsLocal)
                .Where(c => titleKeys.Contains(LocalMatcher.NormalizeTitle(c.Name)))
                .Where(c => LocalMatcher.ArtistsMatch(localArtists, (c.Artists ?? new Artist[0]).Select(a => a.Name)))
                .Select(c => (Track: c, Diff: local.Duration.HasValue
                    ? (TimeSpan.FromMilliseconds(c.DurationMs) - local.Duration.Value).Duration()
                    : TimeSpan.Zero))
                .Where(x => !local.Duration.HasValue || x.Diff <= CloseLength(local.Duration.Value))
                .OrderByDescending(x => x.Diff <= ExactLength)
                .ThenByDescending(x => album.Length > 0 && LocalMatcher.NormalizeTitle(x.Track.Album?.Name) == album)
                .ThenBy(x => x.Diff)
                .FirstOrDefault();
            return scored.Track == null ? (null, false) : (scored.Track, scored.Diff > ExactLength);
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
