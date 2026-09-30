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
    /// <summary>Creates Spotify playlists from MusicBee playlists (matching local files on Spotify) and links them.</summary>
    public sealed class SendToSpotifyForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly IMusicBeeLibrary musicBee;
        private readonly SyncScheduler scheduler;

        private readonly ListView list = new ListView
        {
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly TextBox details = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 90,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Visible = false,
        };
        private readonly ProgressBar progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 6, Style = ProgressBarStyle.Continuous };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button send = new Button { Text = "Send to Spotify", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private CancellationTokenSource running;

        public SendToSpotifyForm(SpotiBeeController controller, IMusicBeeLibrary musicBee, SyncScheduler scheduler)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            this.scheduler = scheduler;

            Text = "Send Playlists to Spotify";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(720, 540);
            MinimumSize = new Size(480, 320);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Text = "Each ticked playlist becomes a private Spotify playlist and stays in sync both ways. " +
                       "Your local files are matched to Spotify's catalogue by title, artist and length; " +
                       "anything Spotify doesn't have stays in MusicBee only.",
            };

            list.Columns.Add("MusicBee playlist", 320);
            list.Columns.Add("Tracks", 60, HorizontalAlignment.Right);
            list.Columns.Add("Status", 280);
            list.ItemCheck += (s, e) =>
            {
                if (running != null)
                    e.NewValue = e.CurrentValue;
            };
            list.ItemChecked += (s, e) => UpdateSendButton();
            list.Resize += (s, e) => list.Columns[2].Width = Math.Max(160, list.ClientSize.Width - 320 - 60 - 4);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(send);
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(progressBar);

            Controls.Add(list);
            Controls.Add(details);
            Controls.Add(intro);
            Controls.Add(bottom);

            send.Click += OnSendClicked;
            Shown += (s, e) => LoadPlaylists();
        }

        private void LoadPlaylists()
        {
            if (controller.Client == null)
            {
                status.Text = "Connect to Spotify first (Tools > SpotiBee Settings).";
                return;
            }
            var linked = new HashSet<string>(controller.Store.Playlists.Select(p => p.MusicBeePlaylistUrl ?? ""), StringComparer.OrdinalIgnoreCase);
            var candidates = musicBee.GetPlaylists()
                .Where(p => !linked.Contains(p.Url))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            list.BeginUpdate();
            foreach (var (url, name) in candidates)
            {
                var auto = musicBee.IsAutoPlaylist(url);
                var item = new ListViewItem(new[]
                {
                    name,
                    musicBee.GetPlaylistFiles(url).Length.ToString(),
                    auto ? "Auto-playlist: changes only go MusicBee → Spotify" : "",
                })
                { Tag = url };
                list.Items.Add(item);
            }
            list.EndUpdate();
            status.Text = candidates.Count == 0
                ? "Every MusicBee playlist is already linked to Spotify."
                : $"{candidates.Count} playlists not yet on Spotify.";
        }

        private void UpdateSendButton()
        {
            if (running != null)
                return;
            var count = list.CheckedItems.Count;
            send.Enabled = count > 0;
            send.Text = count > 1 ? $"Send {count} playlists" : "Send to Spotify";
        }

        private async void OnSendClicked(object sender, EventArgs e)
        {
            if (running != null)
            {
                running.Cancel();
                send.Enabled = false;
                return;
            }

            var items = list.CheckedItems.Cast<ListViewItem>().ToList();
            running = new CancellationTokenSource();
            send.Text = "Cancel";
            close.Enabled = false;
            details.Visible = false;
            var progress = new Progress<ImportProgress>(p =>
            {
                status.Text = p.Message;
                if (p.Total > 0)
                {
                    progressBar.Maximum = p.Total;
                    progressBar.Value = Math.Min(p.Done, p.Total);
                }
            });

            var unmatched = new List<string>();
            var sent = 0;
            try
            {
                foreach (var item in items)
                {
                    running.Token.ThrowIfCancellationRequested();
                    item.SubItems[2].Text = "Sending…";
                    progressBar.Value = 0;
                    try
                    {
                        var outcome = await scheduler.SendToSpotifyAsync((string)item.Tag, progress, running.Token);
                        sent++;
                        item.Checked = false;
                        item.ForeColor = SystemColors.GrayText;
                        item.SubItems[2].Text = $"On Spotify: {outcome.SentToSpotify} tracks" +
                                                (outcome.Unmatched.Count > 0 ? $", {outcome.Unmatched.Count} not on Spotify" : "");
                        unmatched.AddRange(outcome.Unmatched.Select(u => $"{item.Text}: {u}"));
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        item.SubItems[2].Text = "Failed: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
                    }
                }
                status.Text = $"Done. {sent} playlist(s) created on Spotify and linked." +
                              (unmatched.Count > 0 ? $" {unmatched.Count} track(s) aren't on Spotify (listed below)." : "");
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelled. Playlists already sent stay linked.";
            }
            finally
            {
                running.Dispose();
                running = null;
                close.Enabled = true;
                progressBar.Value = 0;
                UpdateSendButton();
            }

            if (unmatched.Count > 0)
            {
                details.Text = "Not found on Spotify (kept in MusicBee only):\r\n" + string.Join("\r\n", unmatched);
                details.Visible = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null)
            {
                running.Cancel();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
