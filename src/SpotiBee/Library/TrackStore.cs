using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace SpotiBee.Library
{
    /// <summary>
    /// SpotiBee's own record of Spotify tracks and the MusicBee files that stand in for them.
    /// Kept separate from file tags so users' local music files are never modified.
    /// </summary>
    public sealed class TrackStore
    {
        private readonly string filePath;
        private readonly object sync = new object();
        private Dictionary<string, TrackRecord> tracks = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        private Dictionary<string, PlaylistRecord> playlists = new Dictionary<string, PlaylistRecord>(StringComparer.Ordinal);
        private Dictionary<string, TrackRecord> byPath;
        private readonly Dictionary<string, (DateTime When, int Version)> unmatched = new Dictionary<string, (DateTime, int)>(StringComparer.OrdinalIgnoreCase);

        public TrackStore(string filePath)
        {
            this.filePath = filePath;
            Load();
        }

        public TrackRecord GetTrack(string spotifyId)
        {
            lock (sync)
                return tracks.TryGetValue(spotifyId, out var record) ? record : null;
        }

        public TrackRecord GetOrAddTrack(string spotifyId)
        {
            lock (sync)
            {
                if (!tracks.TryGetValue(spotifyId, out var record))
                {
                    record = new TrackRecord { SpotifyId = spotifyId };
                    tracks[spotifyId] = record;
                }
                byPath = null;
                return record;
            }
        }

        /// <summary>Finds the Spotify track behind a MusicBee file, whether it's a placeholder or a matched local file.</summary>
        public TrackRecord FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            lock (sync)
            {
                if (byPath == null)
                {
                    byPath = new Dictionary<string, TrackRecord>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in tracks.Values)
                    {
                        if (!string.IsNullOrEmpty(t.PlaceholderPath))
                            byPath[t.PlaceholderPath] = t;
                        if (!string.IsNullOrEmpty(t.LocalPath))
                            byPath[t.LocalPath] = t;
                    }
                }
                return byPath.TryGetValue(path, out var record) ? record : null;
            }
        }

        public PlaylistRecord GetPlaylist(string spotifyPlaylistId)
        {
            lock (sync)
                return playlists.TryGetValue(spotifyPlaylistId, out var record) ? record : null;
        }

        public void SetPlaylist(PlaylistRecord record)
        {
            lock (sync)
                playlists[record.SpotifyId] = record;
        }

        public void RemovePlaylist(string spotifyPlaylistId)
        {
            lock (sync)
                playlists.Remove(spotifyPlaylistId);
        }

        public PlaylistRecord FindPlaylistByMusicBeeUrl(string url)
        {
            lock (sync)
                return playlists.Values.FirstOrDefault(p => string.Equals(p.MusicBeePlaylistUrl, url, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True if this local file was searched for on Spotify recently without a match.</summary>
        public bool WasRecentlyUnmatched(string path, TimeSpan within, int matcherVersion)
        {
            lock (sync)
                return unmatched.TryGetValue(path, out var u) && u.Version >= matcherVersion && DateTime.UtcNow - u.When < within;
        }

        public void SetUnmatched(string path, bool isUnmatched, int matcherVersion)
        {
            lock (sync)
            {
                if (isUnmatched)
                    unmatched[path] = (DateTime.UtcNow, matcherVersion);
                else
                    unmatched.Remove(path);
            }
        }

        /// <summary>Local files that were searched for on Spotify without a match.</summary>
        public IReadOnlyList<string> UnmatchedPaths
        {
            get
            {
                lock (sync)
                    return unmatched.Keys.ToList();
            }
        }

        /// <summary>
        /// Records the user's own choice of Spotify track for a local file. Pinned pairings are
        /// never replaced by automatic matching.
        /// </summary>
        public TrackRecord PinMatch(string file, Spotify.Track track, bool lengthDiffers)
        {
            lock (sync)
            {
                // A file can only stand for one Spotify track
                foreach (var other in tracks.Values.Where(t => string.Equals(t.LocalPath, file, StringComparison.OrdinalIgnoreCase)))
                {
                    other.LocalPath = null;
                    other.LocalPathPinned = false;
                }
                if (!tracks.TryGetValue(track.Id, out var record))
                    tracks[track.Id] = record = new TrackRecord { SpotifyId = track.Id };
                record.Uri = track.Uri;
                record.Title = track.Name;
                record.Artist = track.ArtistNames;
                record.Album = track.AlbumName;
                record.DurationMs = track.DurationMs;
                record.Isrc = track.ExternalIds?.Isrc;
                record.LocalPath = file;
                record.LocalPathPinned = true;
                record.LocalLengthDiffers = lengthDiffers;
                unmatched.Remove(file);
                byPath = null;
                return record;
            }
        }

        public IReadOnlyList<PlaylistRecord> Playlists
        {
            get
            {
                lock (sync)
                    return playlists.Values.ToList();
            }
        }

        /// <summary>Call after changing a record's paths so path lookups stay correct.</summary>
        public void InvalidatePathIndex()
        {
            lock (sync)
                byPath = null;
        }

        public void Save()
        {
            StoreData data;
            lock (sync)
                data = new StoreData
                {
                    Version = 1,
                    Tracks = tracks.Values.ToList(),
                    Playlists = playlists.Values.ToList(),
                    Unmatched = unmatched.Select(u => new UnmatchedFile { Path = u.Key, CheckedUtc = u.Value.When, MatcherVersion = u.Value.Version }).ToList(),
                };

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, Json.Stringify(data));
            if (File.Exists(filePath))
                File.Replace(temp, filePath, null);
            else
                File.Move(temp, filePath);
        }

        private void Load()
        {
            if (!File.Exists(filePath) || !Json.TryParse<StoreData>(File.ReadAllText(filePath), out var data))
                return;
            foreach (var t in data.Tracks ?? new List<TrackRecord>())
                if (!string.IsNullOrEmpty(t.SpotifyId))
                    tracks[t.SpotifyId] = t;
            foreach (var p in data.Playlists ?? new List<PlaylistRecord>())
                if (!string.IsNullOrEmpty(p.SpotifyId))
                    playlists[p.SpotifyId] = p;
            foreach (var u in data.Unmatched ?? new List<UnmatchedFile>())
                if (!string.IsNullOrEmpty(u.Path))
                    unmatched[u.Path] = (u.CheckedUtc, u.MatcherVersion);
        }

        [DataContract]
        private sealed class StoreData
        {
            [DataMember] public int Version { get; set; }
            [DataMember] public List<TrackRecord> Tracks { get; set; }
            [DataMember] public List<PlaylistRecord> Playlists { get; set; }
            [DataMember] public List<UnmatchedFile> Unmatched { get; set; }
        }

        [DataContract]
        private sealed class UnmatchedFile
        {
            [DataMember] public string Path { get; set; }
            [DataMember] public DateTime CheckedUtc { get; set; }
            [DataMember] public int MatcherVersion { get; set; }
        }
    }

    [DataContract]
    public sealed class TrackRecord
    {
        [DataMember] public string SpotifyId { get; set; }
        [DataMember] public string Uri { get; set; }
        [DataMember] public string Title { get; set; }
        [DataMember] public string Artist { get; set; }
        [DataMember] public string Album { get; set; }
        [DataMember] public int DurationMs { get; set; }
        [DataMember] public string Isrc { get; set; }

        /// <summary>Silent stand-in file, if one has been generated.</summary>
        [DataMember] public string PlaceholderPath { get; set; }

        /// <summary>The user's own copy of this track, if one was found.</summary>
        [DataMember] public string LocalPath { get; set; }

        /// <summary>
        /// The local file is a different edit of this track (lengths differ by more than a few seconds),
        /// so it can't stand in as the clock while Spotify plays the track.
        /// </summary>
        [DataMember] public bool LocalLengthDiffers { get; set; }

        /// <summary>True when the user chose the local file by hand; automatic matching won't override it.</summary>
        [DataMember] public bool LocalPathPinned { get; set; }

        /// <summary>The file MusicBee playlists should reference: the local copy when there is one.</summary>
        public string PreferredPath => !string.IsNullOrEmpty(LocalPath) ? LocalPath : PlaceholderPath;
    }

    [DataContract]
    public sealed class PlaylistRecord
    {
        public const string LikedSongsId = "liked-songs";

        [DataMember] public string SpotifyId { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string SnapshotId { get; set; }
        [DataMember] public string MusicBeePlaylistUrl { get; set; }
        [DataMember] public DateTime LastImportedUtc { get; set; }

        /// <summary>Spotify track IDs in order, as of the last sync: the common ancestor for two-way merges.</summary>
        [DataMember] public List<string> TrackIds { get; set; }

        [DataMember] public string MusicBeeName { get; set; }
        [DataMember] public DateTime LastSyncedUtc { get; set; }

        /// <summary>"Artist – Title" of MusicBee tracks Spotify doesn't have, as of the last sync.</summary>
        [DataMember] public List<string> Unmatched { get; set; }

        /// <summary>The Spotify playlist has episodes or Spotify local files, which sync must leave in place.</summary>
        [DataMember] public bool HasUnsyncableItems { get; set; }

        public bool IsLikedSongs => SpotifyId == LikedSongsId;
    }
}
