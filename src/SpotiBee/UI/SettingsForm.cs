using System;
using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using SpotiBee.Spotify;

namespace SpotiBee.UI
{
    /// <summary>
    /// Spotify setup and login, plus SpotiBee's options. Shown from MusicBee's plugin "Configure" button and
    /// the panel. Until connected, it walks through creating a Spotify app step by step.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private const string DashboardUrl = "https://developer.spotify.com/dashboard";

        private readonly SpotiBeeController controller;
        private readonly TextBox clientId = new TextBox();
        private readonly TextBox redirectUri = new TextBox { ReadOnly = true, Text = SpotifyAuth.RedirectUri };
        private readonly Button copyRedirect = new Button { Text = "Copy", AutoSize = true };
        private readonly Button pasteClientId = new Button { Text = "Paste", AutoSize = true };
        private readonly Button connect = new Button { AutoSize = true };
        private readonly Button openDashboard = new Button { Text = "Open the Spotify Developer Dashboard", AutoSize = true };
        private readonly Button diagnostics = new Button { Text = "Diagnostics summary…", AutoSize = true };
        private readonly Label clientIdHint = new Label { AutoSize = true, MaximumSize = new Size(300, 0) };
        private readonly Label accountStatus = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly TableLayoutPanel guide = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        private readonly Action<IWin32Window> showDiagnostics;
        private bool justConnected;
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        private readonly TextBox placeholderFolder = new TextBox();
        private readonly Button browse = new Button { Text = "Browse…", AutoSize = true };
        private readonly CheckBox autoSync = new CheckBox { Text = "Keep linked playlists in sync with Spotify automatically", AutoSize = true };
        private readonly CheckBox autoImport = new CheckBox { Text = "Import new Spotify playlists and saved albums automatically", AutoSize = true };
        private readonly CheckBox updateCheck = new CheckBox { Text = "Tell me when a new version of SpotiBee is out", AutoSize = true };
        private readonly ToolTip tips = new ToolTip();
        private CancellationTokenSource loginCancel;

