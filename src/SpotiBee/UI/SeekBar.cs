using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpotiBee.UI
{
    /// <summary>A thin progress bar that can be clicked or dragged to seek.</summary>
    public sealed class SeekBar : Control
    {
        private double value;
        private double? dragValue;

        public SeekBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Height = 14;
        }

        public Color TrackColor { get; set; } = Color.Gray;
        public Color FillColor { get; set; } = Color.Orange;

        /// <summary>0..1</summary>
        public double Value
        {
            get => value;
            set
            {
                var clamped = Math.Max(0, Math.Min(1, value));
                if (Math.Abs(clamped - this.value) < 0.0005)
                    return;
                this.value = clamped;
                if (dragValue == null)
                    Invalidate();
            }
        }

        public bool IsDragging => dragValue != null;

        /// <summary>Raised with the chosen 0..1 fraction when the user releases the mouse.</summary>
        public event Action<double> SeekRequested;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled)
                return;
            Capture = true;
            dragValue = Fraction(e.X);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragValue == null)
                return;
            dragValue = Fraction(e.X);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragValue == null)
                return;
            var chosen = Fraction(e.X);
            dragValue = null;
            Capture = false;
            value = chosen;
            Invalidate();
            SeekRequested?.Invoke(chosen);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var barHeight = Math.Max(3, Height / 4);
            var top = (Height - barHeight) / 2f;
            var shown = dragValue ?? value;

            using (var track = new SolidBrush(TrackColor))
                FillRounded(g, track, new RectangleF(0, top, Width - 1, barHeight));

            var fillWidth = (float)((Width - 1) * shown);
            if (fillWidth > 0.5f)
                using (var fill = new SolidBrush(FillColor))
                    FillRounded(g, fill, new RectangleF(0, top, fillWidth, barHeight));

            if (Enabled && (dragValue != null || ClientRectangle.Contains(PointToClient(MousePosition))))
            {
                var knob = barHeight * 2.4f;
                using var brush = new SolidBrush(FillColor);
                g.FillEllipse(brush, Math.Max(0, Math.Min(Width - knob, fillWidth - knob / 2)), (Height - knob) / 2f, knob, knob);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

        private double Fraction(int x) => Width <= 1 ? 0 : Math.Max(0, Math.Min(1, x / (double)(Width - 1)));

        private static void FillRounded(Graphics g, Brush brush, RectangleF rect)
        {
            var r = rect.Height;
            if (rect.Width < r)
            {
                g.FillRectangle(brush, rect);
                return;
            }
            using var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, r, r, 90, 180);
            path.AddArc(rect.Right - r, rect.Top, r, r, 270, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}
