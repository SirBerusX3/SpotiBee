using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    public sealed class Lyrics
    {
        /// <summary>Timed lines, in order; empty for unsynchronised lyrics.</summary>
        public List<(TimeSpan Time, string Text)> Lines { get; } = new List<(TimeSpan, string)>();
        public string Plain { get; set; }
        public bool Instrumental { get; set; }
        public string Source { get; set; }
        public bool IsSynced => Lines.Count > 0;

        /// <summary>The text as stored in a file's lyrics tag: LRC when synced, otherwise plain.</summary>
        public string Raw { get; set; }

        /// <summary>Index of the line being sung at this position, or -1 before the first line.</summary>
        public int LineAt(TimeSpan position)
        {
            var index = -1;
            for (var i = 0; i < Lines.Count && Lines[i].Time <= position; i++)
                index = i;
            return index;
        }

        private static readonly Regex Timestamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.CultureInvariant);
        private static readonly Regex Metadata = new Regex(@"^\[(ar|ti|al|au|by|length|offset|re|ve|id):[^\]]*\]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Parses LRC ("[01:23.45] line", several timestamps per line allowed) or plain text.</summary>
        public static Lyrics Parse(string text, string source)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var lyrics = new Lyrics { Source = source, Raw = text };
            var plain = new List<string>();
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (Metadata.IsMatch(rawLine))
                    continue;
                var stamps = Timestamp.Matches(rawLine).Cast<Match>().ToList();
                // Only leading timestamps count; later brackets are part of the words
                var leading = new List<Match>();
                var pos = 0;
                foreach (var m in stamps)
                {
                    if (rawLine.Substring(pos, m.Index - pos).Trim().Length > 0)
                        break;
                    leading.Add(m);
                    pos = m.Index + m.Length;
                }
                var words = rawLine.Substring(pos).Trim();
                if (leading.Count == 0)
                {
                    plain.Add(rawLine.TrimEnd());
                    continue;
                }
                foreach (var m in leading)
                {
                    var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var seconds = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    lyrics.Lines.Add((TimeSpan.FromSeconds(minutes * 60 + seconds), words));
                }
                plain.Add(words);
            }
            lyrics.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            lyrics.Plain = string.Join("\n", plain).Trim('\n');
            return lyrics.Plain.Length == 0 && !lyrics.IsSynced ? null : lyrics;
        }
    }

    /// <summary>
    /// Finds lyrics for a Spotify track: lyrics saved with the user's file or placeholder first,
    /// then LRCLIB (lrclib.net, free, often time-synced). Lyrics found online are saved into the
    /// placeholder so they're instant and offline next time. Never writes to the user's own files.
    /// </summary>
    public sealed class LyricsService
    {
        private const string LrcLib = "https://lrclib.net/api/";
        private static readonly TimeSpan SearchLengthTolerance = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

        private readonly HttpClient http;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackStore store;
        private readonly Dictionary<string, Lyrics> found = new Dictionary<string, Lyrics>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> missing = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public LyricsService(HttpClient http, IMusicBeeLibrary musicBee, TrackStore store)
        {
            this.http = http;
            this.musicBee = musicBee;
            this.store = store;
        }

        /// <summary>Lyrics for the track, or null if none could be found.</summary>
        public async Task<Lyrics> GetAsync(Track track, CancellationToken ct)
        {
            if (track?.Id == null)
                return null;
            lock (found)
            {
                if (found.TryGetValue(track.Id, out var cached))
                    return cached;
                if (missing.TryGetValue(track.Id, out var when) && DateTime.UtcNow - when < RetryAfterFailure)
                    return null;
            }

            var record = store.GetTrack(track.Id);
            var saved = FromMusicBee(record?.LocalPath) ?? FromMusicBee(record?.PlaceholderPath);
            var lyrics = saved;
            var transientFailure = false;

            // Saved synced lyrics are the best there is. Saved plain lyrics are kept as a fallback,
            // but LRCLIB often has a synced version of the same song, which follows along as it plays.
            if (saved == null || (!saved.IsSynced && !saved.Instrumental))
            {
                try
                {
                    var online = await FromLrcLibAsync(track, ct);
                    if (online != null && (saved == null || online.IsSynced))
                    {
                        lyrics = online;
                        SaveToPlaceholder(record, online.Raw);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    transientFailure = saved == null;
                    Diagnostics.Log("Lyrics lookup for " + track.Name, ex);
                }
            }

            lock (found)
            {
                if (lyrics != null)
                    found[track.Id] = lyrics;
                else if (transientFailure)
                    missing[track.Id] = DateTime.UtcNow;
                else
                    missing[track.Id] = DateTime.MaxValue - TimeSpan.FromDays(1);   // definitely none; don't ask again this session
            }
            return lyrics;
        }

        /// <summary>
        /// MusicBee downloaded lyrics while playing a placeholder: keep them in the file so they're
        /// there next time, even when Spotify plays the track outside MusicBee.
        /// </summary>
        public void SaveDownloaded(string placeholderPath, string text)
        {
            var record = store.FindByPath(placeholderPath);
            if (record == null || !string.Equals(record.PlaceholderPath, placeholderPath, StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.IsNullOrWhiteSpace(musicBee.GetLyrics(placeholderPath)))
                return;
            SaveToPlaceholder(record, text);
            var parsed = Lyrics.Parse(text, "MusicBee");
            if (parsed != null)
                lock (found)
                    found[record.SpotifyId] = parsed;
        }

        private Lyrics FromMusicBee(string file)
        {
            if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
                return null;
            try
            {
                return Lyrics.Parse(musicBee.GetLyrics(file), "MusicBee");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Reading saved lyrics", ex);
                return null;
            }
        }

        private void SaveToPlaceholder(TrackRecord record, string text)
        {
            var path = record?.PlaceholderPath;
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(text) || !System.IO.File.Exists(path))
                return;
            // Never replace synced lyrics already in the placeholder with anything else
            var existing = Lyrics.Parse(musicBee.GetLyrics(path), "MusicBee");
            if (existing?.IsSynced == true)
                return;
            try
            {
                musicBee.SetLyrics(path, text);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Saving lyrics to placeholder", ex);
            }
        }

        private async Task<Lyrics> FromLrcLibAsync(Track track, CancellationToken ct)
        {
            var artist = LocalMatcher.SearchArtist(track.Artists?.FirstOrDefault()?.Name ?? "");
            var title = LocalMatcher.CleanTitle(track.Name);
            var seconds = (int)Math.Round(track.DurationMs / 1000.0);

            // Exact lookup first: LRCLIB matches on title, artist, album and length (±2 s)
            var exact = await GetJsonAsync<LrcLibRecord>(LrcLib + "get?" + Query(
                ("artist_name", artist), ("track_name", title), ("album_name", track.AlbumName), ("duration", seconds.ToString())), ct);
            if (exact != null)
                return ToLyrics(exact);

            // Then a search, keeping only the same song at about the same length
            var results = await GetJsonAsync<LrcLibRecord[]>(LrcLib + "search?" + Query(("track_name", title), ("artist_name", artist)), ct);
            var titleKey = LocalMatcher.NormalizeTitle(title);
            var best = (results ?? new LrcLibRecord[0])
                .Where(r => LocalMatcher.NormalizeTitle(r.TrackName) == titleKey &&
                            LocalMatcher.ArtistsMatch(new[] { r.ArtistName }, (track.Artists ?? new Artist[0]).Select(a => a.Name)) &&
                            Math.Abs(r.Duration - seconds) <= SearchLengthTolerance.TotalSeconds &&
                            (r.Instrumental || !string.IsNullOrWhiteSpace(r.PlainLyrics) || !string.IsNullOrWhiteSpace(r.SyncedLyrics)))
                .OrderByDescending(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
                .ThenBy(r => Math.Abs(r.Duration - seconds))
                .FirstOrDefault();
            return best == null ? null : ToLyrics(best);
        }

        private static Lyrics ToLyrics(LrcLibRecord r)
        {
            if (r.Instrumental)
                return new Lyrics { Instrumental = true, Source = "LRCLIB", Plain = "", Raw = "[Instrumental]" };
            var raw = !string.IsNullOrWhiteSpace(r.SyncedLyrics) ? r.SyncedLyrics : r.PlainLyrics;
            return Lyrics.Parse(raw, "LRCLIB");
        }

        /// <summary>GET and parse JSON; null on 404. Throws on other failures (including LRCLIB's 503 "busy").</summary>
        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return Json.TryParse<T>(body, out var value) ? value : null;
        }

        private static string Query(params (string Key, string Value)[] pairs) =>
            string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Value))
                                  .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

        [DataContract]
        private sealed class LrcLibRecord
        {
            [DataMember(Name = "trackName")] public string TrackName { get; set; }
            [DataMember(Name = "artistName")] public string ArtistName { get; set; }
            [DataMember(Name = "albumName")] public string AlbumName { get; set; }
            [DataMember(Name = "duration")] public double Duration { get; set; }
            [DataMember(Name = "instrumental")] public bool Instrumental { get; set; }
            [DataMember(Name = "plainLyrics")] public string PlainLyrics { get; set; }
            [DataMember(Name = "syncedLyrics")] public string SyncedLyrics { get; set; }
        }
    }
}
