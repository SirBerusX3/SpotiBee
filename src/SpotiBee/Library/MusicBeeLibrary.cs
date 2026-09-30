using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using static MusicBeePlugin.Plugin;

namespace SpotiBee.Library
{
    public sealed class LocalTrack
    {
        public string Path { get; set; }
        public string Artist { get; set; }
        public string AlbumArtist { get; set; }
        public string Title { get; set; }
        public string Album { get; set; }
        public TimeSpan? Duration { get; set; }
    }

    /// <summary>The slice of the MusicBee API that importing needs, so the logic above it stays testable.</summary>
    public interface IMusicBeeLibrary
    {
        IEnumerable<LocalTrack> GetMusicTracks();
        string AddToLibrary(string path);
        string FindPlaylist(string folder, string name);
        bool PlaylistExists(string playlistUrl);
        string CreatePlaylist(string folder, string name, string[] files);
        bool SetPlaylistFiles(string playlistUrl, string[] files);
    }

    public sealed class MusicBeeLibrary : IMusicBeeLibrary
    {
        private static readonly MetaDataType[] MatchFields =
        {
            MetaDataType.Artist, MetaDataType.AlbumArtist, MetaDataType.TrackTitle, MetaDataType.Album,
        };

        private readonly MusicBeeApiInterface api;

        public MusicBeeLibrary(MusicBeeApiInterface api)
        {
            this.api = api;
        }

        public IEnumerable<LocalTrack> GetMusicTracks()
        {
            if (!api.Library_QueryFilesEx("domain=Library", out var files) || files == null)
                yield break;

            foreach (var file in files)
            {
                if (!api.Library_GetFileTags(file, MatchFields, out var tags) || tags == null || tags.Length < MatchFields.Length)
                    continue;
                yield return new LocalTrack
                {
                    Path = file,
                    Artist = tags[0],
                    AlbumArtist = tags[1],
                    Title = tags[2],
                    Album = tags[3],
                    Duration = ParseDuration(api.Library_GetFileProperty(file, FilePropertyType.Duration)),
                };
            }
        }

        public string AddToLibrary(string path) =>
            api.Library_AddFileToLibrary(path, LibraryCategory.Music) ?? path;

        public string FindPlaylist(string folder, string name)
        {
            if (!api.Playlist_QueryPlaylists())
                return null;
            string url;
            while ((url = api.Playlist_QueryGetNextPlaylist()) != null)
            {
                // Playlist URLs are file paths under MusicBee's playlist folder
                var parent = Path.GetFileName(Path.GetDirectoryName(url) ?? "");
                if (string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(api.Playlist_GetName(url), name, StringComparison.OrdinalIgnoreCase))
                    return url;
            }
            return null;
        }

        public bool PlaylistExists(string playlistUrl) =>
            !string.IsNullOrEmpty(playlistUrl) && File.Exists(playlistUrl);

        public string CreatePlaylist(string folder, string name, string[] files) =>
            api.Playlist_CreatePlaylist(folder, name, files);

        public bool SetPlaylistFiles(string playlistUrl, string[] files) =>
            api.Playlist_SetFiles(playlistUrl, files);

        /// <summary>MusicBee reports duration as display text such as "3:45" or "1:02:03".</summary>
        internal static TimeSpan? ParseDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var parts = text.Trim().Split(':');
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return null;
                total = total * 60 + n;
            }
            return total > 0 ? TimeSpan.FromSeconds(total) : (TimeSpan?)null;
        }
    }
}
