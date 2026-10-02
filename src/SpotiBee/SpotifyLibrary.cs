using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee
{
    /// <summary>
    /// The track actions shared by the panel and MusicBee's right-click menu: like, add to or
    /// remove from a Spotify playlist, save the album, follow the artist. When a changed playlist
    /// (or Liked Songs) is synced, MusicBee's copy catches up straight away.
    /// Must be used on MusicBee's UI thread.
    /// </summary>
    public sealed class SpotifyLibrary
    {
        /// <summary>Album and artist actions need each track's full details, one request per track.</summary>
        public const int MaxDetailedTracks = 10;

        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly SyncScheduler sync;
        private readonly Dictionary<string, Track> details = new Dictionary<string, Track>(StringComparer.Ordinal);

        public SpotifyLibrary(SpotiBeeController controller, IMusicBeeLibrary musicBee, SyncScheduler sync, string playlistCachePath)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            this.sync = sync;
            Playlists = new PlaylistMembership(playlistCachePath);
        }

        public PlaylistMembership Playlists { get; }

        /// <summary>Raised after SpotiBee likes, saves or follows something, so displays can update.</summary>
        public event Action Changed;

        private SpotifyClient Client => controller.Client ?? throw new SpotifyAuthException("Not connected to Spotify.");

        // --- Finding the Spotify tracks ------------------------------------

        /// <summary>
        /// The Spotify tracks behind MusicBee files, without duplicates; files Spotify doesn't have
        /// are left out. Only IDs and titles are filled in: see <see cref="GetDetailsAsync"/>.
        /// </summary>
        public async Task<List<Track>> ResolveFilesAsync(IList<string> files, CancellationToken ct)
        {
            var resolver = new TrackResolver(Client, musicBee, controller.Store);
            var result = new List<Track>();
            var learned = false;
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Files SpotiBee hasn't seen before may need a search, so show progress for big selections
                learned |= controller.Store.FindByPath(files[i]) == null;
                if (learned && files.Count > 3)
                    controller.ShowStatus($"Finding the selected songs on Spotify… {i + 1} of {files.Count}");
                var id = await resolver.ResolveAsync(files[i], ct);
                if (id == null || result.Any(t => t.Id == id))
                    continue;
                var record = controller.Store.GetTrack(id);
                result.Add(new Track { Id = id, Uri = "spotify:track:" + id, Name = record?.Title, Type = "track" });
            }
            if (learned)
            {
                try { controller.Store.Save(); }
                catch (Exception ex) { Diagnostics.Log("Saving matches", ex); }
            }
            return result;
        }

        /// <summary>Full details (album, artists) for up to <see cref="MaxDetailedTracks"/> tracks.</summary>
        public async Task<List<Track>> GetDetailsAsync(IEnumerable<Track> tracks, CancellationToken ct)
        {
            var result = new List<Track>();
            foreach (var track in tracks.Take(MaxDetailedTracks))
            {
                if (track.Album?.Uri != null && track.Artists != null)
                {
                    result.Add(track);
                    continue;
                }
                if (!details.TryGetValue(track.Id, out var full))
                {
                    try { full = await Client.GetTrackAsync(track.Id, ct); }
                    catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.BadRequest) { }
                    details[track.Id] = full;
                }
                if (full != null)
                    result.Add(full);
            }
            return result;
        }

        // --- Liked, saved and followed --------------------------------------

        public sealed class SavedState
        {
            internal readonly Dictionary<string, bool> Saved = new Dictionary<string, bool>(StringComparer.Ordinal);

            /// <summary>False when the login predates the follow permissions; see <see cref="SpotiBeeController.ReconnectAsync"/>.</summary>
            public bool CanFollow { get; internal set; }

            public bool Knows(string uri) => uri != null && Saved.ContainsKey(uri);

            public bool IsSaved(string uri) => uri != null && Saved.TryGetValue(uri, out var saved) && saved;

            public bool AllSaved(IEnumerable<string> uris) => uris.All(IsSaved);
        }

        /// <summary>Whether each track is liked, each album saved and each artist followed.</summary>
        public async Task<SavedState> GetStateAsync(IEnumerable<string> uris, CancellationToken ct)
        {
            var client = Client;
            var state = new SavedState { CanFollow = await client.HasScopesAsync(SpotifyAuth.FollowScopes) };
            var asked = uris.Where(u => u != null && (state.CanFollow || !IsArtist(u))).Distinct().ToList();
            if (asked.Count == 0)
                return state;
            var flags = await client.LibraryContainsAsync(asked, ct);
            for (var i = 0; i < asked.Count; i++)
                state.Saved[asked[i]] = flags[i];
            return state;
        }

        /// <summary>Likes or unlikes tracks, saves or removes albums, follows or unfollows artists.</summary>
        public async Task SetSavedAsync(IList<string> uris, bool save)
        {
            if (uris.Count == 0)
                return;
            if (save)
                await Client.SaveToLibraryAsync(uris);
            else
                await Client.RemoveFromLibraryAsync(uris);
            if (uris.Any(u => u.StartsWith("spotify:track:", StringComparison.Ordinal)))
                sync?.SyncSoon(controller.Store.GetPlaylist(PlaylistRecord.LikedSongsId));
            Changed?.Invoke();
        }

        public static bool IsArtist(string uri) => uri.StartsWith("spotify:artist:", StringComparison.Ordinal);

        // --- Playlists -----------------------------------------------------

        public Task RefreshPlaylistsAsync(bool force = false) =>
            Playlists.RefreshAsync(Client, controller.User?.Id, controller.Store, force);

        /// <summary>True when the playlist has every one of the tracks.</summary>
        public bool PlaylistHasAll(PlaylistMembership.Entry playlist, IEnumerable<Track> tracks) =>
            tracks.All(t => Playlists.Contains(playlist, t.Id));

        /// <summary>Adds the tracks the playlist doesn't have yet, at the end. Returns how many were added.</summary>
        public async Task<int> AddToPlaylistAsync(PlaylistMembership.Entry playlist, IList<Track> tracks)
        {
            var missing = tracks.Select(t => t.Id).Distinct().Where(id => !Playlists.Contains(playlist, id)).ToList();
            if (missing.Count == 0)
                return 0;
            string snapshot = null;
            for (var i = 0; i < missing.Count; i += SpotifyClient.MaxPlaylistItemsPerRequest)
                snapshot = await Client.AddPlaylistItemsAsync(playlist.Id,
                    missing.Skip(i).Take(SpotifyClient.MaxPlaylistItemsPerRequest).Select(id => "spotify:track:" + id).ToList(), null);
            Playlists.Added(playlist, missing, snapshot);
            sync?.SyncSoon(controller.Store.GetPlaylist(playlist.Id));
            return missing.Count;
        }

        /// <summary>Removes every copy of the tracks from the playlist. Returns how many tracks were in it.</summary>
        public async Task<int> RemoveFromPlaylistAsync(PlaylistMembership.Entry playlist, IList<Track> tracks)
        {
            var present = tracks.Select(t => t.Id).Distinct().Where(id => Playlists.Contains(playlist, id)).ToList();
            if (present.Count == 0)
                return 0;
            string snapshot = null;
            for (var i = 0; i < present.Count; i += SpotifyClient.MaxPlaylistItemsPerRequest)
                snapshot = await Client.RemovePlaylistItemsAsync(playlist.Id,
                    present.Skip(i).Take(SpotifyClient.MaxPlaylistItemsPerRequest).Select(id => "spotify:track:" + id).ToList());
            Playlists.Removed(playlist, present, snapshot);
            sync?.SyncSoon(controller.Store.GetPlaylist(playlist.Id));
            return present.Count;
        }
    }
}
