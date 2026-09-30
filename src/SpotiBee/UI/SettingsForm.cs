using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>Client ID entry and Spotify login. Shown from MusicBee's plugin "Configure" button and the panel.</summary>
    public sealed class SettingsForm : Form
    {
        private const string DashboardUrl = "https://developer.spotify.com/dashboard";

        private readonly SpotiBeeController controller;
        private readonly TextBox clientId = new TextBox();
        private readonly TextBox redirectUri = new TextBox { ReadOnly = true, Text = SpotifyAuth.RedirectUri };
        private readonly Button copyRedirect = new Button { Text = "Copy", AutoSize = true };
        private readonly Button connect = new Button { AutoSize = true };
        private readonly Label accountStatus = new Label { AutoSize = true, MaximumSize = new Size(440, 0) };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly TextBox placeholderFolder = new TextBox();
        private readonly Button browse = new Button { Text = "Browse…", AutoSize = true };
        private readonly CheckBox autoSync = new CheckBox { Text = "Keep linked playlists in sync with Spotify automatically", AutoSize = true };
        private readonly ToolTip tips = new ToolTip();
        private CancellationTokenSource loginCancel;

        public SettingsForm(SpotiBeeController controller)
        {
            this.controller = controller;

            Text = "SpotiBee Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            Font = SystemFonts.MessageBoxFont;
            CancelButton = close;

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 3,
                Dock = DockStyle.Fill,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new LinkLabel
            {
                AutoSize = true,
                MaximumSize = new Size(460, 0),
                Text = "1. Create an app in the Spotify Developer Dashboard (choose \"Web API\").\n" +
                       "2. Add the Redirect URI below to the app's settings.\n" +
                       "3. Paste the app's Client ID here and click Connect.",
                Margin = new Padding(0, 0, 0, 10),
            };
            var linkStart = intro.Text.IndexOf("Spotify Developer Dashboard", StringComparison.Ordinal);
            intro.Links.Add(linkStart, "Spotify Developer Dashboard".Length, DashboardUrl);
            intro.LinkClicked += (s, e) => Open((string)e.Link.LinkData);
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 3);

            layout.Controls.Add(FieldLabel("Redirect URI:"), 0, 1);
            redirectUri.Dock = DockStyle.Fill;
            layout.Controls.Add(redirectUri, 1, 1);
            copyRedirect.Click += (s, e) => Clipboard.SetText(SpotifyAuth.RedirectUri);
            layout.Controls.Add(copyRedirect, 2, 1);

            layout.Controls.Add(FieldLabel("Client ID:"), 0, 2);
            clientId.Dock = DockStyle.Fill;
            clientId.Text = controller.Settings.ClientId ?? "";
            layout.Controls.Add(clientId, 1, 2);
            connect.Click += OnConnectClicked;
            layout.Controls.Add(connect, 2, 2);

            accountStatus.Margin = new Padding(0, 10, 0, 10);
            layout.Controls.Add(accountStatus, 0, 3);
            layout.SetColumnSpan(accountStatus, 3);

            layout.Controls.Add(FieldLabel("Placeholders:"), 0, 4);
            placeholderFolder.Dock = DockStyle.Fill;
            placeholderFolder.Text = controller.Settings.EffectivePlaceholderFolder;
            tips.SetToolTip(placeholderFolder, "Where silent stand-in files for Spotify-only tracks are created. Changing this doesn't move existing ones.");
            layout.Controls.Add(placeholderFolder, 1, 4);
            browse.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Folder for SpotiBee placeholder tracks",
                    SelectedPath = placeholderFolder.Text,
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    placeholderFolder.Text = dialog.SelectedPath;
            };
            layout.Controls.Add(browse, 2, 4);

            autoSync.Checked = !controller.Settings.DisableAutoSync;
            autoSync.Margin = new Padding(0, 8, 0, 0);
            tips.SetToolTip(autoSync,
                "Changes to linked playlists in MusicBee are sent to Spotify a few seconds later,\n" +
                "and changes made in Spotify are picked up every few minutes.\n" +
                "Tools > SpotiBee: Sync Playlists Now works either way.");
            layout.Controls.Add(autoSync, 0, 5);
            layout.SetColumnSpan(autoSync, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(close);
            layout.Controls.Add(buttons, 0, 6);
            layout.SetColumnSpan(buttons, 3);

            Controls.Add(layout);

            controller.ConnectionChanged += OnConnectionChanged;
            UpdateState();
        }

        private void OnConnectionChanged()
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)UpdateState);
        }

        private void UpdateState()
        {
            var loggingIn = loginCancel != null;
            clientId.Enabled = !loggingIn && !controller.IsConnected;

            if (loggingIn)
            {
                connect.Text = "Cancel";
                accountStatus.Text = "Waiting for you to approve SpotiBee in your browser…";
            }
            else if (controller.IsConnected)
            {
                connect.Text = "Disconnect";
                var user = controller.User;
                accountStatus.Text = user == null
                    ? "Connected to Spotify."
                    : $"Connected as {user.DisplayName ?? user.Id}" + (user.IsPremium == false
                        ? ". This account isn't Premium, so Spotify won't allow playback control."
                        : ".");
            }
            else
            {
                connect.Text = "Connect";
                accountStatus.Text = "Not connected.";
            }
        }

        private async void OnConnectClicked(object sender, EventArgs e)
        {
            if (loginCancel != null)
            {
                loginCancel.Cancel();
                return;
            }
            if (controller.IsConnected)
            {
                controller.Disconnect();
                UpdateState();
                return;
            }

            // Spotify's login page is abandoned more often than cancelled; give up after a while
            loginCancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            UpdateState();
            string error = null;
            try
            {
                await controller.ConnectAsync(clientId.Text, loginCancel.Token);
            }
            catch (OperationCanceledException)
            {
                error = "Login cancelled.";
            }
            catch (SpotifyAuthException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = "Login failed: " + ex.Message;
            }
            finally
            {
                loginCancel.Dispose();
                loginCancel = null;
            }

            if (IsDisposed)
                return;
            UpdateState();
            if (error != null)
                accountStatus.Text = error;
            else
                Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            loginCancel?.Cancel();
            var folder = placeholderFolder.Text.Trim();
            var chosen = string.Equals(folder, PluginSettings.DefaultPlaceholderFolder, StringComparison.OrdinalIgnoreCase) || folder.Length == 0
                ? null
                : folder;
            var disableAutoSync = !autoSync.Checked;
            if (chosen != controller.Settings.PlaceholderFolder || disableAutoSync != controller.Settings.DisableAutoSync)
            {
                controller.Settings.PlaceholderFolder = chosen;
                controller.Settings.DisableAutoSync = disableAutoSync;
                try { controller.Settings.Save(); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't save settings: " + ex.Message, "SpotiBee"); }
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                controller.ConnectionChanged -= OnConnectionChanged;
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Label FieldLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 6),
        };

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no default browser */ }
        }
    }
}
