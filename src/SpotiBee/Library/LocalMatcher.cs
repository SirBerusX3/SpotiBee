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
    /// Also holds the title and artist normalisation shared with <see cref="TrackResolver"/>.
    /// </summary>
    public sealed class LocalMatcher
    {
        private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(4);

        private static readonly Regex Featuring = new Regex(
            @"[\(\[]\s*(feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s(feat\.?|ft\.?|featuring)\s.*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ArtistSeparators = new Regex(
            @"\s*(;|,|/|&|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bvs\.?\b|\band\b|\bx\b)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Separators that split a list of different artists, but not a band name like "Sly & The Family Stone"
        private static readonly Regex ArtistListSeparators = new Regex(
            @"\s*(;|\bfeat\.?\s|\bft\.?\s|\bfeaturing\b|\bvs\.?\s)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Charlie "Bird" Parker, Damian "Jr. Gong" Marley, UGK (Underground Kingz)
        private static readonly Regex Nicknames = new Regex(
            "\\s*([\"“”][^\"“”]*[\"“”]|\\([^\\)]*\\)|\\[[^\\]]*\\])\\s*",
            RegexOptions.CultureInvariant);
        private static readonly Regex InnermostBrackets = new Regex(@"[\(\[]([^\(\)\[\]]*)[\)\]]", RegexOptions.CultureInvariant);
        private static readonly Regex LabelTokens = new Regex(@"[a-z0-9]+", RegexOptions.CultureInvariant);

        // Words that only describe which release or master a recording came from. A bracket or " - "
        // suffix made only of these (and years) is dropped. Deliberately absent: live, remix, acoustic,
        // demo, extended, instrumental: those are different recordings.
        private static readonly HashSet<string> LabelWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "remaster", "remastered", "remasters", "mastered", "master", "digital", "digitally",
            "album", "single", "lp", "version", "original", "explicit", "clean", "censored", "uncensored",
            "mono", "stereo", "radio", "edit", "mix", "bonus", "track", "deluxe", "edition", "expanded",
            "and", "from",
        };
        private static readonly HashSet<string> CoreLabelWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "remaster", "remastered", "remasters", "mastered", "master", "version", "explicit", "clean",
            "censored", "uncensored", "mono", "stereo", "edit", "mix", "bonus",
        };

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

        // --- Titles --------------------------------------------------------

        /// <summary>A title with "feat." and release labels removed, still readable, for search queries.</summary>
        public static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";
            return Regex.Replace(StripVersionLabels(Featuring.Replace(title, "")), @"\s{2,}", " ").Trim();
        }

        /// <summary>
        /// Readable title variants to try: the clean title and, for tags like
        /// "Led Zeppelin II - Whole Lotta Love", the part after the album prefix.
        /// </summary>
        public static IEnumerable<string> TitleVariants(string title)
        {
            var clean = CleanTitle(title);
            if (clean.Length > 0)
                yield return clean;
            var dash = clean.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash + 3 < clean.Length)
                yield return clean.Substring(dash + 3).Trim();
        }

        internal static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";
            return Simplify(StripVersionLabels(Featuring.Replace(title, "")));
        }

        /// <summary>
        /// Removes brackets and " - " suffixes that only name a release or master, e.g.
        /// "(Album Version (Explicit))", "(Single Version / Mono)", " - 2009 Remaster".
        /// Keeps ones that name a different recording, e.g. "(Bayside Boys Remix)" or " - Live".
        /// </summary>
        internal static string StripVersionLabels(string title)
        {
            var t = title;
            // Innermost brackets first, so "(Album Version (Explicit))" is handled in two steps.
            // Brackets that aren't labels are hidden behind placeholders so the loop ends.
            var kept = new List<string>();
            while (true)
            {
                var m = InnermostBrackets.Match(t);
                if (!m.Success)
                    break;
                string replacement;
                if (IsVersionLabel(m.Groups[1].Value))
                    replacement = " ";
                else
                {
                    kept.Add(m.Value);
                    replacement = "\u0001" + (kept.Count - 1) + "\u0002";
                }
                t = t.Substring(0, m.Index) + replacement + t.Substring(m.Index + m.Length);
            }
            for (var i = 0; i < kept.Count; i++)
                t = t.Replace("\u0001" + i + "\u0002", kept[i]);

            // " - 2009 Remaster", " - Single Version", possibly stacked
            while (true)
            {
                var dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash <= 0 || !IsVersionLabel(t.Substring(dash + 3)))
                    break;
                t = t.Substring(0, dash);
            }
            return t.Trim();
        }

        private static bool IsVersionLabel(string text)
        {
            var tokens = LabelTokens.Matches(text.ToLowerInvariant()).Cast<Match>().Select(m => m.Value).ToList();
            if (tokens.Count == 0)
                return false;
            var core = false;
            foreach (var token in tokens)
            {
                var isYear = token.Length == 4 && token.All(char.IsDigit) && (token[0] == '1' || token[0] == '2');
                if (isYear)
                    continue;
                if (!LabelWords.Contains(token))
                    return false;
                core |= CoreLabelWords.Contains(token);
            }
            return core;
        }

        // --- Artists -------------------------------------------------------

        /// <summary>The first artist of a MusicBee artist tag ("A; B", "A feat. B"), still readable.</summary>
        public static string FirstArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
                return "";
            return ArtistSeparators.Split(artist.Split('\0')[0])[0].Trim();
        }

        /// <summary>
        /// The main artist for a search query: the whole band name ("Sly &amp; The Family Stone"),
        /// minus featured artists and nicknames ("Charlie \"Bird\" Parker" → "Charlie Parker").
        /// </summary>
        public static string SearchArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
                return "";
            var first = ArtistListSeparators.Split(artist.Split('\0')[0])[0];
            return Regex.Replace(Nicknames.Replace(first, " "), @"\s{2,}", " ").Trim();
        }

        internal static string PrimaryArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
                return "";
            // MusicBee stores multiple artists separated by NUL or "; "
            var first = Nicknames.Replace(artist.Split('\0')[0], " ");
            var simplified = Simplify(ArtistSeparators.Split(first.Trim())[0]);
            // "The Beatles" vs "Beatles"
            return simplified.StartsWith("the", StringComparison.Ordinal) && simplified.Length > 3
                ? simplified.Substring(3)
                : simplified;
        }

        /// <summary>
        /// True if the artists plausibly name the same act: equal primary artists, or one full name
        /// containing the other ("Joe Turner" / "Big Joe Turner", "Lester Flatt" / "Lester Flatt &amp; Earl Scruggs").
        /// </summary>
        public static bool ArtistsMatch(IEnumerable<string> localArtists, IEnumerable<string> spotifyArtists)
        {
            var local = localArtists.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            var remote = spotifyArtists.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            var localKeys = new HashSet<string>(local.Select(PrimaryArtist).Where(k => k.Length > 0));
            if (remote.Select(PrimaryArtist).Any(localKeys.Contains))
                return true;
            var localFull = local.Select(a => Simplify(SearchArtist(a))).Where(k => k.Length >= 5).ToList();
            var remoteFull = remote.Select(a => Simplify(SearchArtist(a))).Where(k => k.Length >= 5).ToList();
            return localFull.Any(l => remoteFull.Any(r => l.Contains(r) || r.Contains(l)));
        }

        /// <summary>Lowercase, strip accents, "&amp;" to "and", keep only letters and digits.</summary>
        internal static string Simplify(string text)
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
