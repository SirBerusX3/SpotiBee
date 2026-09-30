using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        /// <summary>Every playlist in MusicBee, as (url, name).</summary>
        IList<(string Url, string Name)> GetPlaylists();
        string[] GetPlaylistFiles(string playlistUrl);
        string GetPlaylistName(string playlistUrl);
        /// <summary>Auto-playlists are defined by a filter, so their contents can't be set.</summary>
        bool IsAutoPlaylist(string playlistUrl);
        LocalTrack GetTrack(string file);

        // Now playing list
        bool PlayNow(string[] files);
        bool QueueNext(string[] files);
        bool QueueLast(string[] files);
        bool AppendToPlaylist(string playlistUrl, string[] files);
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
                var track = GetTrack(file);
                if (track != null)
                    yield return track;
            }
        }

        public LocalTrack GetTrack(string file)
        {
            if (!api.Library_GetFileTags(file, MatchFields, out var tags) || tags == null || tags.Length < MatchFields.Length)
                return null;
            return new LocalTrack
            {
                Path = file,
                Artist = tags[0],
                AlbumArtist = tags[1],
                Title = tags[2],
                Album = tags[3],
                Duration = ParseDuration(api.Library_GetFileProperty(file, FilePropertyType.Duration)),
            };
        }

        public IList<(string Url, string Name)> GetPlaylists()
        {
            var result = new List<(string, string)>();
            if (!api.Playlist_QueryPlaylists())
                return result;
            string url;
            while ((url = api.Playlist_QueryGetNextPlaylist()) != null)
                result.Add((url, api.Playlist_GetName(url)));
            return result;
        }

        public string[] GetPlaylistFiles(string playlistUrl) =>
            api.Playlist_QueryFilesEx(playlistUrl, out var files) && files != null ? files : new string[0];

        public string GetPlaylistName(string playlistUrl) => api.Playlist_GetName(playlistUrl);

        public bool IsAutoPlaylist(string playlistUrl) => api.Playlist_GetType(playlistUrl) == PlaylistFormat.Auto;

        public bool PlayNow(string[] files)
        {
            if (files.Length == 0 || !api.NowPlayingList_PlayNow(files[0]))
                return false;
            return files.Length == 1 || api.NowPlayingList_QueueFilesNext(files.Skip(1).ToArray());
        }

        public bool QueueNext(string[] files) => files.Length > 0 && api.NowPlayingList_QueueFilesNext(files);
        public bool QueueLast(string[] files) => files.Length > 0 && api.NowPlayingList_QueueFilesLast(files);
        public bool AppendToPlaylist(string playlistUrl, string[] files) => files.Length > 0 && api.Playlist_AppendFiles(playlistUrl, files);

        public string AddToLibrary(string path) =>
            api.Library_AddFileToLibrary(path, LibraryCategory.Music) ?? path;

        /// <summary>Finds a playlist by name; with a folder, only inside that playlist folder.</summary>
        public string FindPlaylist(string folder, string name)
        {
            foreach (var (url, playlistName) in GetPlaylists())
            {
                // Playlist URLs are file paths under MusicBee's playlist folder
                var parent = Path.GetFileName(Path.GetDirectoryName(url) ?? "");
                if ((folder == null || string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(playlistName, name, StringComparison.OrdinalIgnoreCase))
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
