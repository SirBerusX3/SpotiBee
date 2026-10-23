using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>
    /// Lists the albums saved on Spotify and adds the chosen ones to the MusicBee library, so they
    /// show in MusicBee's album view. No playlists are made.
    /// </summary>
    public sealed class AlbumImportForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly bool tickNewOnly;

        private readonly ListView list = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly ProgressBar progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 6, Style = ProgressBarStyle.Continuous };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button import = new Button { Text = "Import", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly Button selectAll = new Button { Text = "Select all", AutoSize = true, Enabled = false };
        private readonly Button selectNone = new Button { Text = "Select none", AutoSize = true, Enabled = false };
        private readonly ToolTip tips = new ToolTip();
        private readonly FilterBox filterBox = new FilterBox { Width = 280, Hint = "Filter by album or artist (Ctrl+F)", Margin = new Padding(0, 0, 0, 6) };
        private readonly ListFilter filter;
        private CancellationTokenSource running;

        private sealed class Row
        {
            public Album Album;
            public bool IsNew;
        }

        /// <param name="tickNewOnly">Tick the albums saved since the user last looked (opened from the "new album" notice).</param>
        public AlbumImportForm(SpotiBeeController controller, IMusicBeeLibrary musicBee, bool tickNewOnly = false)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            this.tickNewOnly = tickNewOnly;

            Text = "Import Saved Albums";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(760, 540);
            MinimumSize = new Size(500, 320);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Text = "Choose albums from your Spotify library to add to MusicBee, where they show in the album view " +
                       "like any other album. Songs you already have use your own files; the rest become Spotify " +
                       "placeholder tracks in " + controller.Settings.EffectivePlaceholderFolder + ".",
            };

            list.Columns.Add("Album", 260);
            list.Columns.Add("Artist", 170);
            list.Columns.Add("Tracks", 55, HorizontalAlignment.Right);
            list.Columns.Add("Status", 200);
            list.Resize += (s, e) => list.Columns[3].Width = Math.Max(120, list.ClientSize.Width - 260 - 170 - 55 - 4);
            filter = new ListFilter(list, filterBox) { MatchText = item => item.Text + " " + item.SubItems[1].Text };
            list.ItemCheck += (s, e) =>
            {
                if (!filter.Changing && running != null)
                    e.NewValue = e.CurrentValue;
            };
            filter.CheckedChanged += UpdateImportButton;
            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            filterRow.Controls.Add(filterBox);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(import);
            buttons.Controls.Add(close);
            var selection = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
            selection.Controls.Add(selectAll);
            selection.Controls.Add(selectNone);
            selectAll.Click += (s, e) => SetAllChecked(true);
            selectNone.Click += (s, e) => SetAllChecked(false);
            bottom.Controls.Add(status);
            bottom.Controls.Add(selection);
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(progressBar);

            Controls.Add(list);
            Controls.Add(filterRow);
            Controls.Add(intro);
            Controls.Add(bottom);

            import.Click += OnImportClicked;
            Shown += async (s, e) => await LoadAlbumsAsync();
        }

        private async System.Threading.Tasks.Task LoadAlbumsAsync()
        {
            var client = controller.Client;
            if (client == null)
            {
                status.Text = "Connect to Spotify first (Tools > SpotiBee Settings).";
                return;
            }

            status.Text = "Loading your saved albums…";
            SavedAlbum[] saved;
            try
            {
                saved = await client.GetSavedAlbumsAsync(new Progress<int>(n => status.Text = $"Loading your saved albums… {n}"));
            }
            catch (Exception ex)
            {
                status.Text = "Couldn't load your saved albums: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
                return;
            }
            if (IsDisposed)
                return;

            var store = controller.Store;
            var since = store.AlbumsSeenUntilUtc;
            var albums = saved.Where(s => s?.Album?.Id != null).ToList();
            var items = albums.Select(s => MakeRow(new Row
            {
                Album = s.Album,
                // Before saved albums were first looked at, nothing counts as new
                IsNew = since != null && s.AddedUtc > since && store.GetAlbum(s.Album.Id) == null,
            })).ToList();
            filter.SetItems(items);
            if (tickNewOnly)
                filter.CheckedItems.FirstOrDefault(i => i.ListView != null)?.EnsureVisible();

            // Everything listed has now been seen, so the panel stops announcing it
            SyncScheduler.MarkAlbumsSeen(store, albums);
            try { store.Save(); }
            catch (Exception ex) { Diagnostics.Log("Saving library", ex); }

            var imported = albums.Count(s => store.GetAlbum(s.Album.Id) != null);
            var fresh = items.Count(i => ((Row)i.Tag).IsNew);
            status.Text = albums.Count == 0
                ? "There are no albums in your Spotify library."
                : $"{albums.Count} saved album{(albums.Count == 1 ? "" : "s")}, {imported} imported" + (fresh > 0 ? $", {fresh} new." : ".");
            UpdateImportButton();
        }

        private ListViewItem MakeRow(Row row)
        {
            var previous = controller.Store.GetAlbum(row.Album.Id);
            return new ListViewItem(new[]
            {
                row.Album.Name ?? "",
                row.Album.ArtistNames,
                row.Album.TotalTracks > 0 ? row.Album.TotalTracks.ToString() : "",
                previous != null
                    ? "Imported " + previous.LastImportedUtc.ToLocalTime().ToString("g")
                    : row.IsNew ? "New on Spotify" : "",
            })
            {
                Tag = row,
                Checked = tickNewOnly && row.IsNew,
            };
        }

        /// <summary>Ticks or unticks the rows the filter is showing.</summary>
        private void SetAllChecked(bool value)
        {
            list.BeginUpdate();
            foreach (var item in list.Items.Cast<ListViewItem>().ToList())
                filter.SetChecked(item, value);
            list.EndUpdate();
        }

        private void UpdateImportButton()
        {
            selectAll.Enabled = selectNone.Enabled = running == null && list.Items.Count > 0;
            filterBox.Enabled = running == null;
            if (running != null)
                return;
            var ticked = filter.CheckedItems;
            var hidden = filter.HiddenCheckedCount;
            var tracks = ticked.Sum(i => ((Row)i.Tag).Album.TotalTracks);
            import.Enabled = ticked.Count > 0;
            import.Text = ticked.Count == 0
                ? "Import"
                : $"Import {ticked.Count} album{(ticked.Count == 1 ? "" : "s")} ({tracks} track{(tracks == 1 ? "" : "s")})" +
                  (hidden > 0 ? $", {hidden} hidden" : "");
            tips.SetToolTip(import, hidden > 0
                ? $"{hidden} ticked album{(hidden == 1 ? " is" : "s are")} hidden by the filter and will be imported too."
                : null);
        }

        private async void OnImportClicked(object sender, EventArgs e)
        {
            if (running != null)
            {
                running.Cancel();
                import.Enabled = false;
                return;
            }
            var client = controller.Client;
            if (client == null)
            {
                status.Text = "Not connected to Spotify.";
                return;
            }

            var items = filter.CheckedItems;
            running = new CancellationTokenSource();
            import.Text = "Cancel";
            close.Enabled = false;
            UpdateImportButton();

            var placeholders = new PlaceholderWriter(controller.Http, controller.Settings.EffectivePlaceholderFolder);
            var importer = new PlaylistImporter(client, musicBee, controller.Store, placeholders);
            var progress = new Progress<ImportProgress>(p =>
            {
                status.Text = p.Message;
                if (p.Total > 0)
                {
                    progressBar.Maximum = p.Total;
                    progressBar.Value = Math.Min(p.Done, p.Total);
                }
            });

            var results = new List<ImportResult>();
            var failures = 0;
            try
            {
                foreach (var item in items)
                {
                    running.Token.ThrowIfCancellationRequested();
                    var row = (Row)item.Tag;
                    item.SubItems[3].Text = "Importing…";
                    progressBar.Value = 0;
                    try
                    {
                        var result = await importer.ImportAlbumAsync(row.Album, progress, running.Token);
                        results.Add(result);
                        filter.SetChecked(item, false);
                        item.SubItems[3].Text = "Imported: " + result.Summary;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        failures++;
                        item.SubItems[3].Text = "Failed: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
                    }
                }

                var tracks = results.Sum(r => r.Tracks);
                var local = results.Sum(r => r.LocalMatches);
                var created = results.Sum(r => r.PlaceholdersCreated);
                status.Text = $"Done. {results.Count} album(s), {tracks} tracks: {local} from your library, " +
                              $"{created} new placeholder(s)" + (failures > 0 ? $", {failures} failed." : ".");
            }
            catch (OperationCanceledException)
            {
                status.Text = "Import cancelled. Albums already imported have been kept.";
            }
            finally
            {
                running.Dispose();
                running = null;
                close.Enabled = true;
                progressBar.Value = 0;
                UpdateImportButton();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                filterBox.Focus();
                filterBox.SelectAll();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null)
            {
                // Let the current album finish cleanly rather than abandoning half-written state
                running.Cancel();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
