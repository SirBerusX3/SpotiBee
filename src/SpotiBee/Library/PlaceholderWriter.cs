using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Spotify;

namespace SpotiBee.Library
{
    /// <summary>Creates silent placeholder files for Spotify tracks, laid out as Artist\Album\NN Title.opus.</summary>
    public sealed class PlaceholderWriter
    {
        /// <summary>Written to the ENCODER tag so placeholders can be filtered in MusicBee (Encoder is "SpotiBee").</summary>
        public const string EncoderTag = "SpotiBee";

        private const int MaxComponentLength = 80;
        private readonly HttpClient http;
        private readonly string root;
        private readonly ConcurrentDictionary<string, Task<SilentOpusFile.Picture>> artworkCache =
            new ConcurrentDictionary<string, Task<SilentOpusFile.Picture>>(StringComparer.Ordinal);

        public PlaceholderWriter(HttpClient http, string root)
        {
            this.http = http;
            this.root = root;
        }

        public string Root => root;

        public bool IsPlaceholderPath(string path) =>
            !string.IsNullOrEmpty(path) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        public async Task<string> CreateAsync(Track track, CancellationToken ct)
        {
            var path = ChoosePath(track);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var cover = await GetArtworkAsync(track, ct).ConfigureAwait(false);
            SilentOpusFile.Write(path, TimeSpan.FromMilliseconds(track.DurationMs), Tags(track), cover);
            return path;
        }

        private static IEnumerable<KeyValuePair<string, string>> Tags(Track track)
        {
            KeyValuePair<string, string> Tag(string key, string value) => new KeyValuePair<string, string>(key, value);

            yield return Tag("TITLE", track.Name);
            foreach (var artist in track.Artists ?? new Artist[0])
                yield return Tag("ARTIST", artist.Name);
            yield return Tag("ALBUM", track.Album?.Name);
            foreach (var artist in track.Album?.Artists ?? new Artist[0])
                yield return Tag("ALBUMARTIST", artist.Name);
            if (track.TrackNumber > 0)
                yield return Tag("TRACKNUMBER", track.TrackNumber.ToString());
            if (track.DiscNumber > 0)
                yield return Tag("DISCNUMBER", track.DiscNumber.ToString());
            yield return Tag("DATE", track.Album?.ReleaseDate);
            yield return Tag("ISRC", track.ExternalIds?.Isrc);
            yield return Tag("SPOTIFY_URI", track.Uri);
            yield return Tag("ENCODER", EncoderTag);
        }

        private string ChoosePath(Track track)
        {
            var artist = track.Album?.Artists?.FirstOrDefault()?.Name ?? track.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist";
            var album = track.Album?.Name ?? "Unknown Album";
            var number = track.TrackNumber > 0
                ? (track.DiscNumber > 1 ? $"{track.DiscNumber}-" : "") + track.TrackNumber.ToString("00") + " "
                : "";
            var folder = Path.Combine(root, SafeName(artist), SafeName(album));
            var baseName = SafeName(number + track.Name);

            var path = Path.Combine(folder, baseName + ".opus");
            // Same name, different recording (e.g. a deluxe edition's alternate take): disambiguate with the ID
            if (File.Exists(path) && !IsSameTrack(path, track.Uri))
                path = Path.Combine(folder, $"{baseName} [{track.Id}].opus");
            return path;
        }

        private static bool IsSameTrack(string path, string uri)
        {
            try
            {
                // The Opus header plus the start of OpusTags is well within the first few KB
                var head = new byte[8192];
                int read;
                using (var fs = File.OpenRead(path))
                    read = fs.Read(head, 0, head.Length);
                return Encoding.UTF8.GetString(head, 0, read).Contains("SPOTIFY_URI=" + uri);
            }
            catch (IOException)
            {
                return false;
            }
        }

        private Task<SilentOpusFile.Picture> GetArtworkAsync(Track track, CancellationToken ct)
        {
            // ~300px is plenty for MusicBee's artwork panels and keeps placeholders small
            var image = track.Artwork?
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .OrderBy(i => Math.Abs((i.Width ?? 300) - 300))
                .FirstOrDefault();
            if (image == null)
                return Task.FromResult<SilentOpusFile.Picture>(null);

            return artworkCache.GetOrAdd(image.Url, async url =>
            {
                try
                {
                    using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return null;
                    return new SilentOpusFile.Picture
                    {
                        Data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false),
                        MimeType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                        Width = image.Width ?? 0,
                        Height = image.Height ?? 0,
                    };
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return null;   // artwork is nice-to-have; never fail an import over it
                }
            });
        }

        internal static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            var result = sb.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length > MaxComponentLength)
                result = result.Substring(0, MaxComponentLength).TrimEnd('.', ' ');
            if (result.Length == 0)
                result = "_";
            // Reserved device names can't be used as file or folder names on Windows
            var stem = result.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$"))
                result = "_" + result;
            return result;
        }
    }
}
