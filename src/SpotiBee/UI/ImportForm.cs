using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>Lists the user's Spotify playlists and imports the chosen ones into MusicBee.</summary>
    public sealed class ImportForm : Form
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
        private readonly ProgressBar progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 6, Style = ProgressBarStyle.Continuous };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button import = new Button { Text = "Import", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly ToolTip tips = new ToolTip();
        private CancellationTokenSource running;

        private sealed class Row
        {
            public ImportSource Source;
            public bool Importable;
            public string Reason;
        }

        public ImportForm(SpotiBeeController controller, IMusicBeeLibrary musicBee, SyncScheduler scheduler = null)
        {
            this.controller = controller;
            this.musicBee = musicBee;
            this.scheduler = scheduler;

            Text = "Import Spotify Playlists";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(720, 520);
            MinimumSize = new Size(480, 320);
            Padding = new Padding(10);
            CancelButton = close;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 48,
                Text = "Choose playlists to import into MusicBee's \"Spotify\" playlist folder. Songs you already " +
                       "have locally use your own files; the rest become Spotify placeholder tracks in " +
                       controller.Settings.EffectivePlaceholderFolder + ".",
            };
            var restriction = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = SystemColors.GrayText,
                Text = "Spotify only lets apps read playlists you own or collaborate on. Greyed-out ones can't be imported.",
            };

            list.Columns.Add("Playlist", 300);
            list.Columns.Add("Tracks", 60, HorizontalAlignment.Right);
            list.Columns.Add("Owner", 120);
            list.Columns.Add("Status", 200);
            list.ItemCheck += (s, e) =>
            {
                if (running != null || !((Row)list.Items[e.Index].Tag).Importable)
                    e.NewValue = e.CurrentValue;
            };
            list.ItemChecked += (s, e) => UpdateImportButton();
            list.Resize += (s, e) => list.Columns[3].Width = Math.Max(120, list.ClientSize.Width - 300 - 60 - 120 - 4);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            buttons.Controls.Add(import);
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(progressBar);

            Controls.Add(list);
            Controls.Add(restriction);
            Controls.Add(intro);
            Controls.Add(bottom);

            import.Click += OnImportClicked;
            Shown += async (s, e) => await LoadPlaylistsAsync();
        }

        private async System.Threading.Tasks.Task LoadPlaylistsAsync()
        {
            var client = controller.Client;
            if (client == null)
            {
                status.Text = "Connect to Spotify first (Tools > SpotiBee Settings).";
                return;
            }

            status.Text = "Loading your Spotify playlists…";
            UserProfile me;
            Playlist[] playlists;
            try
            {
                me = controller.User ?? await client.GetCurrentUserAsync();
                playlists = await client.GetMyPlaylistsAsync();
            }
            catch (Exception ex)
            {
                status.Text = "Couldn't load playlists: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
                return;
            }
            if (IsDisposed)
                return;

            list.BeginUpdate();
            list.Items.Clear();
            AddRow(new Row { Source = ImportSource.LikedSongs(), Importable = true }, "", "You");
            foreach (var p in playlists.Where(p => p != null && p.Id != null))
            {
                var mine = p.Owner?.Id == me?.Id;
                var row = new Row
                {
                    Source = ImportSource.From(p),
                    Importable = mine || p.Collaborative,
                    Reason = mine || p.Collaborative ? null : "Owned by someone else",
                };
                AddRow(row, p.ItemCount.ToString(), mine ? "You" : p.Owner?.DisplayName ?? p.Owner?.Id ?? "");
            }
            list.EndUpdate();

            var importable = list.Items.Cast<ListViewItem>().Count(i => ((Row)i.Tag).Importable);
            status.Text = $"{playlists.Length} playlists, {importable} importable (including Liked Songs).";
            UpdateImportButton();
        }

        private void AddRow(Row row, string tracks, string owner)
        {
            var previous = controller.Store.GetPlaylist(row.Source.Id);
            var item = new ListViewItem(new[]
            {
                row.Source.Name,
                tracks,
                owner,
                row.Reason ?? (previous == null ? "" :
                    "Linked, last synced " + (previous.LastSyncedUtc > previous.LastImportedUtc ? previous.LastSyncedUtc : previous.LastImportedUtc)
                        .ToLocalTime().ToString("g")),
            })
            {
                Tag = row,
                // Previously imported playlists start ticked, so re-running refreshes them
                Checked = row.Importable && previous != null,
            };
            if (!row.Importable)
                item.ForeColor = SystemColors.GrayText;
            list.Items.Add(item);
        }

        private void UpdateImportButton()
        {
            if (running != null)
                return;
            var count = list.CheckedItems.Count;
            import.Enabled = count > 0;
            import.Text = count > 1 ? $"Import {count} playlists" : "Import";
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

            var items = list.CheckedItems.Cast<ListViewItem>().ToList();
            running = new CancellationTokenSource();
            import.Text = "Cancel";
            close.Enabled = false;

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
                        // Already linked: sync instead, so MusicBee-side changes aren't overwritten
                        var link = controller.Store.GetPlaylist(row.Source.Id);
                        if (link != null && scheduler != null && musicBee.PlaylistExists(link.MusicBeePlaylistUrl))
                        {
                            var outcome = await scheduler.SyncOneAsync(link, progress, running.Token);
                            item.SubItems[3].Text = "Synced: " + outcome.Summary;
                            continue;
                        }
                        var result = await importer.ImportAsync(row.Source, progress, running.Token);
                        results.Add(result);
                        item.SubItems[3].Text = "Imported: " + result.Summary;
                    }
                    catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                    {
                        failures++;
                        row.Importable = false;
                        item.Checked = false;
                        item.ForeColor = SystemColors.GrayText;
                        item.SubItems[3].Text = "Spotify won't share this playlist's tracks";
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
                status.Text = $"Done. {results.Count} playlist(s), {tracks} tracks: {local} from your library, " +
                              $"{created} new placeholder(s)" + (failures > 0 ? $", {failures} failed." : ".");
            }
            catch (OperationCanceledException)
            {
                status.Text = "Import cancelled. Anything already imported has been kept.";
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null)
            {
                // Let the current step finish cleanly rather than abandoning half-written state
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
