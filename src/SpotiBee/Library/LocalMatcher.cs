using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    /// <summary>
    /// Finds the user's own copy of a Spotify track by primary artist + title, confirmed by duration.
    /// MusicBee's API doesn't expose ISRC, so this is name-based; the duration check keeps
    /// live/edit/extended versions from matching the wrong recording.
    /// </summary>
    public sealed class LocalMatcher
    {
        private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(4);

        // "(feat. X)", "[with X]", "- 2011 Remaster", "(Remastered 2009)", "- Mono Version" and similar.
        // Deliberately not stripped: live, remix, acoustic, demo, edit. Those are different recordings.
        private static readonly Regex Featuring = new Regex(
            @"[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s(feat\.?|ft\.?|featuring)\s.*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Remaster = new Regex(
            @"\s-\s[^-]*\b(remaster(ed)?|mono|stereo)\b[^-]*$|[\(\[][^\)\]]*\b(remaster(ed)?|mono version|stereo version)\b[^\)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ArtistSeparators = new Regex(
            @"\s*(;|,|/|&|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bvs\.?\b|\band\b|\bx\b)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, List<LocalTrack>> index = new Dictionary<string, List<LocalTrack>>(StringComparer.Ordinal);

        public LocalMatcher(IEnumerable<LocalTrack> tracks)
        {
            foreach (var track in tracks)
            {
                var title = NormalizeTitle(track.Title);
                if (title.Length == 0)
                    continue;
                // Index under both the track artist and album artist so compilations still match
                foreach (var artist in new[] { track.Artist, track.AlbumArtist }
                             .Select(PrimaryArtist).Where(a => a.Length > 0).Distinct())
                {
                    var key = artist + "|" + title;
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = new List<LocalTrack>();
                    if (!list.Contains(track))
                        list.Add(track);
                }
                Count++;
            }
        }

        public int Count { get; }

        public LocalTrack Match(Track spotify) =>
            Match(spotify.Artists?.Select(a => a.Name) ?? Enumerable.Empty<string>(),
                  spotify.Name, spotify.Album?.Name, TimeSpan.FromMilliseconds(spotify.DurationMs));

        public LocalTrack Match(IEnumerable<string> artists, string title, string album, TimeSpan duration)
        {
            var normalizedTitle = NormalizeTitle(title);
            if (normalizedTitle.Length == 0)
                return null;
            var normalizedAlbum = NormalizeTitle(album);

            var candidates = artists
                .Select(PrimaryArtist)
                .Where(a => a.Length > 0)
                .SelectMany(a => index.TryGetValue(a + "|" + normalizedTitle, out var list) ? list : Enumerable.Empty<LocalTrack>())
                .Distinct()
                .Where(c => duration <= TimeSpan.Zero || c.Duration == null ||
                            (c.Duration.Value - duration).Duration() <= DurationTolerance);

            // Same album first, then the closest length
            return candidates
                .OrderByDescending(c => normalizedAlbum.Length > 0 && NormalizeTitle(c.Album) == normalizedAlbum)
                .ThenBy(c => c.Duration.HasValue && duration > TimeSpan.Zero ? (c.Duration.Value - duration).Duration() : TimeSpan.MaxValue)
                .FirstOrDefault();
        }

        /// <summary>A title with "feat." and remaster suffixes removed, still readable, for use in search queries.</summary>
        public static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";
            return Remaster.Replace(Featuring.Replace(title, ""), "").Trim();
        }

        /// <summary>The first artist of a MusicBee artist tag ("A; B", "A feat. B"), still readable.</summary>
        public static string FirstArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
                return "";
            return ArtistSeparators.Split(artist.Split('\0')[0])[0].Trim();
        }

        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";
            var t = Featuring.Replace(title, "");
            t = Remaster.Replace(t, "");
            return Simplify(t);
        }

        internal static string PrimaryArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
                return "";
            // MusicBee stores multiple artists separated by NUL or "; "
            var first = artist.Split('\0')[0];
            var simplified = Simplify(ArtistSeparators.Split(first)[0]);
            // "The Beatles" vs "Beatles"
            return simplified.StartsWith("the", StringComparison.Ordinal) && simplified.Length > 3
                ? simplified.Substring(3)
                : simplified;
        }

        /// <summary>Lowercase, strip accents, "&amp;" to "and", keep only letters and digits.</summary>
        private static string Simplify(string text)
        {
            var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
