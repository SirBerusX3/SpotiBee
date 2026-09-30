using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SpotiBee.Library;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>Search Spotify and pick one track. Shows how each result's length compares to a local file.</summary>
    public sealed class SpotifyPickerForm : Form
    {
        private readonly ISpotifySearch spotify;
        private readonly TimeSpan? localLength;

        private readonly TextBox query = new TextBox { Dock = DockStyle.Fill };
        private readonly Button search = new Button { Text = "Search", AutoSize = true };
        private readonly ListView results = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button choose = new Button { Text = "Use this track", AutoSize = true, Enabled = false };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        public SpotifyPickerForm(ISpotifySearch spotify, string title, string initialQuery, TimeSpan? localLength)
        {
            this.spotify = spotify;
            this.localLength = localLength;

            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(760, 420);
            MinimumSize = new Size(480, 280);
            Padding = new Padding(10);
            AcceptButton = search;
            CancelButton = cancel;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 2 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(query, 0, 0);
            top.Controls.Add(search, 1, 0);
            query.Text = initialQuery;

            results.Columns.Add("Title", 260);
            results.Columns.Add("Artist", 150);
            results.Columns.Add("Album", 160);
            results.Columns.Add("Length", 60, HorizontalAlignment.Right);
            results.Columns.Add(localLength.HasValue ? "vs yours" : "", 70, HorizontalAlignment.Right);
            results.SelectedIndexChanged += (s, e) => choose.Enabled = results.SelectedItems.Count == 1;
            results.DoubleClick += (s, e) => { if (results.SelectedItems.Count == 1) Choose(); };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(choose);
            buttons.Controls.Add(cancel);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);

            Controls.Add(results);
            Controls.Add(top);
            Controls.Add(bottom);

            search.Click += async (s, e) => await SearchAsync();
            choose.Click += (s, e) => Choose();
            Shown += async (s, e) => await SearchAsync();
        }

        public Track Selected { get; private set; }

        private async System.Threading.Tasks.Task SearchAsync()
        {
            var q = query.Text.Trim();
            if (q.Length == 0)
                return;
            search.Enabled = false;
            status.Text = "Searching Spotify…";
            try
            {
                var tracks = await spotify.SearchTracksAsync(q, SpotifyClient.MaxSearchResults);
                if (IsDisposed)
                    return;
                results.BeginUpdate();
                results.Items.Clear();
                foreach (var t in tracks.Where(t => t?.Id != null && t.IsTrack))
                {
                    var length = TimeSpan.FromMilliseconds(t.DurationMs);
                    var diff = localLength.HasValue ? length - localLength.Value : (TimeSpan?)null;
                    var item = new ListViewItem(new[]
                    {
                        t.Name,
                        t.ArtistNames,
                        t.AlbumName,
                        Format(length),
                        diff == null ? "" : Math.Abs(diff.Value.TotalSeconds) < 1 ? "same" : (diff.Value < TimeSpan.Zero ? "−" : "+") + Format(diff.Value.Duration()),
                    })
                    { Tag = t };
                    if (diff.HasValue && diff.Value.Duration() <= TrackResolver.ExactLength)
                        item.Font = new Font(results.Font, FontStyle.Bold);
                    results.Items.Add(item);
                }
                results.EndUpdate();
                // Keep Spotify's relevance order, but start on the closest length to the user's file
                var best = results.Items.Cast<ListViewItem>()
                    .OrderBy(i => localLength.HasValue
                        ? Math.Abs(((Track)i.Tag).DurationMs - localLength.Value.TotalMilliseconds)
                        : i.Index)
                    .FirstOrDefault();
                if (best != null)
                {
                    best.Selected = true;
                    best.EnsureVisible();
                }
                status.Text = results.Items.Count == 0
                    ? "No results. Try fewer words, or just the song title."
                    : $"{results.Items.Count} results (Spotify returns at most {SpotifyClient.MaxSearchResults}). Bold ones are the same length as your file.";
            }
            catch (Exception ex)
            {
                status.Text = "Search failed: " + (ex is SpotifyApiException api ? api.FriendlyMessage : ex.Message);
            }
            finally
            {
                search.Enabled = true;
            }
        }

        private void Choose()
        {
            Selected = (Track)results.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
