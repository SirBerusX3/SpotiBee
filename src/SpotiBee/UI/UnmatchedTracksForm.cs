using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SpotiBee.Library;

namespace SpotiBee.UI
{
    /// <summary>
    /// Local files in linked playlists that couldn't be matched on Spotify automatically,
    /// with a way to pick the right Spotify track by hand.
    /// </summary>
    public sealed class UnmatchedTracksForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly SyncScheduler scheduler;

        private readonly ListView list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button choose = new Button { Text = "Choose Spotify track…", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private int matchedByHand;

        private sealed class Row
        {
            public string File;
            public LocalTrack Track;
        }

        public UnmatchedTracksForm(SpotiBeeController controller, IMusicBeeLibrary musicBee, SyncScheduler scheduler)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            this.scheduler = scheduler;

            Text = "Tracks Not Found on Spotify";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(760, 440);
            MinimumSize = new Size(480, 280);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Text = "These tracks in your synced playlists weren't matched on Spotify automatically, usually because " +
                       "your copy is a noticeably different edit. Choose a Spotify track to use for one, and it's added to those playlists.",
            };

            list.Columns.Add("Track", 300);
            list.Columns.Add("Length", 60, HorizontalAlignment.Right);
            list.Columns.Add("In playlists", 340);
            list.SelectedIndexChanged += (s, e) => choose.Enabled = list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is Row;
            list.DoubleClick += (s, e) => Choose();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(choose);
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);

            Controls.Add(list);
            Controls.Add(intro);
            Controls.Add(bottom);

            choose.Click += (s, e) => Choose();
            Shown += (s, e) => Reload();
        }

        private void Reload()
        {
            // Which linked playlists each unmatched file appears in
            var playlistsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in controller.Store.Playlists)
            {
                if (!musicBee.PlaylistExists(link.MusicBeePlaylistUrl))
                    continue;
                foreach (var file in musicBee.GetPlaylistFiles(link.MusicBeePlaylistUrl).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!playlistsByFile.TryGetValue(file, out var names))
                        playlistsByFile[file] = names = new List<string>();
                    names.Add(link.MusicBeeName ?? link.Name);
                }
            }

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var file in controller.Store.UnmatchedPaths)
            {
                if (!playlistsByFile.TryGetValue(file, out var names) || controller.Store.FindByPath(file) != null)
                    continue;
                var track = musicBee.GetTrack(file);
                var label = track == null || string.IsNullOrWhiteSpace(track.Title)
                    ? Path.GetFileNameWithoutExtension(file)
                    : $"{LocalMatcher.FirstArtist(track.Artist)} – {track.Title}";
                list.Items.Add(new ListViewItem(new[]
                {
                    label,
                    track?.Duration is TimeSpan d ? d.ToString(@"m\:ss") : "",
                    string.Join(", ", names),
                })
                { Tag = new Row { File = file, Track = track } });
            }
            list.EndUpdate();
            if (list.Items.Count > 0)
                list.Items[0].Selected = true;
            status.Text = list.Items.Count == 0
                ? "Every track in your synced playlists is matched on Spotify (or has no Spotify version)."
                : $"{list.Items.Count} track(s) not matched.";
        }

        private void Choose()
        {
            if (list.SelectedItems.Count != 1 || !(list.SelectedItems[0].Tag is Row row) || controller.Client == null)
                return;

            var query = row.Track == null
                ? Path.GetFileNameWithoutExtension(row.File)
                : $"{LocalMatcher.SearchArtist(row.Track.Artist)} {LocalMatcher.TitleVariants(row.Track.Title).LastOrDefault() ?? row.Track.Title}".Trim();
            using var picker = new SpotifyPickerForm(controller.Client, "Choose the Spotify track for " + list.SelectedItems[0].Text,
                query, row.Track);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null)
                return;

            var picked = picker.Selected;
            var lengthDiffers = row.Track?.Duration is TimeSpan local &&
                                (TimeSpan.FromMilliseconds(picked.DurationMs) - local).Duration() > TrackResolver.ExactLength;
            controller.Store.PinMatch(row.File, picked, lengthDiffers);
            controller.Store.Save();
            matchedByHand++;

            var item = list.SelectedItems[0];
            item.Tag = null;
            item.ForeColor = SystemColors.GrayText;
            item.SubItems[2].Text = $"Matched to {picked.ArtistNames} – {picked.Name}; syncing when you close this window";
            choose.Enabled = false;
            status.Text = $"{matchedByHand} matched by hand. They'll be added to Spotify when this window closes.";
        }

        protected override async void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (matchedByHand > 0 && scheduler != null)
            {
                controller.ShowStatus($"Adding {matchedByHand} hand-matched track(s) to Spotify…");
                var outcomes = await scheduler.SyncAllAsync();
                if (outcomes == null)
                    controller.ShowStatus("Hand-matched tracks will be added on the next sync.");
            }
        }
    }
}
