//
//  DimOverlay.cs
//  MonBright for Windows - dims the picture below the panel's hardware floor.
//
//  A black, click-through, always-on-top window laid over one monitor, with
//  its opacity set to (1 - scale). Alpha-blending toward black is the same
//  linear scaling the macOS build gets from the display gamma ramp.
//
//  Why an overlay and not SetDeviceGammaRamp: Windows clamps the ramp to
//  roughly half brightness unless an admin-only registry key is set, and
//  Night Light writes the same ramp, so the two would fight. The overlay
//  needs no rights and leaves Night Light alone.
//
//  SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) keeps it out of
//  screenshots and screen sharing, so what you show on a call is undimmed.
//
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MonBright
{
    internal sealed class DimOverlay : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        private readonly Native.RECT _bounds;

        public DimOverlay(Native.RECT monitorBounds)
        {
            _bounds = monitorBounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Opacity = 0.0;
            Enabled = false;   // never takes focus or input
            ApplyBounds();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
                            | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE
                            | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 10 2004 and later. Older builds return false: the overlay
            // still dims, it just shows up in captures. Log once so it's diagnosable.
            if (!Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE))
                Log.Write("DimOverlay: capture exclusion unavailable on this Windows build");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                // Keep the physical monitor rectangle; ignore the suggested logical resize.
                ApplyBounds();
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>1.0 hides the overlay; anything less shows it at (1 - scale) opacity.</summary>
        public void SetScale(double scale)
        {
            if (IsDisposed) return;
            if (scale >= 0.999)
            {
                if (Visible) Hide();
                return;
            }
            if (scale < 0.0) scale = 0.0;
            Opacity = 1.0 - scale;
            if (!Visible)
            {
                ApplyBounds();
                Show();
            }
        }

        private void ApplyBounds()
        {
            SetBounds(_bounds.left, _bounds.top, _bounds.Width, _bounds.Height);
        }
    }
}
