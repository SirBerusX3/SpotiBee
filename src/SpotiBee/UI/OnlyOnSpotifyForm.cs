using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpotiBee.Library;

namespace SpotiBee.UI
{
    /// <summary>Songs the user only has as Spotify placeholders, grouped by album, with a CSV export.</summary>
    public sealed class OnlyOnSpotifyForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;

        private readonly ListView list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowGroups = true,
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button export = new Button { Text = "Export CSV…", AutoSize = true, Enabled = false };
        private readonly Button openInSpotify = new Button { Text = "Open in Spotify", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly FilterBox filterBox = new FilterBox { Width = 280, Hint = "Filter by song, artist or album (Ctrl+F)", Margin = new Padding(0, 0, 0, 6) };
        private readonly ToolTip tips = new ToolTip();
        private List<OnlyOnSpotify.Entry> entries = new List<OnlyOnSpotify.Entry>();
        private List<OnlyOnSpotify.Entry> shown = new List<OnlyOnSpotify.Entry>();

        public OnlyOnSpotifyForm(SpotiBeeController controller, IMusicBeeLibrary musicBee)
        {
            this.controller = controller;
            this.musicBee = musicBee;

            Text = "Songs Only on Spotify";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(820, 560);
            MinimumSize = new Size(520, 320);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Text = "Songs in your playlists and imported albums that you only have from Spotify. Export the list if " +
                       "you'd like to buy them: once your own copy is in your MusicBee library, SpotiBee uses it in " +
                       "place of the placeholder automatically.",
            };

            list.Columns.Add("Song", 280);
            list.Columns.Add("Artist", 190);
            list.Columns.Add("Length", 60, HorizontalAlignment.Right);
            list.Columns.Add("In", 200);
            list.Resize += (s, e) => list.Columns[3].Width = Math.Max(120, list.ClientSize.Width - 280 - 190 - 60 - 4);
            list.SelectedIndexChanged += (s, e) => openInSpotify.Enabled = list.SelectedItems.Count == 1;
            list.DoubleClick += (s, e) => OpenSelected();
            filterBox.TextChanged += (s, e) => ShowEntries(filterBox.Text);
            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            filterRow.Controls.Add(filterBox);

            tips.SetToolTip(export, "Save the songs shown (all of them, or what the filter matches) as a CSV file for a spreadsheet");
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(openInSpotify);
            buttons.Controls.Add(export);
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);

            Controls.Add(list);
            Controls.Add(filterRow);
            Controls.Add(intro);
            Controls.Add(bottom);

            export.Click += (s, e) => Export();
            openInSpotify.Click += (s, e) => OpenSelected();
            Shown += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            status.Text = "Looking through your playlists and albums…";
            try
            {
                var playlists = OnlyOnSpotify.ReadPlaylists(musicBee);
                // Thousands of tag lookups; keep the window responsive
                entries = await Task.Run(() => OnlyOnSpotify.Build(controller.Store, musicBee, playlists));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Songs only on Spotify", ex);
                status.Text = "Couldn't build the list: " + ex.Message;
                return;
            }
            if (IsDisposed)
                return;
            ShowEntries(filterBox.Text);
        }

        private void ShowEntries(string filterText)
        {
            var words = (filterText ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            shown = entries.Where(e => words.All(w =>
                (e.Title + " " + e.Artist + " " + e.AlbumArtist + " " + e.Album).IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();

            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            foreach (var album in shown.GroupBy(e => (e.AlbumArtist, e.Album)))
            {
                var count = album.Count();
                var group = new ListViewGroup($"{album.Key.Album} — {album.Key.AlbumArtist} ({count} song{(count == 1 ? "" : "s")})");
                list.Groups.Add(group);
                foreach (var e in album)
                    list.Items.Add(new ListViewItem(new[] { e.Title, e.Artist, OnlyOnSpotify.FormatLength(e.Duration), string.Join(", ", e.UsedIn) }, group)
                    {
                        Tag = e,
                    });
            }
            list.EndUpdate();

            var albums = shown.Select(e => (e.AlbumArtist, e.Album)).Distinct().Count();
            status.Text = entries.Count == 0
                ? "Every song in your playlists and imported albums is one you have yourself."
                : $"{shown.Count} song{(shown.Count == 1 ? "" : "s")} on {albums} album{(albums == 1 ? "" : "s")}" +
                  (shown.Count < entries.Count ? $" (of {entries.Count})" : "") + " only on Spotify.";
            export.Enabled = shown.Count > 0;
            openInSpotify.Enabled = list.SelectedItems.Count == 1;
        }

        private void Export()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Export songs only on Spotify",
                Filter = "CSV file (*.csv)|*.csv",
                FileName = "Songs only on Spotify.csv",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                OnlyOnSpotify.WriteCsv(dialog.FileName, shown);
                status.Text = $"Exported {shown.Count} song(s) to {dialog.FileName}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the file: " + ex.Message, "SpotiBee", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenSelected()
        {
            if (list.SelectedItems.Count != 1 || !(list.SelectedItems[0].Tag is OnlyOnSpotify.Entry entry))
                return;
            try { Process.Start(new ProcessStartInfo("spotify:track:" + entry.Record.SpotifyId) { UseShellExecute = true }); }
            catch
            {
                // Spotify desktop app not installed
                try { Process.Start(new ProcessStartInfo(entry.SpotifyLink) { UseShellExecute = true }); }
                catch { /* no browser either */ }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
