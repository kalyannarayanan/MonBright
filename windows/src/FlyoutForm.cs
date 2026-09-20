//
//  FlyoutForm.cs
//  MonBright for Windows — the panel that drops out of the tray.
//
//  Everything is drawn by hand rather than assembled from WinForms controls:
//  a stock TrackBar looks like Windows 7, and owner-drawing the whole surface
//  is what lets the panel scale crisply per-monitor and follow the system
//  light/dark setting and accent colour.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace MonBright
{
    internal sealed class FlyoutForm : Form
    {
        private sealed class Row
        {
            public MonitorModel Model;
            public Rectangle Header;    // name + percentage
            public Rectangle Slider;    // whole grab area for the slider line
            public Rectangle Track;     // the drawn groove
            public Rectangle Status;    // explanation line, present only when unusable
            public bool ShowStatus;
        }

        private readonly List<Row> _rows = new List<Row>();
        private List<MonitorModel> _monitors = new List<MonitorModel>();

        private float _scale = 1f;
        private Font _fontName, _fontValue, _fontFoot;
        private int _dragIndex = -1;
        private int _hoverIndex = -1;
        private int _focusIndex = 0;

        public DateTime LastHidden = DateTime.MinValue;

        /// <summary>Bottom hint line. Set by TrayApp before showing, so the
        /// panel can report a hotkey conflict it has no way to detect itself.</summary>
        public string FooterHint;

        private Native.POINT _anchor;
        private string _laidOutAvailability = "";

        /// <summary>Raised when the user finishes a drag, so the value can be persisted.</summary>
        public event EventHandler<MonitorEventArgs> ValueCommitted;

        public FlyoutForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = "MonBright";

            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW — keep out of Alt+Tab
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        // -------------------------------------------------------------------
        // Showing
        // -------------------------------------------------------------------

        public void ShowFor(List<MonitorModel> monitors)
        {
            Detach();
            _monitors = monitors ?? new List<MonitorModel>();
            foreach (MonitorModel m in _monitors) m.Changed += OnModelChanged;

            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) { cursor.x = 0; cursor.y = 0; }

            _anchor = cursor;
            _scale = Native.DpiAt(cursor) / 96f;
            BuildLayout();
            PositionNear(cursor);
            ApplyWindowStyling();

            if (_focusIndex >= _rows.Count) _focusIndex = 0;
            Show();
            Native.SetForegroundWindow(Handle);
            Activate();
            Invalidate();
        }

        private void Detach()
        {
            foreach (MonitorModel m in _monitors) m.Changed -= OnModelChanged;
        }

        private void OnModelChanged(object sender, EventArgs e)
        {
            if (!IsHandleCreated || !Visible) return;

            // A row that has just gone unavailable grows by an explanation line,
            // so the panel needs measuring and repositioning, not just repainting.
            if (AvailabilitySignature() != _laidOutAvailability)
            {
                BuildLayout();
                PositionNear(_anchor);
            }
            Invalidate();
        }

        private string AvailabilitySignature()
        {
            var sb = new StringBuilder(_monitors.Count);
            foreach (MonitorModel m in _monitors) sb.Append(m.Available ? '1' : '0');
            return sb.ToString();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HidePanel();
        }

        public void HidePanel()
        {
            if (!Visible) return;
            _dragIndex = -1;
            _hoverIndex = -1;
            LastHidden = DateTime.UtcNow;
            Hide();
            Detach();
            _monitors = new List<MonitorModel>();
            _rows.Clear();
        }

        private void ApplyWindowStyling()
        {
            try
            {
                int dark = Theme.AppsLight ? 0 : 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int round = Native.DWMWCP_ROUND;
                int hr = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                if (hr != 0) ApplyRegionFallback();   // pre-Windows 11
            }
            catch (Exception ex)
            {
                Log.Write("ApplyWindowStyling: " + ex.Message);
                ApplyRegionFallback();
            }
        }

        private void ApplyRegionFallback()
        {
            try
            {
                using (GraphicsPath p = RoundedRect(new Rectangle(0, 0, Width, Height), S(8)))
                    Region = new Region(p);
            }
            catch (Exception) { }
        }

        /// <summary>Place the panel against whichever edge the taskbar occupies.</summary>
        private void PositionNear(Native.POINT cursor)
        {
            IntPtr hmon = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFOEX();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX));
            if (!Native.GetMonitorInfo(hmon, ref mi))
            {
                Location = new Point(cursor.x - Width / 2, cursor.y - Height - 12);
                return;
            }

            Native.RECT work = mi.rcWork, full = mi.rcMonitor;
            int margin = S(10);
            int x, y;

            if (work.bottom < full.bottom)          // taskbar along the bottom
            {
                x = cursor.x - Width / 2;
                y = work.bottom - Height - margin;
            }
            else if (work.top > full.top)           // taskbar along the top
            {
                x = cursor.x - Width / 2;
                y = work.top + margin;
            }
            else if (work.left > full.left)         // taskbar down the left
            {
                x = work.left + margin;
                y = cursor.y - Height / 2;
            }
            else if (work.right < full.right)       // taskbar down the right
            {
                x = work.right - Width - margin;
                y = cursor.y - Height / 2;
            }
            else                                    // auto-hidden taskbar
            {
                x = cursor.x - Width / 2;
                y = work.bottom - Height - margin;
            }

            if (x < work.left + margin) x = work.left + margin;
            if (x + Width > work.right - margin) x = work.right - Width - margin;
            if (y < work.top + margin) y = work.top + margin;
            if (y + Height > work.bottom - margin) y = work.bottom - Height - margin;

            Location = new Point(x, y);
        }

        // -------------------------------------------------------------------
        // Layout
        // -------------------------------------------------------------------

        private int S(double v) { return (int)Math.Round(v * _scale); }

        private void BuildLayout()
        {
            if (_fontName != null) _fontName.Dispose();
            if (_fontValue != null) _fontValue.Dispose();
            if (_fontFoot != null) _fontFoot.Dispose();
            _fontName = new Font("Segoe UI Semibold", S(12.5), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontValue = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontFoot = new Font("Segoe UI", S(11), FontStyle.Regular, GraphicsUnit.Pixel);

            _rows.Clear();
            _laidOutAvailability = AvailabilitySignature();

            int width = S(320);
            int padX = S(15);
            int y = S(13);

            if (_monitors.Count == 0)
            {
                Size = new Size(width, S(74));
                return;
            }

            int thumbR = S(7);

            for (int i = 0; i < _monitors.Count; i++)
            {
                if (i > 0) y += S(13);   // separator band

                var row = new Row();
                row.Model = _monitors[i];
                row.Header = new Rectangle(padX, y, width - padX * 2, S(19));
                y += S(19) + S(3);

                row.Slider = new Rectangle(padX, y, width - padX * 2, S(26));

                int sunSmall = S(15);
                int sunLarge = S(19);
                int gap = S(9);
                int trackLeft = row.Slider.Left + sunSmall + gap;
                int trackRight = row.Slider.Right - sunLarge - gap;
                row.Track = new Rectangle(trackLeft, row.Slider.Top, Math.Max(trackRight - trackLeft, thumbR * 4), row.Slider.Height);

                y += S(26);

                if (!row.Model.Available)
                {
                    row.ShowStatus = true;
                    row.Status = new Rectangle(padX, y, width - padX * 2, S(17));
                    y += S(17);
                }

                _rows.Add(row);
            }

            y += S(11);              // footer band
            y += S(16);
            y += S(9);

            Size = new Size(width, y);
        }

        // -------------------------------------------------------------------
        // Painting
        // -------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var bounds = new Rectangle(0, 0, Width, Height);
            using (var back = new SolidBrush(Theme.Background))
                g.FillRectangle(back, bounds);

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(8)))
            using (var pen = new Pen(Theme.Border, 1f))
                g.DrawPath(pen, path);

            if (_rows.Count == 0)
            {
                using (var brush = new SolidBrush(Theme.TextDim))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("No displays detected", _fontValue, brush, bounds, fmt);
                return;
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];

                if (i > 0)
                {
                    int sy = row.Header.Top - S(7);
                    using (var pen = new Pen(Theme.Separator, 1f))
                        g.DrawLine(pen, S(15), sy, Width - S(15), sy);
                }

                PaintRow(g, row, i);
            }

            PaintFooter(g);
        }

        private void PaintRow(Graphics g, Row row, int index)
        {
            MonitorModel m = row.Model;
            int value = m.Brightness;
            bool dead = !m.Available;

            Color textColor = dead ? Theme.TextFaint : Theme.Text;
            Color dimColor = dead ? Theme.TextFaint : Theme.TextDim;

            // Name, truncated with an ellipsis if the monitor has a long EDID name.
            string valueText = value.ToString() + "%";
            SizeF valueSize = g.MeasureString("100%", _fontValue);
            int valueWidth = (int)Math.Ceiling(valueSize.Width) + S(2);

            var nameRect = new RectangleF(row.Header.Left, row.Header.Top,
                                          row.Header.Width - valueWidth - S(8), row.Header.Height);
            using (var brush = new SolidBrush(textColor))
            using (var fmt = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = StringAlignment.Center
            })
                g.DrawString(m.Name, _fontName, brush, nameRect, fmt);

            var valueRect = new RectangleF(row.Header.Right - valueWidth, row.Header.Top,
                                           valueWidth, row.Header.Height);
            using (var brush = new SolidBrush(dimColor))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                g.DrawString(valueText, _fontValue, brush, valueRect, fmt);

            // Sun glyphs bracketing the groove.
            int cy = row.Slider.Top + row.Slider.Height / 2;
            DrawSun(g, new Point(row.Slider.Left + S(7), cy), S(4.0), false, dimColor);
            DrawSun(g, new Point(row.Slider.Right - S(9), cy), S(4.6), true, dimColor);

            // Groove, fill and thumb.
            int thumbR = S(7);
            int grooveH = S(4);
            int travelLeft = row.Track.Left + thumbR;
            int travelRight = row.Track.Right - thumbR;
            int travel = Math.Max(travelRight - travelLeft, 1);
            int thumbX = travelLeft + (int)Math.Round(travel * value / 100.0);

            var groove = new Rectangle(row.Track.Left, cy - grooveH / 2, row.Track.Width, grooveH);
            using (GraphicsPath p = RoundedRect(groove, grooveH / 2f))
            using (var brush = new SolidBrush(Theme.Track))
                g.FillPath(brush, p);

            int fillWidth = thumbX - groove.Left;
            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(groove.Left, groove.Top, Math.Max(fillWidth, grooveH), grooveH);
                using (GraphicsPath p = RoundedRect(fillRect, grooveH / 2f))
                using (var brush = new SolidBrush(dead ? Theme.Track : Theme.Fill))
                    g.FillPath(brush, p);
            }

            bool active = _dragIndex == index || _hoverIndex == index || _focusIndex == index;
            int r = active ? thumbR : thumbR - S(1);
            var thumbRect = new Rectangle(thumbX - r, cy - r, r * 2, r * 2);
            using (var brush = new SolidBrush(Theme.Thumb))
                g.FillEllipse(brush, thumbRect);
            using (var pen = new Pen(Theme.ThumbBorder, 1f))
                g.DrawEllipse(pen, thumbRect);

            if (_focusIndex == index && !dead)
            {
                using (var pen = new Pen(Theme.Fill, Math.Max(1f, _scale * 1.5f)))
                    g.DrawEllipse(pen, Rectangle.Inflate(thumbRect, S(3), S(3)));
            }

            // Say why a dead row is dead. A greyed-out slider with no
            // explanation just reads as a broken app.
            if (row.ShowStatus)
            {
                string message = m.StatusMessage;
                if (!string.IsNullOrEmpty(message))
                {
                    using (var brush = new SolidBrush(Theme.TextFaint))
                    using (var fmt = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap,
                        Trimming = StringTrimming.EllipsisCharacter
                    })
                        g.DrawString(message, _fontFoot, brush, row.Status, fmt);
                }
            }
        }

        private void PaintFooter(Graphics g)
        {
            int y = Height - S(9) - S(16);
            using (var pen = new Pen(Theme.Separator, 1f))
                g.DrawLine(pen, S(15), y - S(6), Width - S(15), y - S(6));

            string hint = !string.IsNullOrEmpty(FooterHint)
                ? FooterHint
                : (Settings.HotkeysEnabled
                    ? "Ctrl+Alt+F2 brighter  ·  Ctrl+Alt+F1 dimmer"
                    : "Right-click the tray icon for options");
            using (var brush = new SolidBrush(Theme.TextFaint))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(hint, _fontFoot, brush,
                             new RectangleF(0, y, Width, S(16)), fmt);
        }

        /// <summary>A small sun: filled disc, plus rays on the "bright" one.</summary>
        private void DrawSun(Graphics g, Point center, double radius, bool rays, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                float r = (float)radius;
                g.FillEllipse(brush, center.X - r, center.Y - r, r * 2, r * 2);
            }
            if (!rays) return;

            using (var pen = new Pen(color, Math.Max(1f, _scale * 1.3f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                double inner = radius + _scale * 2.2;
                double outer = radius + _scale * 4.6;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4.0;
                    g.DrawLine(pen,
                        (float)(center.X + Math.Cos(a) * inner), (float)(center.Y + Math.Sin(a) * inner),
                        (float)(center.X + Math.Cos(a) * outer), (float)(center.Y + Math.Sin(a) * outer));
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.5f || r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // -------------------------------------------------------------------
        // Interaction
        // -------------------------------------------------------------------

        private int RowAt(Point p)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Rectangle grab = _rows[i].Slider;
                grab.Inflate(0, S(6));
                if (grab.Contains(p)) return i;
            }
            return -1;
        }

        private int RowAnywhere(Point p)
        {
            // Wheel should work over the name as well as the groove.
            for (int i = 0; i < _rows.Count; i++)
            {
                int top = _rows[i].Header.Top - S(6);
                int bottom = _rows[i].Slider.Bottom + S(6);
                if (p.Y >= top && p.Y <= bottom) return i;
            }
            return -1;
        }

        private void ApplyFromX(int index, int x)
        {
            Row row = _rows[index];
            int thumbR = S(7);
            int left = row.Track.Left + thumbR;
            int travel = Math.Max(row.Track.Right - thumbR - left, 1);
            int pct = (int)Math.Round((x - left) * 100.0 / travel);
            row.Model.Brightness = pct;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i < 0) return;
            _dragIndex = i;
            _focusIndex = i;
            Capture = true;
            ApplyFromX(i, e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIndex >= 0)
            {
                ApplyFromX(_dragIndex, e.X);
                return;
            }
            int i = RowAt(e.Location);
            if (i != _hoverIndex) { _hoverIndex = i; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragIndex < 0) return;
            Commit(_rows[_dragIndex].Model);
            _dragIndex = -1;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int i = RowAnywhere(e.Location);
            if (i < 0) return;
            _focusIndex = i;
            int steps = e.Delta / 120;
            if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
            _rows[i].Model.Nudge(steps * Settings.Step);
            Commit(_rows[i].Model);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_rows.Count == 0) return base.ProcessCmdKey(ref msg, keyData);

            switch (keyData)
            {
                case Keys.Escape:
                    HidePanel();
                    return true;

                case Keys.Up:
                case Keys.Down:
                    _focusIndex += keyData == Keys.Down ? 1 : -1;
                    if (_focusIndex < 0) _focusIndex = _rows.Count - 1;
                    if (_focusIndex >= _rows.Count) _focusIndex = 0;
                    Invalidate();
                    return true;

                case Keys.Left:
                case Keys.Right:
                    if (_focusIndex >= 0 && _focusIndex < _rows.Count)
                    {
                        MonitorModel m = _rows[_focusIndex].Model;
                        m.Nudge(keyData == Keys.Right ? Settings.Step : -Settings.Step);
                        Commit(m);
                        Invalidate();
                    }
                    return true;

                case Keys.Home:
                case Keys.End:
                    if (_focusIndex >= 0 && _focusIndex < _rows.Count)
                    {
                        MonitorModel m = _rows[_focusIndex].Model;
                        m.Brightness = keyData == Keys.End ? 100 : 0;
                        Commit(m);
                        Invalidate();
                    }
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Commit(MonitorModel m)
        {
            EventHandler<MonitorEventArgs> h = ValueCommitted;
            if (h != null) h(this, new MonitorEventArgs(m));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Detach();
                if (_fontName != null) _fontName.Dispose();
                if (_fontValue != null) _fontValue.Dispose();
                if (_fontFoot != null) _fontFoot.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class MonitorEventArgs : EventArgs
    {
        public readonly MonitorModel Monitor;
        public MonitorEventArgs(MonitorModel m) { Monitor = m; }
    }
}
