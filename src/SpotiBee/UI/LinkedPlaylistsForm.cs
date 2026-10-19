using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SpotiBee.Library;

namespace SpotiBee.UI
{
    /// <summary>Lists every MusicBee ↔ Spotify playlist link, with ways to sync, pause or unlink each one.</summary>
    public sealed class LinkedPlaylistsForm : Form
    {
        private readonly SpotiBeeController controller;
        private readonly SyncScheduler scheduler;

        private readonly ListView list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly ProgressBar progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 6, Style = ProgressBarStyle.Continuous };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button syncNow = new Button { Text = "Sync now", AutoSize = true, Enabled = false };
        private readonly Button pause = new Button { Text = "Pause sync", AutoSize = true, Enabled = false };
        private readonly Button unlink = new Button { Text = "Unlink…", AutoSize = true, Enabled = false };
        private readonly Button openInSpotify = new Button { Text = "Open in Spotify", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly ToolTip tips = new ToolTip();
        private CancellationTokenSource running;

        public LinkedPlaylistsForm(SpotiBeeController controller, SyncScheduler scheduler)
        {
            this.controller = controller;
            this.scheduler = scheduler;

            Text = "Linked Playlists";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(820, 480);
            MinimumSize = new Size(560, 300);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Text = "These MusicBee playlists are kept in step with Spotify. Paused ones are left alone until " +
                       "you resume them or click Sync now. Unlinking keeps both playlists as they are.",
            };

            list.Columns.Add("MusicBee playlist", 220);
            list.Columns.Add("Spotify playlist", 180);
            list.Columns.Add("Last synced", 120);
            list.Columns.Add("Last result", 240);
            list.Resize += (s, e) => list.Columns[3].Width = Math.Max(160, list.ClientSize.Width - 220 - 180 - 120 - 4);
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.A && e.Control)
                    foreach (ListViewItem item in list.Items)
                        item.Selected = true;
                else if (e.KeyCode == Keys.Delete && unlink.Enabled)
                    OnUnlinkClicked(this, EventArgs.Empty);
            };

            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            actions.Controls.Add(syncNow);
            actions.Controls.Add(pause);
            actions.Controls.Add(unlink);
            actions.Controls.Add(openInSpotify);
            tips.SetToolTip(syncNow, "Sync the selected playlists now, paused ones included");
            tips.SetToolTip(pause, "Stop or restart automatic syncing for the selected playlists");
            tips.SetToolTip(unlink, "Stop syncing the selected playlists. Both the MusicBee and Spotify playlists are kept.");

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(progressBar);

            Controls.Add(list);
            Controls.Add(intro);
            Controls.Add(actions);
            Controls.Add(bottom);

            syncNow.Click += OnSyncNowClicked;
            pause.Click += (s, e) => OnPauseClicked();
            unlink.Click += OnUnlinkClicked;
            openInSpotify.Click += (s, e) => OpenSelectedInSpotify();
            list.DoubleClick += (s, e) => OpenSelectedInSpotify();

            LoadLinks();
        }

        private void LoadLinks()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var link in controller.Store.Playlists.OrderBy(l => l.MusicBeeName ?? l.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ListViewItem(new[] { "", "", "", "" }) { Tag = link };
                Fill(item);
                list.Items.Add(item);
            }
            list.EndUpdate();
            UpdateButtons();
            ShowSummary();
        }

        private static void Fill(ListViewItem item)
        {
            var link = (PlaylistRecord)item.Tag;
            var last = link.LastSyncedUtc > link.LastImportedUtc ? link.LastSyncedUtc : link.LastImportedUtc;
            item.SubItems[0].Text = link.MusicBeeName ?? link.Name;
            item.SubItems[1].Text = link.IsLikedSongs ? "Liked Songs" : link.Name;
            item.SubItems[2].Text = last == default ? "Never" : last.ToLocalTime().ToString("g");
            item.SubItems[3].Text = link.SyncPaused
                ? "Paused" + (string.IsNullOrEmpty(link.LastResult) ? "" : " (last time: " + link.LastResult + ")")
                : link.LastResult ?? "";
            item.ForeColor = link.SyncPaused ? SystemColors.GrayText : SystemColors.WindowText;
        }

        private void ShowSummary()
        {
            var links = controller.Store.Playlists;
            if (links.Count == 0)
            {
                status.Text = "No playlists are linked yet. Import some from Spotify, or send MusicBee playlists to Spotify.";
                return;
            }
            var paused = links.Count(l => l.SyncPaused);
            status.Text = $"{links.Count} linked" + (paused > 0 ? $", {paused} paused" : "") +
                          (controller.Settings.DisableAutoSync
                              ? ". Automatic sync is off in Settings, so they only sync when you ask."
                              : ".");
        }

        private List<ListViewItem> SelectedItems => list.SelectedItems.Cast<ListViewItem>().ToList();

        private void UpdateButtons()
        {
            var selected = SelectedItems;
            var any = selected.Count > 0;
            var idle = running == null;
            syncNow.Enabled = (any && idle) || !idle;
            syncNow.Text = idle ? "Sync now" : "Cancel";
            pause.Enabled = unlink.Enabled = any && idle;
            pause.Text = any && selected.All(i => ((PlaylistRecord)i.Tag).SyncPaused) ? "Resume sync" : "Pause sync";
            openInSpotify.Enabled = selected.Count == 1;
            close.Enabled = idle;
        }

        private async void OnSyncNowClicked(object sender, EventArgs e)
        {
            if (running != null)
            {
                running.Cancel();
                syncNow.Enabled = false;
                return;
            }
            if (controller.Client == null)
            {
                status.Text = "Connect to Spotify first (Tools > SpotiBee Settings).";
                return;
            }

            var items = SelectedItems;
            running = new CancellationTokenSource();
            UpdateButtons();
            var progress = new Progress<ImportProgress>(p =>
            {
                status.Text = p.Message;
                if (p.Total > 0)
                {
                    progressBar.Maximum = p.Total;
                    progressBar.Value = Math.Min(p.Done, p.Total);
                }
            });

            var failures = 0;
            try
            {
                if (scheduler.IsRunning)
                    status.Text = "Waiting for the sync that's already running…";
                foreach (var item in items)
                {
                    running.Token.ThrowIfCancellationRequested();
                    var link = (PlaylistRecord)item.Tag;
                    item.SubItems[3].Text = "Syncing…";
                    progressBar.Value = 0;
                    try
                    {
                        var outcome = await scheduler.SyncOneAsync(link, progress, running.Token);
                        if (outcome.Unlinked)
                        {
                            // The MusicBee playlist was deleted, so the link went with it
                            item.SubItems[3].Text = outcome.Note;
                            item.ForeColor = SystemColors.GrayText;
                            item.Tag = null;
                            continue;
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // The scheduler recorded the failure on the link
                        failures++;
                    }
                    Fill(item);
                }
                status.Text = failures == 0 ? "Done." : $"Done, {failures} failed. See the Last result column.";
            }
            catch (OperationCanceledException)
            {
                foreach (var item in items.Where(i => i.Tag != null))
                    Fill(item);
                status.Text = "Sync cancelled.";
            }
            finally
            {
                running.Dispose();
                running = null;
                progressBar.Value = 0;
                foreach (var item in items.Where(i => i.Tag == null).ToList())
                    list.Items.Remove(item);
                UpdateButtons();
            }
        }

        private void OnPauseClicked()
        {
            var items = SelectedItems;
            var resume = items.All(i => ((PlaylistRecord)i.Tag).SyncPaused);
            foreach (var item in items)
            {
                ((PlaylistRecord)item.Tag).SyncPaused = !resume;
                Fill(item);
            }
            Save();
            UpdateButtons();
            ShowSummary();
            if (resume)
                status.Text = "Resumed. Changes made while paused on either side are merged at the next sync.";
        }

        private void OnUnlinkClicked(object sender, EventArgs e)
        {
            var items = SelectedItems;
            if (items.Count == 0)
                return;
            var first = (PlaylistRecord)items[0].Tag;
            var question = items.Count == 1
                ? $"Stop syncing \"{first.MusicBeeName ?? first.Name}\" with Spotify?"
                : $"Stop syncing these {items.Count} playlists with Spotify?";
            var answer = MessageBox.Show(this,
                question + "\n\nThe MusicBee and Spotify playlists are both kept as they are; they just won't be kept " +
                "in step any more. Importing from Spotify again links them again.",
                "Unlink playlists", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            foreach (var item in items)
            {
                controller.Store.RemovePlaylist(((PlaylistRecord)item.Tag).SpotifyId);
                list.Items.Remove(item);
            }
            Save();
            UpdateButtons();
            ShowSummary();
        }

        private void OpenSelectedInSpotify()
        {
            var item = SelectedItems.FirstOrDefault();
            if (!(item?.Tag is PlaylistRecord link))
                return;
            var (app, web) = link.IsLikedSongs
                ? ("spotify:collection:tracks", "https://open.spotify.com/collection/tracks")
                : ("spotify:playlist:" + link.SpotifyId, "https://open.spotify.com/playlist/" + link.SpotifyId);
            try { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true }); }
            catch
            {
                // Spotify desktop app not installed
                try { Process.Start(new ProcessStartInfo(web) { UseShellExecute = true }); }
                catch { /* no browser either */ }
            }
        }

        private void Save()
        {
            try { controller.Store.Save(); }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't save: " + ex.Message, "SpotiBee"); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null)
            {
                // Let the current playlist finish cleanly rather than abandoning half-written state
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
