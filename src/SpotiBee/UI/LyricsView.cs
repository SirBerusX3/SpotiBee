using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SpotiBee.Library;

namespace SpotiBee.UI
{
    /// <summary>
    /// Shows lyrics: synced ones highlight the current line and keep it centred; plain ones scroll.
    /// Scrolling with the mouse wheel pauses auto-scrolling for a few seconds.
    /// </summary>
    public sealed class LyricsView : Control
    {
        private static readonly TimeSpan ManualScrollHold = TimeSpan.FromSeconds(5);

        private Lyrics lyrics;
        private string message;
        private List<string> lines = new List<string>();
        private int current = -1;
        private float scroll;            // pixels from the top of the text
        private float targetScroll;
        private DateTime manualUntil;
        private readonly Timer animation = new Timer { Interval = 16 };

        public LyricsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            animation.Tick += (s, e) => Animate();
        }

        public Color DimColor { get; set; } = Color.Gray;
        public Color HighlightColor { get; set; } = Color.Orange;

        /// <summary>Shows a status line ("Looking up lyrics…", "No lyrics found") instead of lyrics.</summary>
        public void ShowMessage(string text)
        {
            lyrics = null;
            message = text;
            lines = new List<string>();
            current = -1;
            scroll = targetScroll = 0;
            Invalidate();
        }

        public void ShowLyrics(Lyrics value)
        {
            lyrics = value;
            message = value == null ? "No lyrics found" : value.Instrumental ? "Instrumental" : null;
            lines = value == null || value.Instrumental
                ? new List<string>()
                : value.IsSynced ? value.Lines.Select(l => l.Text).ToList() : value.Plain.Split('\n').ToList();
            current = -1;
            scroll = targetScroll = 0;
            manualUntil = DateTime.MinValue;
            Invalidate();
        }

        public bool HasLyrics => lyrics != null && lines.Count > 0;

        /// <summary>Call regularly with Spotify's playback position to move the highlight.</summary>
        public void UpdatePosition(TimeSpan position)
        {
            if (lyrics == null || !lyrics.IsSynced)
                return;
            var line = lyrics.LineAt(position);
            if (line == current)
                return;
            current = line;
            if (DateTime.UtcNow >= manualUntil)
                CentreOn(current);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (lines.Count == 0)
                return;
            manualUntil = DateTime.UtcNow + ManualScrollHold;
            targetScroll = Clamp(targetScroll - e.Delta / 120f * LineHeight * 3);
            animation.Start();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();   // so the wheel reaches us
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (lines.Count == 0)
            {
                if (!string.IsNullOrEmpty(message))
                    TextRenderer.DrawText(g, message, Font, ClientRectangle, DimColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            var lineHeight = LineHeight;
            var synced = lyrics?.IsSynced == true;
            using var bold = new Font(Font, FontStyle.Bold);
            var first = Math.Max(0, (int)(scroll / lineHeight) - 1);
            for (var i = first; i < lines.Count; i++)
            {
                var y = i * lineHeight - scroll;
                if (y > Height)
                    break;
                var isCurrent = synced && i == current;
                var colour = !synced ? ForeColor : isCurrent ? HighlightColor : (i < current ? DimColor : ForeColor);
                var rect = new Rectangle(4, (int)y, Math.Max(0, Width - 8), (int)lineHeight);
                TextRenderer.DrawText(g, lines[i], isCurrent ? bold : Font, rect, colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (current >= 0 && DateTime.UtcNow >= manualUntil)
            {
                CentreOn(current);
                scroll = targetScroll;
            }
        }

        private float LineHeight => Font.GetHeight() * 1.45f;

        private void CentreOn(int line)
        {
            targetScroll = Clamp(line * LineHeight - (Height - LineHeight) / 2f);
            animation.Start();
        }

        private float Clamp(float value) =>
            Math.Max(0, Math.Min(value, Math.Max(0, lines.Count * LineHeight - Height + LineHeight)));

        private void Animate()
        {
            var delta = targetScroll - scroll;
            if (Math.Abs(delta) < 0.5f)
            {
                scroll = targetScroll;
                animation.Stop();
            }
            else
            {
                scroll += delta * 0.2f;   // ease towards the target
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
