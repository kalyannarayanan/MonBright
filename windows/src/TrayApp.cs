//
//  TrayApp.cs
//  MonBright for Windows — the tray icon, its menu, and global hotkeys.
//
//  The tray glyph is drawn at runtime rather than shipped as a bitmap, so it
//  lands on an exact pixel grid at any DPI and flips between light and dark
//  when the taskbar theme changes.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonBright
{
    internal sealed class TrayApp : ApplicationContext
    {
        public const string Version = "1.0.0";

        private const int HotkeyBrighten = 1;
        private const int HotkeyDim = 2;
        private const int VK_F1 = 0x70;
        private const int VK_F2 = 0x71;
        private const int HotkeyRepeatMs = 90;

        private readonly NotifyIcon _tray;
        private readonly FlyoutForm _flyout;
        private readonly HotkeyWindow _hotkeys;
        private readonly Timer _rescanTimer;

        private List<DisplayInfo> _displays = new List<DisplayInfo>();
        private List<MonitorModel> _monitors = new List<MonitorModel>();
        private Icon _trayIcon;
        private DateTime _lastHotkey = DateTime.MinValue;
        private bool _hotkeysRegistered;
        private bool _hotkeyConflict;
        private bool _firstScan = true;
        private AboutForm _about;

        private ToolStripMenuItem _miStartup, _miRestore, _miHotkeys;

        public TrayApp()
        {
            _flyout = new FlyoutForm();
            _flyout.ValueCommitted += OnValueCommitted;

            _tray = new NotifyIcon();
            _tray.Text = "MonBright";
            _tray.Visible = true;
            _tray.MouseUp += OnTrayMouseUp;
            _tray.BalloonTipClicked += delegate { ShowPanel(); };
            RefreshTrayIcon();

            _tray.ContextMenuStrip = BuildMenu();

            _hotkeys = new HotkeyWindow();
            _hotkeys.HotkeyPressed = OnHotkey;

            _rescanTimer = new Timer();
            _rescanTimer.Interval = 700;   // display changes arrive in bursts
            _rescanTimer.Tick += delegate
            {
                _rescanTimer.Stop();
                Rescan();
            };

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            Rescan();

            // Windows 11 files new tray icons away in the hidden overflow, so
            // without this a first run looks exactly like nothing happening.
            bool firstRun = !Settings.FirstRunDone;
            if (firstRun)
            {
                Settings.FirstRunDone = true;
                Notify("MonBright is running",
                       "New icons start hidden: click the ^ arrow near the clock, then drag "
                     + "MonBright onto the taskbar to keep it there. Click here to open it now.");
            }

            // Don't stack two balloons on the very first launch.
            ApplyHotkeyRegistration(!firstRun);
        }

        private void Notify(string title, string text)
        {
            try { _tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info); }
            catch (Exception ex) { Log.Write("balloon tip: " + ex.Message); }
        }

        // -------------------------------------------------------------------
        // Tray icon
        // -------------------------------------------------------------------

        private void RefreshTrayIcon()
        {
            Icon old = _trayIcon;
            _trayIcon = BuildTrayIcon(Theme.TaskbarLight);
            _tray.Icon = _trayIcon;
            if (old != null) old.Dispose();
        }

        private static Icon BuildTrayIcon(bool lightTaskbar)
        {
            Size sz = SystemInformation.SmallIconSize;
            int w = Math.Max(sz.Width, 16), h = Math.Max(sz.Height, 16);

            using (var bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    Color c = lightTaskbar ? Color.FromArgb(26, 26, 26) : Color.White;
                    float stroke = Math.Max(1.25f, w / 13f);

                    // Screen
                    var screen = new RectangleF(w * 0.07f, h * 0.13f, w * 0.86f, h * 0.60f);
                    using (var pen = new Pen(c, stroke))
                    using (var path = RoundedRectF(screen, w * 0.11f))
                        g.DrawPath(pen, path);

                    // Stand
                    using (var pen = new Pen(c, stroke))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawLine(pen, w * 0.5f, screen.Bottom + stroke * 0.2f, w * 0.5f, h * 0.85f);
                        g.DrawLine(pen, w * 0.27f, h * 0.90f, w * 0.73f, h * 0.90f);
                    }

                    // Brightness dot
                    using (var brush = new SolidBrush(c))
                    {
                        float r = w * 0.10f;
                        g.FillEllipse(brush, w * 0.5f - r, screen.Top + screen.Height * 0.5f - r, r * 2, r * 2);
                    }
                }

                IntPtr hicon = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(hicon))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(hicon);
                }
            }
        }

        private static GraphicsPath RoundedRectF(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // -------------------------------------------------------------------
        // Menu
        // -------------------------------------------------------------------

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Renderer = new FlatMenuRenderer();

            var header = new ToolStripMenuItem("MonBright " + Version);
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            _miStartup = new ToolStripMenuItem("Start with Windows");
            _miStartup.Click += delegate
            {
                Settings.StartWithWindows = !Settings.StartWithWindows;
                SyncMenuState();
            };
            menu.Items.Add(_miStartup);

            _miRestore = new ToolStripMenuItem("Restore brightness at startup");
            _miRestore.Click += delegate
            {
                Settings.RestoreOnStart = !Settings.RestoreOnStart;
                SyncMenuState();
            };
            menu.Items.Add(_miRestore);

            _miHotkeys = new ToolStripMenuItem("Global hotkeys (Ctrl+Alt+F1/F2)");
            _miHotkeys.Click += delegate
            {
                Settings.HotkeysEnabled = !Settings.HotkeysEnabled;
                ApplyHotkeyRegistration(true);
                SyncMenuState();
            };
            menu.Items.Add(_miHotkeys);

            menu.Items.Add(new ToolStripSeparator());

            var refresh = new ToolStripMenuItem("Refresh displays");
            refresh.Click += delegate { Rescan(); };
            menu.Items.Add(refresh);

            var about = new ToolStripMenuItem("About MonBright");
            about.Click += delegate { ShowAbout(); };
            menu.Items.Add(about);

            menu.Items.Add(new ToolStripSeparator());

            var quit = new ToolStripMenuItem("Quit MonBright");
            quit.Click += delegate { ExitApp(); };
            menu.Items.Add(quit);

            menu.Opening += delegate { SyncMenuState(); };
            return menu;
        }

        private void ShowAbout()
        {
            if (_about != null && !_about.IsDisposed)
            {
                _about.Activate();
                return;
            }
            _about = new AboutForm();
            _about.FormClosed += delegate { _about = null; };
            _about.Show();
            _about.Activate();
        }

        private void SyncMenuState()
        {
            _miStartup.Checked = Settings.StartWithWindows;
            _miRestore.Checked = Settings.RestoreOnStart;
            _miHotkeys.Checked = Settings.HotkeysEnabled;
        }

        // -------------------------------------------------------------------
        // Tray interaction
        // -------------------------------------------------------------------

        private void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // The flyout hides itself when it loses focus, which happens the
            // instant the tray icon is clicked. Without this guard, clicking an
            // open panel closes and immediately reopens it.
            if ((DateTime.UtcNow - _flyout.LastHidden).TotalMilliseconds < 250) return;

            if (_flyout.Visible) _flyout.HidePanel();
            else ShowPanel();
        }

        private void ShowPanel()
        {
            _flyout.FooterHint = BuildFooterHint();
            _flyout.ShowFor(_monitors);
        }

        /// <summary>Null lets the panel use its own default hint.</summary>
        private string BuildFooterHint()
        {
            if (Settings.HotkeysEnabled && _hotkeyConflict)
                return "Ctrl+Alt+F1/F2 are in use by another app";
            return null;
        }

        // -------------------------------------------------------------------
        // Hotkeys
        // -------------------------------------------------------------------

        private void ApplyHotkeyRegistration(bool announce)
        {
            if (_hotkeysRegistered)
            {
                Native.UnregisterHotKey(_hotkeys.Handle, HotkeyBrighten);
                Native.UnregisterHotKey(_hotkeys.Handle, HotkeyDim);
                _hotkeysRegistered = false;
            }
            if (!Settings.HotkeysEnabled)
            {
                _hotkeyConflict = false;
                return;
            }

            uint mods = Native.MOD_CONTROL | Native.MOD_ALT;
            bool a = Native.RegisterHotKey(_hotkeys.Handle, HotkeyBrighten, mods, VK_F2);
            bool b = Native.RegisterHotKey(_hotkeys.Handle, HotkeyDim, mods, VK_F1);
            _hotkeysRegistered = a || b;
            _hotkeyConflict = !a || !b;

            if (!_hotkeyConflict) return;

            // Registration losing to another app used to fail silently into the
            // log, which nobody reads - the shortcuts just seemed broken.
            Log.Write("RegisterHotKey failed (brighten=" + a + " dim=" + b +
                      ") - another app already owns Ctrl+Alt+F1/F2.");
            if (announce)
            {
                Notify("Keyboard shortcuts unavailable",
                       "Another app is already using Ctrl+Alt+F1 and Ctrl+Alt+F2. "
                     + "The sliders still work normally.");
            }
        }

        private void OnHotkey(int id)
        {
            // Windows repeats WM_HOTKEY at the keyboard repeat rate while the
            // combination is held; throttle it to a steady ramp.
            double since = (DateTime.UtcNow - _lastHotkey).TotalMilliseconds;
            if (since < HotkeyRepeatMs) return;
            _lastHotkey = DateTime.UtcNow;

            if (_monitors.Count == 0) return;

            MonitorModel target = null;
            DisplayInfo under = Displays.UnderCursor(_displays);
            if (under != null)
            {
                foreach (MonitorModel m in _monitors)
                    if (m.Display.HMonitor == under.HMonitor) { target = m; break; }
            }
            if (target == null) target = _monitors[0];

            target.Nudge(id == HotkeyBrighten ? Settings.Step : -Settings.Step);
            Settings.SetBrightness(target.Id, target.Brightness);
            if (_flyout.Visible) _flyout.Invalidate();
        }

        // -------------------------------------------------------------------
        // Display scanning
        // -------------------------------------------------------------------

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _rescanTimer.Stop();
            _rescanTimer.Start();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Color ||
                e.Category == UserPreferenceCategory.VisualStyle)
            {
                RefreshTrayIcon();
                if (_flyout.Visible) _flyout.Invalidate();
            }
        }

        private void Rescan()
        {
            // Remember where each monitor was before tearing the models down.
            var previous = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MonitorModel m in _monitors) previous[m.Id] = m.Brightness;

            if (_flyout.Visible) _flyout.HidePanel();

            foreach (MonitorModel m in _monitors) m.Dispose();
            _monitors.Clear();
            Displays.Release(_displays);

            WmiBrightness.Invalidate();
            _displays = Displays.Enumerate();

            foreach (DisplayInfo d in _displays)
            {
                var model = new MonitorModel(d);

                int? restore = null;
                if (Settings.RestoreOnStart)
                {
                    int prev;
                    if (previous.TryGetValue(d.Id, out prev)) restore = prev;
                    else restore = Settings.GetBrightness(d.Id);
                }

                model.BeginInitialize(restore);
                _monitors.Add(model);
            }

            _tray.Text = BuildTooltip();
            if (_firstScan)
            {
                _firstScan = false;
                Log.Write("Detected " + _displays.Count + " display(s).");
                foreach (DisplayInfo d in _displays)
                    Log.Write("  " + d.Id + "  \"" + d.Name + "\"  internal=" + d.IsInternal +
                              "  ddc=" + d.HasDdc);
            }
        }

        private string BuildTooltip()
        {
            // NotifyIcon.Text is capped at 63 characters.
            string text = _monitors.Count == 0
                ? "MonBright — no displays detected"
                : "MonBright — " + _monitors.Count + (_monitors.Count == 1 ? " display" : " displays");
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void OnValueCommitted(object sender, MonitorEventArgs e)
        {
            Settings.SetBrightness(e.Monitor.Id, e.Monitor.Brightness);
        }

        // -------------------------------------------------------------------
        // Shutdown
        // -------------------------------------------------------------------

        private void ExitApp()
        {
            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

                if (_hotkeysRegistered)
                {
                    Native.UnregisterHotKey(_hotkeys.Handle, HotkeyBrighten);
                    Native.UnregisterHotKey(_hotkeys.Handle, HotkeyDim);
                }
                _hotkeys.Dispose();

                _rescanTimer.Dispose();
                foreach (MonitorModel m in _monitors) m.Dispose();
                _monitors.Clear();
                Displays.Release(_displays);

                if (_about != null && !_about.IsDisposed) _about.Dispose();
                _flyout.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                if (_trayIcon != null) _trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        // -------------------------------------------------------------------
        // Hidden window that receives WM_HOTKEY
        // -------------------------------------------------------------------

        private sealed class HotkeyWindow : NativeWindow, IDisposable
        {
            public Action<int> HotkeyPressed;

            public HotkeyWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_HOTKEY)
                {
                    Action<int> h = HotkeyPressed;
                    if (h != null) h(m.WParam.ToInt32());
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }
    }

    /// <summary>Flat, theme-following renderer so the menu matches the flyout.</summary>
    internal sealed class FlatMenuRenderer : ToolStripProfessionalRenderer
    {
        public FlatMenuRenderer() : base(new FlatColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Background))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Separator))
                e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var item = e.Item as ToolStripMenuItem;
            if (item == null || !item.Checked) return;

            Rectangle r = e.ImageRectangle;
            using (var pen = new Pen(Theme.Fill, 2f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float x = r.Left + r.Width * 0.22f, y = r.Top + r.Height * 0.52f;
                e.Graphics.DrawLine(pen, x, y, x + r.Width * 0.22f, y + r.Height * 0.24f);
                e.Graphics.DrawLine(pen, x + r.Width * 0.22f, y + r.Height * 0.24f,
                                    r.Left + r.Width * 0.80f, r.Top + r.Height * 0.28f);
            }
        }

        private sealed class FlatColors : ProfessionalColorTable
        {
            public FlatColors() { UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return Theme.Background; } }
            public override Color MenuItemSelected { get { return Theme.RowHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.RowHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.RowHover; } }
            public override Color MenuItemBorder { get { return Theme.RowHover; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Background; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Background; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Background; } }
            public override Color SeparatorDark { get { return Theme.Separator; } }
            public override Color SeparatorLight { get { return Theme.Separator; } }
        }
    }
}
