using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpotiBee.UI
{
    /// <summary>Shows the forum-ready diagnostics summary so the user can see exactly what they'd share, and copy it.</summary>
    public sealed class DiagnosticsSummaryForm : Form
    {
        private readonly TextBox text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button copy = new Button { Text = "Copy", AutoSize = true };
        private readonly Button close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

        public DiagnosticsSummaryForm(string summary)
        {
            Text = "SpotiBee Diagnostics Summary";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(760, 480);
            MinimumSize = new Size(480, 300);
            Padding = new Padding(10);
            CancelButton = close;
            AcceptButton = copy;

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Text = "Paste this into your post on the MusicBee forum when asking for help. It has nothing private in " +
                       "it: no login details, account name or Client ID, and your computer, device names and Windows " +
                       "user folder are hidden. It's already wrapped in [code] tags for the forum.",
            };
            text.Text = summary.Replace("\n", Environment.NewLine).Replace("\r" + Environment.NewLine, Environment.NewLine);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
            buttons.Controls.Add(copy);
            buttons.Controls.Add(close);
            bottom.Controls.Add(status);
            bottom.Controls.Add(buttons);

            Controls.Add(text);
            Controls.Add(intro);
            Controls.Add(bottom);

            copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetDataObject(text.Text, copy: true, retryTimes: 5, retryDelay: 100);
                    status.Text = "Copied. Paste it into your forum post.";
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
                {
                    status.Text = "Couldn't copy: another program may be using the clipboard. Try again.";
                }
            };
            Shown += (s, e) => copy.Focus();
        }
    }
}
