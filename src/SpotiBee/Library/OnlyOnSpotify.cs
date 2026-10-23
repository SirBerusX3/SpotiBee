using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SpotiBee.Library
{
    /// <summary>
    /// Songs in the user's playlists and imported albums that they only have as Spotify placeholders:
    /// a shopping list for anyone who'd rather own them.
    /// </summary>
    public static class OnlyOnSpotify
    {
        public sealed class Entry
        {
            public TrackRecord Record;
            public string Path;
            public string Title;
            public string Artist;
            public string AlbumArtist;
            public string Album;
            public TimeSpan? Duration;

            /// <summary>Playlist names, and "Saved album" for imported albums.</summary>
            public List<string> UsedIn = new List<string>();

            public string SpotifyLink => "https://open.spotify.com/track/" + Record.SpotifyId;
        }

        /// <summary>
        /// The files of every playlist except auto-playlists (which only filter the library, as in
        /// Clean Up), as (name, files). MusicBee's playlist listing is stateful, so call this on its UI thread.
        /// </summary>
        public static List<(string Name, string[] Files)> ReadPlaylists(IMusicBeeLibrary musicBee) =>
            musicBee.GetPlaylists()
                .Where(p => !musicBee.IsAutoPlaylist(p.Url))
                .Select(p => (p.Name, musicBee.GetPlaylistFiles(p.Url)))
                .ToList();

        /// <summary>
        /// Every placeholder still used by one of the playlists or an imported album, whose song the user
        /// doesn't own. Ordered by album artist, album, then file name, which starts with the track number.
        /// Only reads tags from MusicBee, so it can run off the UI thread.
        /// </summary>
        public static List<Entry> Build(TrackStore store, IMusicBeeLibrary musicBee, IEnumerable<(string Name, string[] Files)> playlists)
        {
            var usedIn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Use(string path, string where)
            {
                if (string.IsNullOrEmpty(path))
                    return;
                if (!usedIn.TryGetValue(path, out var list))
                    usedIn[path] = list = new List<string>();
                if (!list.Contains(where))
                    list.Add(where);
            }

            foreach (var (name, files) in playlists)
                foreach (var file in files ?? new string[0])
                    Use(file, name);
            foreach (var album in store.Albums)
                foreach (var id in album.TrackIds ?? new List<string>())
                {
                    var record = store.GetTrack(id);
                    if (record != null && string.IsNullOrEmpty(record.LocalPath))
                        Use(record.PlaceholderPath, "Saved album");
                }

            var entries = new List<Entry>();
            foreach (var record in store.AllTracks)
            {
                var path = record.PlaceholderPath;
                if (string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(record.LocalPath) ||
                    !usedIn.TryGetValue(path, out var where) || !File.Exists(path))
                    continue;
                var tags = musicBee.GetTrack(path);
                entries.Add(new Entry
                {
                    Record = record,
                    Path = path,
                    Title = NonEmpty(tags?.Title, record.Title),
                    Artist = NonEmpty(tags?.Artist, record.Artist),
                    AlbumArtist = NonEmpty(tags?.AlbumArtist, NonEmpty(tags?.Artist, record.Artist)),
                    Album = NonEmpty(tags?.Album, record.Album),
                    Duration = tags?.Duration ?? (record.DurationMs > 0 ? TimeSpan.FromMilliseconds(record.DurationMs) : (TimeSpan?)null),
                    UsedIn = where,
                });
            }
            return entries
                .OrderBy(e => e.AlbumArtist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Album, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => System.IO.Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>A spreadsheet-friendly CSV (UTF-8 with a byte-order mark, so Excel reads accents correctly).</summary>
        public static void WriteCsv(string path, IEnumerable<Entry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Artist,Album Artist,Album,Title,Length,ISRC,Spotify,Used In");
            foreach (var e in entries)
                sb.AppendLine(string.Join(",", new[]
                {
                    e.Artist, e.AlbumArtist, e.Album, e.Title, FormatLength(e.Duration), e.Record.Isrc, e.SpotifyLink,
                    string.Join("; ", e.UsedIn),
                }.Select(Quote)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        public static string FormatLength(TimeSpan? d) =>
            d == null ? "" : d.Value.TotalHours >= 1 ? d.Value.ToString(@"h\:mm\:ss") : d.Value.ToString(@"m\:ss");

        private static string Quote(string value)
        {
            value ??= "";
            // A leading = + - @ would make a spreadsheet treat the cell as a formula
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0)
                value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || value != value.Trim()
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static string NonEmpty(string preferred, string fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback ?? "" : preferred;
    }
}
