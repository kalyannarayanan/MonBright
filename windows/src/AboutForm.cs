//
//  AboutForm.cs
//  MonBright for Windows - the About window.
//
//  Unlike the flyout, this is built from real WinForms controls rather than
//  being owner-drawn. It costs a little more layout code, but Narrator and
//  other assistive tech can actually read it - which an owner-drawn surface
//  cannot offer without hand-written UI Automation peers.
//

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MonBright
{
    internal sealed class AboutForm : Form
    {
        private const string RepoUrl = "https://github.com/kalyannarayanan/MonBright";
        private const string IssuesUrl = RepoUrl + "/issues";
        private const string ReleasesUrl = RepoUrl + "/releases/latest";

        private readonly float _scale;

        public AboutForm()
        {
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) { cursor.x = 0; cursor.y = 0; }
            _scale = Native.DpiAt(cursor) / 96f;

            Text = "About MonBright";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            ClientSize = new Size(S(392), S(322));
            KeyPreview = true;

            BuildContent();
        }

        private int S(double v) { return (int)Math.Round(v * _scale); }

        private Font Px(double size, bool semibold)
        {
            return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI",
                            S(size), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar to match the rest of the app.
            try
            {
                int dark = Theme.AppsLight ? 0 : 1;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch (Exception ex) { Log.Write("About dark titlebar: " + ex.Message); }
        }

        private void BuildContent()
        {
            int pad = S(24);

            // ---- App icon, pulled from the exe's own embedded resource ----
            var icon = new PictureBox();
            icon.Bounds = new Rectangle(pad, S(26), S(64), S(64));
            icon.SizeMode = PictureBoxSizeMode.Zoom;
            // An explicit colour, not Transparent: the icon has alpha around
            // its rounded corners, and Transparent composites against white
            // rather than the form behind it.
            icon.BackColor = Theme.Background;
            icon.TabStop = false;
            icon.Image = LoadAppIcon(S(64));
            Controls.Add(icon);

            int textLeft = pad + S(64) + S(18);

            var title = new Label();
            title.Text = "MonBright";
            title.Font = Px(27, true);
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.Location = new Point(textLeft, S(32));
            Controls.Add(title);

            var version = new Label();
            version.Text = "Version " + TrayApp.Version;
            version.Font = Px(13.5, false);
            version.ForeColor = Theme.TextDim;
            version.AutoSize = true;
            version.Location = new Point(textLeft, S(68));
            Controls.Add(version);

            var blurb = new Label();
            blurb.Text = "A brightness slider for every monitor on your PC.";
            blurb.Font = Px(13.5, false);
            blurb.ForeColor = Theme.TextDim;
            blurb.Bounds = new Rectangle(pad, S(110), ClientSize.Width - pad * 2, S(24));
            Controls.Add(blurb);

            int y = S(150);
            AddLink("View the source on GitHub", y, delegate { Open(RepoUrl); });
            y += S(27);
            AddLink("Check for updates on GitHub", y, delegate { Open(ReleasesUrl); });
            y += S(27);
            AddLink("Report an issue", y, delegate { Open(IssuesUrl); });
            y += S(27);
            AddLink("Open the diagnostic log folder", y, delegate { RevealLog(); });

            var license = new Label();
            license.Text = "MIT licensed. Copyright (c) Kalyan.";
            license.Font = Px(12.5, false);
            license.ForeColor = Theme.TextFaint;
            license.AutoSize = true;
            license.Location = new Point(pad, ClientSize.Height - S(37));
            Controls.Add(license);

            var close = new Button();
            close.Text = "Close";
            close.Font = Px(13.5, false);
            close.FlatStyle = FlatStyle.Flat;
            close.BackColor = Theme.RowHover;
            close.ForeColor = Theme.Text;
            close.FlatAppearance.BorderColor = Theme.Border;
            close.Size = new Size(S(84), S(30));
            close.Location = new Point(ClientSize.Width - pad - close.Width, ClientSize.Height - S(44));
            close.Click += delegate { Close(); };
            Controls.Add(close);

            AcceptButton = close;
            CancelButton = close;   // Esc closes
        }

        private void AddLink(string text, int y, EventHandler onClick)
        {
            var link = new LinkLabel();
            link.Text = text;
            link.Font = Px(13.5, false);
            link.AutoSize = true;
            link.Location = new Point(S(24), y);
            link.BackColor = Color.Transparent;
            link.LinkColor = Theme.Fill;
            link.ActiveLinkColor = Theme.Fill;
            link.VisitedLinkColor = Theme.Fill;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkClicked += delegate { onClick(this, EventArgs.Empty); };
            Controls.Add(link);
        }

        /// <summary>
        /// The app icon at a specific pixel size, taken from the exe's own
        /// embedded multi-resolution .ico so it stays crisp at any DPI.
        /// </summary>
        private static Image LoadAppIcon(int size)
        {
            var handles = new IntPtr[1];
            var ids = new IntPtr[1];
            try
            {
                int found = Native.PrivateExtractIcons(
                    Application.ExecutablePath, 0, size, size, handles, ids, 1, 0);
                if (found > 0 && handles[0] != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon ic = Icon.FromHandle(handles[0]))
                            return ic.ToBitmap();
                    }
                    finally { Native.DestroyIcon(handles[0]); }
                }
            }
            catch (Exception ex) { Log.Write("About icon: " + ex.Message); }

            // Fall back to the low-resolution associated icon rather than
            // showing an empty box.
            try
            {
                using (Icon raw = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    if (raw != null) return raw.ToBitmap();
            }
            catch (Exception ex) { Log.Write("About fallback icon: " + ex.Message); }
            return null;
        }

        private static void Open(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { Log.Write("About open " + url + ": " + ex.Message); }
        }

        /// <summary>
        /// Select the log file if it exists; otherwise just open the folder,
        /// because a missing log is the normal case - it is only written when
        /// something fails.
        /// </summary>
        private static void RevealLog()
        {
            try
            {
                string path = Log.Path;
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                string dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Write("About reveal log: " + ex.Message); }
        }
    }
}
