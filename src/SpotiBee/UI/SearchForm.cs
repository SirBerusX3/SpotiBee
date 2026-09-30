using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>
    /// Search Spotify and play, queue, like or add the results to a MusicBee playlist.
    /// Songs the user owns use their local file; the rest get a placeholder on the spot.
    /// </summary>
    public sealed class SearchForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly TrackActions actions;

        private readonly ComboBox scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(0, 3, 6, 3) };
        private readonly TextBox query = new TextBox { Dock = DockStyle.Fill };
        private readonly Button search = new Button { Text = "Search", AutoSize = true };
        private readonly ListView results = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly Button more = new Button { Text = "More results", AutoSize = true, Enabled = false };
        private readonly Button playNow = new Button { Text = "Play now", AutoSize = true };
        private readonly Button playNext = new Button { Text = "Play next", AutoSize = true };
        private readonly Button queueLast = new Button { Text = "Add to queue", AutoSize = true };
        private readonly Button addToPlaylist = new Button { Text = "Add to playlist ▾", AutoSize = true };
        private readonly Button like = new Button { Text = "♥ Like", AutoSize = true };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ContextMenuStrip playlistMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
        private string lastQuery;
        private int nextOffset;
        private bool busy;

        public SearchForm(SpotiBeeController controller, IMusicBeeLibrary musicBee)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            actions = new TrackActions(controller.Client, musicBee, controller.Store,
                new PlaceholderWriter(controller.Http, controller.Settings.EffectivePlaceholderFolder));

            Text = "Search Spotify";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(800, 500);
            MinimumSize = new Size(560, 300);
            Padding = new Padding(10);
            AcceptButton = search;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 3 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            scope.Items.AddRange(Scopes.Select(s => (object)s.Label).ToArray());
            scope.SelectedIndex = Math.Max(0, Math.Min(Scopes.Length - 1, controller.Settings.SearchScope));
            scope.SelectedIndexChanged += async (s, e) =>
            {
                controller.Settings.SearchScope = scope.SelectedIndex;
                try { controller.Settings.Save(); } catch { /* only a preference */ }
                if (query.Text.Trim().Length > 0)
                    await NewSearchAsync();
            };
            top.Controls.Add(scope, 0, 0);
            top.Controls.Add(query, 1, 0);
            top.Controls.Add(search, 2, 0);

            results.Columns.Add("Title", 260);
            results.Columns.Add("Artist", 170);
            results.Columns.Add("Album", 180);
            results.Columns.Add("Length", 60, HorizontalAlignment.Right);
            results.Columns.Add("In MusicBee", 90);
            results.SelectedIndexChanged += (s, e) => UpdateButtons();
            results.DoubleClick += async (s, e) => await RunAsync(PlayNowAsync);
            results.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && results.SelectedItems.Count > 0)
                {
                    e.Handled = true;
                    await RunAsync(PlayNowAsync);
                }
            };

            rowMenu.Items.Add("Play now", null, async (s, e) => await RunAsync(PlayNowAsync));
            rowMenu.Items.Add("Play next", null, async (s, e) => await RunAsync(PlayNextAsync));
            rowMenu.Items.Add("Add to queue", null, async (s, e) => await RunAsync(QueueLastAsync));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add("Like on Spotify", null, async (s, e) => await RunAsync(LikeAsync));
            rowMenu.Items.Add("Open in Spotify", null, (s, e) => OpenInSpotify());
            results.ContextMenuStrip = rowMenu;

            var actionsRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            actionsRow.Controls.AddRange(new Control[] { playNow, playNext, queueLast, addToPlaylist, like, more });
            var statusRow = new Panel { Dock = DockStyle.Bottom, Height = 26 };
            statusRow.Controls.Add(status);

            Controls.Add(results);
            Controls.Add(top);
            Controls.Add(actionsRow);
            Controls.Add(statusRow);

            search.Click += async (s, e) => await NewSearchAsync();
            more.Click += async (s, e) => await LoadPageAsync(append: true);
            playNow.Click += async (s, e) => await RunAsync(PlayNowAsync);
            playNext.Click += async (s, e) => await RunAsync(PlayNextAsync);
            queueLast.Click += async (s, e) => await RunAsync(QueueLastAsync);
            like.Click += async (s, e) => await RunAsync(LikeAsync);
            addToPlaylist.Click += (s, e) =>
            {
                BuildPlaylistMenu();
                playlistMenu.Show(addToPlaylist, new Point(0, addToPlaylist.Height));
            };

            status.Text = "Choose what to search in, or use Everything. Select several results with Ctrl or Shift.";
            UpdateButtons();
        }

        // --- Searching -----------------------------------------------------

        /// <summary>Spotify search field filters. "Everything" is a plain keyword search, which Spotify
        /// pads with popular tracks by similar artists.</summary>
        private static readonly (string Label, string Filter)[] Scopes =
        {
            ("Everything", null),
            ("Artist", "artist"),
            ("Song", "track"),
            ("Album", "album"),
        };

        // Spotify's own field filters; a colon alone (as in "Re: Your Brains") doesn't count
        private static readonly System.Text.RegularExpressions.Regex TypedFilter = new System.Text.RegularExpressions.Regex(
            @"\b(artist|track|album|year|genre|isrc|upc|tag):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Applies the chosen filter, unless the user already typed their own (e.g. year:1994).</summary>
        internal static string BuildQuery(string text, int scopeIndex)
        {
            var filter = scopeIndex >= 0 && scopeIndex < Scopes.Length ? Scopes[scopeIndex].Filter : null;
            if (filter == null || TypedFilter.IsMatch(text))
                return text;
            return $"{filter}:\"{text.Replace("\"", "")}\"";
        }

        private async Task NewSearchAsync()
        {
            lastQuery = BuildQuery(query.Text.Trim(), scope.SelectedIndex);
            nextOffset = 0;
            if (lastQuery.Length > 0)
                await LoadPageAsync(append: false);
        }

        private async Task LoadPageAsync(bool append)
        {
            if (controller.Client == null)
            {
                status.Text = "Not connected to Spotify.";
                return;
            }
            search.Enabled = more.Enabled = false;
            status.Text = "Searching Spotify…";
            try
            {
                var tracks = await controller.Client.SearchTracksPageAsync(lastQuery, nextOffset);
                if (IsDisposed)
                    return;
                nextOffset += SpotifyClient.MaxSearchResults;
                results.BeginUpdate();
                if (!append)
                    results.Items.Clear();
                var seen = new HashSet<string>(results.Items.Cast<ListViewItem>().Select(i => ((Track)i.Tag).Id));
                foreach (var t in tracks.Where(t => t?.Id != null && t.IsTrack && seen.Add(t.Id)))
                {
                    var inLibrary = actions.IsInLibrary(t, out var isLocal);
                    results.Items.Add(new ListViewItem(new[]
                    {
                        t.Name,
                        t.ArtistNames,
                        t.AlbumName,
                        TimeSpan.FromMilliseconds(t.DurationMs).ToString(@"m\:ss"),
                        isLocal ? "Your file" : inLibrary ? "Placeholder" : "",
                    })
                    { Tag = t });
                }
                results.EndUpdate();
                if (!append && results.Items.Count > 0)
                    results.Items[0].Selected = true;
                more.Enabled = tracks.Length == SpotifyClient.MaxSearchResults && nextOffset < 1000;
                status.Text = results.Items.Count == 0 ? "No results." : $"{results.Items.Count} results.";
            }
            catch (Exception ex)
            {
                status.Text = "Search failed: " + Friendly(ex);
            }
            finally
            {
                search.Enabled = true;
                UpdateButtons();
            }
        }

        // --- Actions -------------------------------------------------------

        private List<Track> Selected => results.SelectedItems.Cast<ListViewItem>().Select(i => (Track)i.Tag).ToList();

        private async Task RunAsync(Func<List<Track>, Task<string>> action)
        {
            var tracks = Selected;
            if (busy || tracks.Count == 0)
                return;
            busy = true;
            UpdateButtons();
            try
            {
                status.Text = await action(tracks);
                RefreshLibraryColumn();
            }
            catch (Exception ex)
            {
                status.Text = "Couldn't do that: " + Friendly(ex);
            }
            finally
            {
                busy = false;
                UpdateButtons();
            }
        }

        private async Task<string[]> FilesAsync(List<Track> tracks)
        {
            var progress = new Progress<ImportProgress>(p => status.Text = p.Message);
            return await actions.ToFilesAsync(tracks, progress, CancellationToken.None);
        }

        private async Task<string> PlayNowAsync(List<Track> tracks)
        {
            var files = await FilesAsync(tracks);
            return musicBee.PlayNow(files) ? $"Playing {Describe(tracks)}." : "MusicBee couldn't play that.";
        }

        private async Task<string> PlayNextAsync(List<Track> tracks)
        {
            var files = await FilesAsync(tracks);
            return musicBee.QueueNext(files) ? $"{Describe(tracks)} will play next." : "MusicBee couldn't queue that.";
        }

        private async Task<string> QueueLastAsync(List<Track> tracks)
        {
            var files = await FilesAsync(tracks);
            return musicBee.QueueLast(files) ? $"Added {Describe(tracks)} to the end of the queue." : "MusicBee couldn't queue that.";
        }

        private async Task<string> LikeAsync(List<Track> tracks)
        {
            await controller.Client.SaveToLibraryAsync(tracks.Select(t => t.Uri));
            var synced = controller.Store.GetPlaylist(PlaylistRecord.LikedSongsId) != null;
            return $"Liked {Describe(tracks)} on Spotify." + (synced ? " It'll appear in your MusicBee Liked Songs at the next sync." : "");
        }

        private void BuildPlaylistMenu()
        {
            playlistMenu.Items.Clear();
            var linked = new HashSet<string>(controller.Store.Playlists.Select(p => p.MusicBeePlaylistUrl ?? ""), StringComparer.OrdinalIgnoreCase);
            var playlists = musicBee.GetPlaylists()
                .Where(p => !musicBee.IsAutoPlaylist(p.Url))
                .OrderByDescending(p => linked.Contains(p.Url))
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var shownSeparator = false;
            foreach (var (url, name) in playlists)
            {
                var isLinked = linked.Contains(url);
                if (!isLinked && !shownSeparator && playlistMenu.Items.Count > 0)
                {
                    playlistMenu.Items.Add(new ToolStripSeparator());
                    shownSeparator = true;
                }
                var target = url;
                var label = isLinked ? name + "   (synced with Spotify)" : name;
                playlistMenu.Items.Add(label, null, async (s, e) => await RunAsync(t => AddToPlaylistAsync(t, target, name, isLinked)));
            }
            if (playlistMenu.Items.Count == 0)
                playlistMenu.Items.Add(new ToolStripMenuItem("No MusicBee playlists") { Enabled = false });
        }

        private async Task<string> AddToPlaylistAsync(List<Track> tracks, string url, string name, bool isLinked)
        {
            var files = await FilesAsync(tracks);
            if (!musicBee.AppendToPlaylist(url, files))
                return "MusicBee couldn't add to that playlist.";
            return $"Added {Describe(tracks)} to \"{name}\"." + (isLinked ? " It'll reach Spotify in a few seconds." : "");
        }

        private void OpenInSpotify()
        {
            var uri = Selected.FirstOrDefault()?.Uri;
            if (uri == null)
                return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { /* Spotify app not installed */ }
        }

        // --- Helpers -------------------------------------------------------

        private void RefreshLibraryColumn()
        {
            foreach (ListViewItem item in results.Items)
            {
                var inLibrary = actions.IsInLibrary((Track)item.Tag, out var isLocal);
                item.SubItems[4].Text = isLocal ? "Your file" : inLibrary ? "Placeholder" : "";
            }
        }

        private void UpdateButtons()
        {
            var any = results.SelectedItems.Count > 0 && !busy && controller.Client != null;
            playNow.Enabled = playNext.Enabled = queueLast.Enabled = addToPlaylist.Enabled = like.Enabled = any;
        }

        private static string Describe(List<Track> tracks) =>
            tracks.Count == 1 ? $"\"{tracks[0].Name}\"" : $"{tracks.Count} tracks";

        private static string Friendly(Exception ex) => ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                playlistMenu.Dispose();
                rowMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