        public SettingsForm(SpotiBeeController controller, Action<IWin32Window> showDiagnostics = null)
        {
            this.controller = controller;
            this.showDiagnostics = showDiagnostics;

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

            // Step-by-step setup, shown until connected
            guide.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            guide.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Step("1.", "Create an app in the Spotify Developer Dashboard, logged in with the Spotify account you'll " +
                       "listen with (it needs Premium). Any app name and description will do.", openDashboard);
            Step("2.", "In the app's settings, add the Redirect URI below under Redirect URIs, tick Web API, and click Save.");
            Step("3.", "Copy the Client ID from your app's page and paste it below.");
            Step("4.", "Click Connect, then approve SpotiBee in the browser window that opens.");
            openDashboard.Click += (s, e) => Open(DashboardUrl);
            layout.Controls.Add(guide, 0, 0);
            layout.SetColumnSpan(guide, 3);

            layout.Controls.Add(FieldLabel("Redirect URI:"), 0, 1);
            redirectUri.Dock = DockStyle.Fill;
            layout.Controls.Add(redirectUri, 1, 1);
            copyRedirect.Click += (s, e) => CopyRedirectUri();
            tips.SetToolTip(copyRedirect, "Copy it, then paste it into your Spotify app's Redirect URIs");
            layout.Controls.Add(copyRedirect, 2, 1);

            layout.Controls.Add(FieldLabel("Client ID:"), 0, 2);
            clientId.Dock = DockStyle.Fill;
            clientId.Text = controller.Settings.ClientId ?? "";
            clientId.TextChanged += (s, e) => UpdateClientIdHint();
            layout.Controls.Add(clientId, 1, 2);
            pasteClientId.Click += (s, e) => PasteClientId();
            layout.Controls.Add(pasteClientId, 2, 2);

            clientIdHint.Margin = new Padding(3, 0, 3, 3);
            layout.Controls.Add(clientIdHint, 1, 3);
            connect.Click += OnConnectClicked;
            layout.Controls.Add(connect, 2, 3);

            accountStatus.Margin = new Padding(0, 10, 0, 10);
            layout.Controls.Add(accountStatus, 0, 4);
            layout.SetColumnSpan(accountStatus, 3);

            layout.Controls.Add(FieldLabel("Placeholders:"), 0, 5);
            placeholderFolder.Dock = DockStyle.Fill;
            placeholderFolder.Text = controller.Settings.EffectivePlaceholderFolder;
            tips.SetToolTip(placeholderFolder, "Where silent stand-in files for Spotify-only tracks are created. Changing this doesn't move existing ones.");
            layout.Controls.Add(placeholderFolder, 1, 5);
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
            layout.Controls.Add(browse, 2, 5);

            autoSync.Checked = !controller.Settings.DisableAutoSync;
            autoSync.Margin = new Padding(0, 8, 0, 0);
            tips.SetToolTip(autoSync,
                "Changes to linked playlists in MusicBee are sent to Spotify a few seconds later,\n" +
                "and changes made in Spotify are picked up every few minutes.\n" +
                "Tools > SpotiBee: Sync Playlists Now works either way.");
            layout.Controls.Add(autoSync, 0, 6);
            layout.SetColumnSpan(autoSync, 3);

            autoImport.Checked = controller.Settings.AutoImportNewPlaylists;
            tips.SetToolTip(autoImport,
                "Playlists you create on Spotify (or start collaborating on), and albums you save there,\n" +
                "are imported at the next sync, instead of a notice on the panel asking first.\n" +
                "SpotiBee only looks for new playlists once you've linked one, and for new albums once you've imported one.");
            layout.Controls.Add(autoImport, 0, 7);
            layout.SetColumnSpan(autoImport, 3);

            updateCheck.Checked = !controller.Settings.DisableUpdateCheck;
            tips.SetToolTip(updateCheck,
                $"You have SpotiBee {typeof(SettingsForm).Assembly.GetName().Version.ToString(3)}.\n" +
                "Checks GitHub once a day and shows a notice on the panel. Only the version number is sent.");
            layout.Controls.Add(updateCheck, 0, 8);
            layout.SetColumnSpan(updateCheck, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(close);
            if (showDiagnostics != null)
            {
                diagnostics.Click += (s, e) => showDiagnostics(this);
                tips.SetToolTip(diagnostics, "A short report to paste into a MusicBee forum post when asking for help");
                buttons.Controls.Add(diagnostics);
            }
            layout.Controls.Add(buttons, 0, 9);
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
            var connected = controller.IsConnected;
            clientId.Enabled = pasteClientId.Enabled = !loggingIn && !connected;
            guide.Visible = !connected;
            UpdateClientIdHint();

            if (loggingIn)
            {
                connect.Text = "Cancel";
                accountStatus.Text = "Waiting for you to approve SpotiBee in your browser…\n\n" +
                                     "If Spotify's page shows an error instead:\n" +
                                     "• \"INVALID_CLIENT: Invalid redirect URI\": the Redirect URI isn't saved in your app yet (step 2).\n" +
                                     "• \"INVALID_CLIENT: Invalid client\": the Client ID doesn't match your app (step 3).\n" +
                                     "Fix it, click Cancel, then Connect again.";
            }
            else if (connected)
            {
                connect.Text = "Disconnect";
                var user = controller.User;
                accountStatus.Text = (user == null
                    ? "Connected to Spotify."
                    : $"Connected as {user.DisplayName ?? user.Id}" + (user.IsPremium == false
                        ? ". This account isn't Premium, so Spotify won't allow playback control."
                        : ".")) +
                    (justConnected
                        ? "\n\nNext: import your playlists or saved albums from Tools → SpotiBee, or the SpotiBee " +
                          "panel's header menu. To add the panel, use View → Arrange Panels in MusicBee."
                        : "");
            }
            else
            {
                connect.Text = "Connect";
                accountStatus.Text = "Not connected.";
            }
        }

        private void Step(string number, string text, Control extra = null)
        {
            var row = guide.RowCount++;
            guide.Controls.Add(new Label { Text = number, AutoSize = true, Margin = new Padding(0, 3, 4, 3) }, 0, row);
            var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            body.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 3, 0, 3) });
            if (extra != null)
            {
                extra.Margin = new Padding(0, 0, 0, 6);
                body.Controls.Add(extra);
            }
            guide.Controls.Add(body, 1, row);
        }

        /// <summary>Spotify Client IDs are 32 hexadecimal characters.</summary>
        internal static bool LooksLikeClientId(string text) =>
            Regex.IsMatch(text?.Trim() ?? "", "^[0-9a-fA-F]{32}$");

        private void UpdateClientIdHint()
        {
            var text = clientId.Text.Trim();
            clientIdHint.Visible = !controller.IsConnected && loginCancel == null && text.Length > 0;
            if (LooksLikeClientId(text))
            {
                clientIdHint.Text = "✓ That looks like a Client ID.";
                clientIdHint.ForeColor = SystemColors.GrayText;
            }
            else
            {
                clientIdHint.Text = "A Client ID is 32 letters and numbers (0–9 and a–f). Copy it from your app's page.";
                clientIdHint.ForeColor = Color.Firebrick;
            }
        }

        private void PasteClientId()
        {
            try
            {
                var text = Clipboard.GetText()?.Trim();
                if (!string.IsNullOrEmpty(text))
                    clientId.Text = text;
            }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        }

        private void CopyRedirectUri()
        {
            try
            {
                Clipboard.SetDataObject(SpotifyAuth.RedirectUri, copy: true, retryTimes: 5, retryDelay: 100);
                tips.Show("Copied", copyRedirect, 0, copyRedirect.Height, 1500);
            }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        }

        /// <summary>Turns the token endpoint's terse errors into what to fix.</summary>
        internal static string ExplainLoginError(string message)
        {
            var m = message ?? "";
            if (m.IndexOf("redirect", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"Spotify says the Redirect URI doesn't match. In your app's settings it must be exactly {SpotifyAuth.RedirectUri}. " +
                       "Save it, then Connect again.";
            if (m.IndexOf("invalid_client", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("invalid client", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Spotify didn't accept that Client ID. Copy it again from your app's page in the Developer Dashboard.";
            return m;
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
                justConnected = false;
                UpdateState();
                return;
            }
            if (!LooksLikeClientId(clientId.Text))
            {
                accountStatus.Text = clientId.Text.Trim().Length == 0
                    ? "Paste your Spotify app's Client ID first (step 3)."
                    : "That doesn't look like a Client ID: it should be 32 letters and numbers. Copy it again from your app's page.";
                clientId.Focus();
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
                error = ExplainLoginError(ex.Message);
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
            justConnected = error == null;
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
            var disableUpdateCheck = !updateCheck.Checked;
            if (chosen != controller.Settings.PlaceholderFolder || disableAutoSync != controller.Settings.DisableAutoSync ||
                autoImport.Checked != controller.Settings.AutoImportNewPlaylists || disableUpdateCheck != controller.Settings.DisableUpdateCheck)
            {
                controller.Settings.PlaceholderFolder = chosen;
                controller.Settings.DisableAutoSync = disableAutoSync;
                controller.Settings.AutoImportNewPlaylists = autoImport.Checked;
                controller.Settings.DisableUpdateCheck = disableUpdateCheck;
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
