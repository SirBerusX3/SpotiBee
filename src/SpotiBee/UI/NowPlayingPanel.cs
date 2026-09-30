using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>Dockable MusicBee panel showing what Spotify is playing, with transport controls.</summary>
    public sealed class NowPlayingPanel : UserControl
    {
        private const string IconFontName = "Segoe MDL2 Assets";
        private static readonly HttpClient ArtworkHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly SpotiBeeController controller;
        private readonly SkinColours skin;
        private readonly Action openSettings;
        private readonly float scale;
        private readonly bool hasIconFont;

        private readonly PictureBox artwork = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom };
        private readonly Label title = new Label { AutoEllipsis = true, Cursor = Cursors.Hand };
        private readonly Label artist = new Label { AutoEllipsis = true };
        private readonly Label album = new Label { AutoEllipsis = true };
        private readonly SeekBar seekBar = new SeekBar();
        private readonly Label elapsed = new Label { TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label duration = new Label { TextAlign = ContentAlignment.MiddleRight };
        private readonly Button shuffle, previous, playPause, next, repeat;
        private readonly ComboBox devices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        private readonly Label status = new Label { AutoEllipsis = true };
        private readonly Panel disconnected = new Panel();
        private readonly Label disconnectedText = new Label { TextAlign = ContentAlignment.MiddleCenter };
        private readonly Button connectButton = new Button { Text = "Connect to Spotify…", AutoSize = true };
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer ticker = new Timer { Interval = 250 };
        private readonly Timer statusClear = new Timer { Interval = 8000 };

        private PlaybackSnapshot snapshot;
        private string artworkUrl;
        private bool updatingDevices;
        private bool initialised;

        public NowPlayingPanel(SpotiBeeController controller, SkinColours skin, Action openSettings)
        {
            this.controller = controller;
            this.skin = skin;
            this.openSettings = openSettings;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            // Not CreateGraphics(): that creates the handle, firing OnHandleCreated before the controls exist
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                scale = g.DpiY / 96f;

            using (var probe = new Font(IconFontName, 10f))
                hasIconFont = probe.Name == IconFontName;

            BackColor = skin.Background;
            ForeColor = skin.Foreground;
            Font = skin.Font;

            title.Font = new Font(skin.Font.FontFamily, skin.Font.Size + 1.5f, FontStyle.Bold);
            artist.ForeColor = skin.Foreground;
            album.ForeColor = skin.Dim;
            elapsed.ForeColor = duration.ForeColor = skin.Dim;
            elapsed.Font = duration.Font = new Font(skin.Font.FontFamily, Math.Max(7f, skin.Font.Size - 1f));
            status.ForeColor = skin.Dim;
            status.Font = elapsed.Font;
            seekBar.TrackColor = skin.Track;
            seekBar.FillColor = skin.Accent;
            artwork.BackColor = skin.Track;

            devices.BackColor = skin.InputBackground;
            devices.ForeColor = skin.InputForeground;

            shuffle = IconButton("", "S", "Shuffle", () => controller.ToggleShuffleAsync());
            previous = IconButton("", "|◀", "Previous", () => controller.PreviousAsync());
            playPause = IconButton("", "▶", "Play / pause", () => controller.PlayPauseAsync(), large: true);
            next = IconButton("", "▶|", "Next", () => controller.NextAsync());
            repeat = IconButton("", "R", "Repeat", () => controller.CycleRepeatAsync());

            tips.SetToolTip(title, "Open in Spotify");
            tips.SetToolTip(devices, "Spotify device");

            title.Click += (s, e) => OpenInSpotify(snapshot?.State?.Item?.Uri);
            seekBar.SeekRequested += fraction =>
            {
                var total = snapshot?.Duration ?? TimeSpan.Zero;
                if (total > TimeSpan.Zero)
                    _ = controller.SeekAsync(TimeSpan.FromMilliseconds(total.TotalMilliseconds * fraction));
            };
            devices.DropDown += async (s, e) => await ReloadDevicesAsync();
            devices.SelectionChangeCommitted += (s, e) =>
            {
                if (!updatingDevices && devices.SelectedItem is Device device && !device.IsActive)
                    _ = controller.TransferToAsync(device.Id);
            };

            disconnected.BackColor = skin.Background;
            disconnectedText.ForeColor = skin.Dim;
            connectButton.FlatStyle = FlatStyle.Flat;
            connectButton.ForeColor = skin.Foreground;
            connectButton.FlatAppearance.BorderColor = skin.Accent;
            connectButton.Click += (s, e) => openSettings();
            disconnected.Controls.AddRange(new Control[] { disconnectedText, connectButton });

            Controls.AddRange(new Control[]
            {
                disconnected, artwork, title, artist, album, seekBar, elapsed, duration,
                shuffle, previous, playPause, next, repeat, devices, status,
            });

            ticker.Tick += (s, e) => UpdateProgress();
            statusClear.Tick += (s, e) => { statusClear.Stop(); status.Text = ""; };

            controller.ConnectionChanged += OnConnectionChanged;
            controller.PlaybackUpdated += OnPlaybackUpdated;
            controller.StatusMessage += OnStatusMessage;

            ShowConnectionState();
            if (controller.LastSnapshot != null)
                ApplySnapshot(controller.LastSnapshot);
            else
                ShowNothingPlaying();
            ticker.Start();
            initialised = true;
            PerformLayout();
        }

        /// <summary>The fixed height MusicBee should reserve for this panel.</summary>
        public int PreferredHeight => S(212);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!initialised)
                return;
            var pad = S(8);
            var width = ClientSize.Width;

            disconnected.Bounds = ClientRectangle;
            disconnectedText.Bounds = new Rectangle(pad, 0, Math.Max(0, width - pad * 2), ClientSize.Height / 2);
            connectButton.Location = new Point(Math.Max(pad, (width - connectButton.Width) / 2), ClientSize.Height / 2);

            var artSize = S(72);
            artwork.Bounds = new Rectangle(pad, pad, artSize, artSize);
            var textLeft = artwork.Right + pad;
            var textWidth = Math.Max(0, width - textLeft - pad);
            var line = S(20);
            title.Bounds = new Rectangle(textLeft, pad + S(4), textWidth, line + S(2));
            artist.Bounds = new Rectangle(textLeft, title.Bottom + S(2), textWidth, line);
            album.Bounds = new Rectangle(textLeft, artist.Bottom, textWidth, line);

            seekBar.Bounds = new Rectangle(pad, artwork.Bottom + pad, Math.Max(0, width - pad * 2), S(14));
            var timeWidth = S(60);
            elapsed.Bounds = new Rectangle(pad, seekBar.Bottom, timeWidth, S(16));
            duration.Bounds = new Rectangle(width - pad - timeWidth, seekBar.Bottom, timeWidth, S(16));

            var small = S(32);
            var large = S(40);
            var gap = S(6);
            var rowWidth = small * 4 + large + gap * 4;
            var x = (width - rowWidth) / 2;
            var rowTop = elapsed.Bottom + S(2);
            foreach (var button in new[] { shuffle, previous, playPause, next, repeat })
            {
                var size = button == playPause ? large : small;
                button.Bounds = new Rectangle(x, rowTop + (large - size) / 2, size, size);
                x += size + gap;
            }

            devices.Bounds = new Rectangle(pad, rowTop + large + S(6), Math.Max(0, width - pad * 2), devices.Height);
            status.Bounds = new Rectangle(pad, devices.Bottom + S(4), Math.Max(0, width - pad * 2), S(16));
        }

        // --- Controller events (may arrive on background threads) ---------

        private void OnConnectionChanged() => Ui(ShowConnectionState);
        private void OnPlaybackUpdated(PlaybackSnapshot s) => Ui(() => ApplySnapshot(s));
        private void OnStatusMessage(string message) => Ui(() =>
        {
            status.Text = message;
            tips.SetToolTip(status, message);
            statusClear.Stop();
            statusClear.Start();
        });

        private void Ui(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { /* handle destroyed between checks */ }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!initialised)
                return;
            // Catch up on anything that happened before the handle existed
            ShowConnectionState();
            if (controller.LastSnapshot != null)
                ApplySnapshot(controller.LastSnapshot);
        }

        // --- Rendering -----------------------------------------------------

        private void ShowConnectionState()
        {
            var connected = controller.IsConnected;
            disconnected.Visible = !connected;
            if (!connected)
            {
                disconnected.BringToFront();
                disconnectedText.Text = string.IsNullOrEmpty(controller.Settings.ClientId)
                    ? "SpotiBee isn't set up yet.\nAdd your Spotify Client ID to get started."
                    : "Not connected to Spotify.";
                snapshot = null;
                ShowNothingPlaying();
            }
        }

        private void ApplySnapshot(PlaybackSnapshot s)
        {
            snapshot = s;
            var state = s?.State;
            var item = state?.Item;

            if (item == null)
            {
                ShowNothingPlaying();
                UpdateDeviceDisplay(state?.Device);
                return;
            }

            title.Text = item.Name;
            artist.Text = item.ArtistNames;
            album.Text = item.AlbumName;
            SetIcon(playPause, state.IsPlaying ? "" : "", state.IsPlaying ? "❚❚" : "▶");
            shuffle.ForeColor = state.ShuffleState ? skin.Accent : skin.Dim;
            repeat.ForeColor = state.RepeatState == "off" || state.RepeatState == null ? skin.Dim : skin.Accent;
            SetIcon(repeat, state.RepeatState == "track" ? "" : "", state.RepeatState == "track" ? "R1" : "R");
            seekBar.Enabled = true;
            UpdateDeviceDisplay(state.Device);
            UpdateProgress();
            _ = LoadArtworkAsync(item.Artwork);
        }

        private void ShowNothingPlaying()
        {
            title.Text = controller.IsConnected ? "Nothing playing" : "";
            artist.Text = controller.IsConnected ? "Start something in Spotify or press play" : "";
            album.Text = "";
            elapsed.Text = duration.Text = "";
            seekBar.Value = 0;
            seekBar.Enabled = false;
            SetIcon(playPause, "", "▶");
            shuffle.ForeColor = repeat.ForeColor = skin.Dim;
            SetArtwork(null, null);
        }

        private void UpdateProgress()
        {
            var s = snapshot;
            if (s?.State?.Item == null || seekBar.IsDragging)
                return;
            var position = s.Position;
            var total = s.Duration;
            seekBar.Value = total > TimeSpan.Zero ? position.TotalMilliseconds / total.TotalMilliseconds : 0;
            elapsed.Text = FormatTime(position);
            duration.Text = FormatTime(total);
        }

        private void UpdateDeviceDisplay(Device active)
        {
            if (devices.DroppedDown)
                return;
            updatingDevices = true;
            try
            {
                devices.Items.Clear();
                if (active != null)
                {
                    devices.Items.Add(active);
                    devices.SelectedIndex = 0;
                }
            }
            finally
            {
                updatingDevices = false;
            }
        }

        private async Task ReloadDevicesAsync()
        {
            var list = await controller.GetDevicesAsync();
            if (IsDisposed)
                return;
            updatingDevices = true;
            try
            {
                devices.BeginUpdate();
                devices.Items.Clear();
                foreach (var device in list.Where(d => d.Id != null))
                    devices.Items.Add(device);
                var active = list.FirstOrDefault(d => d.IsActive);
                if (active != null)
                    devices.SelectedItem = active;
                if (list.Length == 0)
                    OnStatusMessage("No Spotify devices found. Open Spotify on this PC or another device.");
            }
            finally
            {
                devices.EndUpdate();
                updatingDevices = false;
            }
        }

        private async Task LoadArtworkAsync(Spotify.Image[] images)
        {
            // Prefer ~300px: sharp at high DPI without downloading the 640px original
            var best = images?
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .OrderBy(i => Math.Abs((i.Width ?? 300) - 300))
                .FirstOrDefault();
            var url = best?.Url;
            if (url == artworkUrl)
                return;
            artworkUrl = url;
            if (url == null)
            {
                SetArtwork(null, null);
                return;
            }

            try
            {
                var bytes = await ArtworkHttp.GetByteArrayAsync(url);
                if (IsDisposed || url != artworkUrl)
                    return;
                using var stream = new MemoryStream(bytes);
                SetArtwork(url, new Bitmap(stream));
            }
            catch
            {
                if (url == artworkUrl)
                    SetArtwork(url, null);
            }
        }

        private void SetArtwork(string url, System.Drawing.Image image)
        {
            if (url == null)
                artworkUrl = null;
            var old = artwork.Image;
            artwork.Image = image;
            old?.Dispose();
        }

        // --- Helpers -------------------------------------------------------

        private Button IconButton(string glyph, string fallback, string tooltip, Func<Task> action, bool large = false)
        {
            var button = new Button
            {
                FlatStyle = FlatStyle.Flat,
                ForeColor = skin.Foreground,
                BackColor = skin.Background,
                TabStop = false,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = skin.Track;
            button.FlatAppearance.MouseDownBackColor = skin.Track;
            button.Font = hasIconFont
                ? new Font(IconFontName, large ? 16f : 11f)
                : new Font(skin.Font.FontFamily, large ? 12f : 9f, FontStyle.Bold);
            SetIcon(button, glyph, fallback);
            tips.SetToolTip(button, tooltip);
            button.Click += async (s, e) =>
            {
                button.Enabled = false;
                try { await action(); }
                finally { button.Enabled = true; }
            };
            return button;
        }

        private void SetIcon(Button button, string glyph, string fallback) =>
            button.Text = hasIconFont ? glyph : fallback;

        private int S(int value) => (int)Math.Round(value * scale);

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private static void OpenInSpotify(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { /* Spotify app not installed */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                controller.ConnectionChanged -= OnConnectionChanged;
                controller.PlaybackUpdated -= OnPlaybackUpdated;
                controller.StatusMessage -= OnStatusMessage;
                ticker.Dispose();
                statusClear.Dispose();
                tips.Dispose();
                artwork.Image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
