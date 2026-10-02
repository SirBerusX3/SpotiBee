using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>
    /// Fills the Spotify menus for one or more tracks: playlists (ticked where the tracks already
    /// are), like, album and artist. Used by the panel's buttons and MusicBee's right-click menu.
    /// Menus open at once and fill in as Spotify answers. Use one instance per menu.
    /// </summary>
    internal sealed class TrackMenu
    {
        /// <summary>Beyond this many albums or artists, one item acts on them all.</summary>
        private const int MaxSeparateItems = 4;

        private readonly SpotiBeeController controller;
        private readonly Func<IWin32Window> owner;
        private int generation;

        public TrackMenu(SpotiBeeController controller, Func<IWin32Window> owner)
        {
            this.controller = controller;
            this.owner = owner;
        }

        private SpotifyLibrary Library => controller.SpotifyLibrary;

        // --- Whole menus ---------------------------------------------------

        /// <summary>MusicBee's right-click menu: everything, for the selected files.</summary>
        public async Task FillForFilesAsync(ToolStripItemCollection items, string[] files)
        {
            var current = ++generation;
            if (Library == null || !controller.IsConnected)
            {
                Replace(items, Disabled("Not connected to Spotify"));
                return;
            }
            if (files.Length == 0)
            {
                Replace(items, Disabled("No songs selected"));
                return;
            }

            Replace(items, Disabled(files.Length == 1 ? "Finding this song on Spotify…" : $"Finding {files.Length} songs on Spotify…"));
            List<Track> tracks, detailed;
            SpotifyLibrary.SavedState state;
            try
            {
                tracks = await Library.ResolveFilesAsync(files, CancellationToken.None);
                if (tracks.Count == 0)
                {
                    if (current == generation)
                        Replace(items, Disabled(files.Length == 1 ? "This song isn't on Spotify" : "None of these songs are on Spotify"));
                    return;
                }
                detailed = tracks.Count <= SpotifyLibrary.MaxDetailedTracks
                    ? await Library.GetDetailsAsync(tracks, CancellationToken.None)
                    : new List<Track>();
                state = await Library.GetStateAsync(tracks.Select(t => t.Uri).Concat(AlbumAndArtistUris(detailed)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Spotify menu", ex);
                if (current == generation)
                    Replace(items, Disabled("Couldn't reach Spotify: " + Friendly(ex)));
                return;
            }
            if (current != generation)
                return;

            var result = new List<ToolStripItem>();
            if (tracks.Count < files.Length)
                result.Add(Disabled($"{files.Length - tracks.Count} of the {files.Length} songs aren't on Spotify"));
            result.Add(LikeItem(tracks, state));
            result.Add(PlaylistsItem("Spotify playlists", tracks));
            var more = AlbumAndArtistItems(detailed, state);
            if (more.Count > 0)
            {
                result.Add(new ToolStripSeparator());
                result.AddRange(more);
            }
            result.Add(new ToolStripSeparator());
            result.Add(new ToolStripMenuItem(tracks.Count == 1 ? "Open in Spotify" : "Open the first song in Spotify", null,
                (s, e) => Open(tracks[0].Uri)));
            Replace(items, result.ToArray());
        }

        /// <summary>The panel's "more" button: album and artist, for the track Spotify is playing.</summary>
        public async Task FillAlbumAndArtistAsync(ToolStripItemCollection items, Track track)
        {
            var current = ++generation;
            if (Library == null || !controller.IsConnected || track?.Uri == null)
            {
                Replace(items, Disabled("Not connected to Spotify"));
                return;
            }
            Replace(items, Disabled("Checking Spotify…"));
            var detailed = new List<Track> { track };
            SpotifyLibrary.SavedState state;
            try
            {
                state = await Library.GetStateAsync(AlbumAndArtistUris(detailed), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Spotify menu", ex);
                if (current == generation)
                    Replace(items, Disabled("Couldn't reach Spotify: " + Friendly(ex)));
                return;
            }
            if (current != generation)
                return;

            var result = AlbumAndArtistItems(detailed, state);
            result.Add(new ToolStripSeparator());
            if (track.Album?.Uri != null)
                result.Add(new ToolStripMenuItem("Open album in Spotify", null, (s, e) => Open(track.Album.Uri)));
            var firstArtist = track.Artists?.FirstOrDefault(a => a?.Uri != null);
            if (firstArtist != null)
                result.Add(new ToolStripMenuItem(track.Artists.Length > 1 ? $"Open {Escape(firstArtist.Name)} in Spotify" : "Open artist in Spotify",
                    null, (s, e) => Open(firstArtist.Uri)));
            Replace(items, result.ToArray());
        }

        // --- Playlists -----------------------------------------------------

        /// <summary>A submenu listing the playlists, filled when it opens.</summary>
        public ToolStripMenuItem PlaylistsItem(string text, IList<Track> tracks)
        {
            var item = new ToolStripMenuItem(text) { ToolTipText = "Ticked playlists already have this. Click one to add or remove." };
            item.DropDownItems.Add(Disabled("Loading your playlists…"));
            item.DropDownOpening += async (s, e) => await FillPlaylistsAsync(item.DropDownItems, tracks);
            return item;
        }

        /// <summary>
        /// One item per playlist the user can edit, with its length, ticked when it already has every
        /// track. Shows what's known straight away, then catches up with any changes on Spotify.
        /// </summary>
        public async Task FillPlaylistsAsync(ToolStripItemCollection items, IList<Track> tracks)
        {
            var library = Library;
            if (library == null || !controller.IsConnected)
            {
                Replace(items, Disabled("Not connected to Spotify"));
                return;
            }
            var shown = -1;
            if (library.Playlists.HasLoaded)
            {
                ShowPlaylists(items, tracks);
                shown = library.Playlists.Version;
            }
            else
            {
                Replace(items, Disabled("Loading your playlists…"));
            }

            try
            {
                await library.RefreshPlaylistsAsync();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Loading playlists", ex);
                if (shown < 0)
                    Replace(items, Disabled("Couldn't load your playlists: " + Friendly(ex)));
                return;
            }
            if (library.Playlists.Version != shown)
                ShowPlaylists(items, tracks);
        }

        private void ShowPlaylists(ToolStripItemCollection items, IList<Track> tracks)
        {
            var library = Library;
            var result = new List<ToolStripItem>();
            foreach (var playlist in library.Playlists.Playlists)
            {
                var target = playlist;
                var hasAll = library.PlaylistHasAll(playlist, tracks);
                result.Add(new ToolStripMenuItem($"{Escape(playlist.Name)}   ({playlist.Count})", null,
                    async (s, e) => await TogglePlaylistAsync(target, tracks, remove: hasAll))
                {
                    Checked = hasAll,
                });
            }
            if (result.Count == 0)
                result.Add(Disabled("You have no Spotify playlists you can change"));
            Replace(items, result.ToArray());
        }

        private async Task TogglePlaylistAsync(PlaylistMembership.Entry playlist, IList<Track> tracks, bool remove)
        {
            var library = Library;
            if (library == null)
                return;
            try
            {
                var changed = remove
                    ? await library.RemoveFromPlaylistAsync(playlist, tracks)
                    : await library.AddToPlaylistAsync(playlist, tracks);
                var synced = controller.Store.GetPlaylist(playlist.Id) != null && !controller.Settings.DisableAutoSync;
                var message = remove
                    ? $"Removed {Describe(tracks)} from \"{playlist.Name}\" on Spotify."
                    : changed == 0
                        ? $"\"{playlist.Name}\" already has {Describe(tracks)}."
                        : $"Added {Describe(tracks, changed)} to \"{playlist.Name}\" on Spotify.";
                controller.ShowStatus(message + (synced && changed > 0 ? " Your MusicBee copy will catch up in a moment." : ""));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Changing a playlist", ex);
                controller.ShowStatus($"Couldn't change \"{playlist.Name}\": {Friendly(ex)}");
            }
        }

        // --- Like, album, artist -------------------------------------------

        public ToolStripMenuItem LikeItem(IList<Track> tracks, SpotifyLibrary.SavedState state)
        {
            var uris = tracks.Select(t => t.Uri).ToList();
            var liked = state.AllSaved(uris);
            var text = liked
                ? tracks.Count == 1 ? "Remove from Liked Songs" : $"Remove {tracks.Count} songs from Liked Songs"
                : tracks.Count == 1 ? "Like on Spotify" : $"Like {tracks.Count} songs on Spotify";
            return new ToolStripMenuItem(text, null, async (s, e) =>
                await SetSavedAsync(uris, !liked, liked ? $"Removed {Describe(tracks)} from Liked Songs." : $"Liked {Describe(tracks)} on Spotify."));
        }

        private static IEnumerable<string> AlbumAndArtistUris(IList<Track> detailed) =>
            Albums(detailed).Select(a => a.Uri).Concat(Artists(detailed).Select(a => a.Uri));

        private static List<Album> Albums(IList<Track> detailed) =>
            detailed.Select(t => t.Album).Where(a => a?.Uri != null).GroupBy(a => a.Uri).Select(g => g.First()).ToList();

        /// <summary>Every artist on a single track (features included); just the main artist of each when there are several tracks.</summary>
        private static List<Artist> Artists(IList<Track> detailed) =>
            (detailed.Count == 1
                ? detailed[0].Artists ?? new Artist[0]
                : detailed.Select(t => t.Artists?.FirstOrDefault()))
            .Where(a => a?.Uri != null).GroupBy(a => a.Uri).Select(g => g.First()).ToList();

        private List<ToolStripItem> AlbumAndArtistItems(IList<Track> detailed, SpotifyLibrary.SavedState state)
        {
            var result = new List<ToolStripItem>();

            var albums = Albums(detailed);
            result.AddRange(ToggleItems(albums.Select(a => (a.Uri, a.Name)).ToList(), state,
                name => $"Save album \"{name}\"", name => $"Remove album \"{name}\" from your library",
                n => $"Save {n} albums", n => $"Remove {n} albums from your library",
                (saved, name) => saved ? $"Saved {name} to your Spotify library." : $"Removed {name} from your Spotify library."));

            var artists = Artists(detailed);
            if (artists.Count == 0)
                return result;
            if (!state.CanFollow)
            {
                result.Add(new ToolStripMenuItem(artists.Count == 1 ? $"Follow {Escape(artists[0].Name)}…" : "Follow artists…", null,
                    async (s, e) => await AllowFollowingAsync())
                {
                    ToolTipText = "Needs one more permission from Spotify",
                });
                return result;
            }
            result.AddRange(ToggleItems(artists.Select(a => (a.Uri, a.Name)).ToList(), state,
                name => $"Follow {name}", name => $"Unfollow {name}",
                n => $"Follow {n} artists", n => $"Unfollow {n} artists",
                (followed, name) => followed ? $"Following {name} on Spotify." : $"Unfollowed {name} on Spotify."));
            return result;
        }

        /// <summary>A save/remove item per thing, or one for them all when there are many.</summary>
        private IEnumerable<ToolStripItem> ToggleItems(IList<(string Uri, string Name)> things, SpotifyLibrary.SavedState state,
            Func<string, string> save, Func<string, string> remove, Func<int, string> saveAll, Func<int, string> removeAll,
            Func<bool, string, string> done)
        {
            if (things.Count > MaxSeparateItems)
            {
                var uris = things.Select(t => t.Uri).ToList();
                var all = state.AllSaved(uris);
                var label = $"{things.Count} {(SpotifyLibrary.IsArtist(uris[0]) ? "artists" : "albums")}";
                yield return new ToolStripMenuItem(all ? removeAll(things.Count) : saveAll(things.Count), null,
                    async (s, e) => await SetSavedAsync(uris, !all, done(!all, label)));
                yield break;
            }
            foreach (var (uri, name) in things)
            {
                var saved = state.IsSaved(uri);
                yield return new ToolStripMenuItem(Escape(saved ? remove(name) : save(name)), null,
                    async (s, e) => await SetSavedAsync(new[] { uri }, !saved, done(!saved, SpotifyLibrary.IsArtist(uri) ? name : $"\"{name}\"")));
            }
        }

        private async Task SetSavedAsync(IList<string> uris, bool save, string doneMessage)
        {
            try
            {
                await Library.SetSavedAsync(uris, save);
                controller.ShowStatus(doneMessage);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Saving to the Spotify library", ex);
                controller.ShowStatus("Couldn't do that on Spotify: " + Friendly(ex));
            }
        }

        /// <summary>Logins from before 0.6 can't follow artists: offer to log in again with the extra permission.</summary>
        private async Task AllowFollowingAsync()
        {
            var answer = MessageBox.Show(owner(),
                "Following artists needs a permission SpotiBee didn't ask for before version 0.6.\n\n" +
                "Your browser will open so you can approve it on Spotify. Nothing else changes.",
                "SpotiBee", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK)
                return;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                controller.ShowStatus("Waiting for you to approve in the browser…");
                await controller.ReconnectAsync(cancel.Token);
                controller.ShowStatus("Done. You can follow artists from SpotiBee now.");
            }
            catch (OperationCanceledException)
            {
                controller.ShowStatus("Reconnecting timed out. Try again when you're ready.");
            }
            catch (Exception ex)
            {
                controller.ShowStatus("Couldn't reconnect: " + Friendly(ex));
            }
        }

        // --- Helpers -------------------------------------------------------

        private static void Replace(ToolStripItemCollection items, params ToolStripItem[] replacement)
        {
            var owner = items.Count > 0 ? items[0].Owner : null;
            owner?.SuspendLayout();
            var old = items.Cast<ToolStripItem>().ToList();
            items.Clear();
            items.AddRange(replacement);
            owner?.ResumeLayout();
            foreach (var item in old)
                item.Dispose();
        }

        private static ToolStripMenuItem Disabled(string text) => new ToolStripMenuItem(Escape(text)) { Enabled = false };

        /// <summary>Menus treat "&amp;" as a keyboard shortcut marker.</summary>
        private static string Escape(string text) => (text ?? "").Replace("&", "&&");

        private static string Describe(IList<Track> tracks, int? count = null)
        {
            var n = count ?? tracks.Count;
            return n == 1 && tracks.Count == 1 && !string.IsNullOrEmpty(tracks[0].Name) ? $"\"{tracks[0].Name}\""
                : n == 1 ? "1 song" : $"{n} songs";
        }

        private static string Friendly(Exception ex) => ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message;

        private static void Open(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { /* Spotify app not installed */ }
        }
    }
}
