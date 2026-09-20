//
//  Theme.cs
//  MonBright for Windows — follow the system's light/dark setting and accent.
//
//  Windows exposes both as registry values that change live, so the flyout is
//  repainted from these rather than from a palette baked in at build time.
//

using System;
using System.Drawing;
using Microsoft.Win32;

namespace MonBright
{
    internal static class Theme
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>True when app surfaces should be light.</summary>
        public static bool AppsLight { get { return ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) != 0; } }

        /// <summary>True when the taskbar is light — drives the tray glyph colour.</summary>
        public static bool TaskbarLight { get { return ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) != 0; } }

        public static Color Background { get { return AppsLight ? Rgb(0xF9F9F9) : Rgb(0x202020); } }
        public static Color Border { get { return AppsLight ? Rgb(0xE1E1E1) : Rgb(0x3B3B3B); } }
        public static Color Text { get { return AppsLight ? Rgb(0x1A1A1A) : Rgb(0xFFFFFF); } }
        public static Color TextDim { get { return AppsLight ? Rgb(0x616161) : Rgb(0xA3A3A3); } }
        public static Color TextFaint { get { return AppsLight ? Rgb(0x8A8A8A) : Rgb(0x7A7A7A); } }
        public static Color Track { get { return AppsLight ? Rgb(0xCFCFCF) : Rgb(0x4A4A4A); } }
        public static Color Separator { get { return AppsLight ? Rgb(0xEAEAEA) : Rgb(0x2F2F2F); } }
        public static Color Thumb { get { return AppsLight ? Color.White : Color.White; } }
        public static Color ThumbBorder { get { return AppsLight ? Rgb(0xB0B0B0) : Rgb(0x101010); } }
        public static Color RowHover { get { return AppsLight ? Rgb(0xF0F0F0) : Rgb(0x2A2A2A); } }

        /// <summary>
        /// The user's accent colour, nudged until it reads clearly against the
        /// current background. Accents like navy on dark, or pale yellow on
        /// light, are unusable as a track fill straight from the registry.
        /// </summary>
        public static Color Fill
        {
            get
            {
                Color c = RawAccent();
                bool light = AppsLight;
                double lum = Luminance(c);

                if (!light)
                {
                    // On a dark panel the fill must be bright enough to read.
                    int guard = 0;
                    while (lum < 0.42 && guard++ < 24) { c = Lighten(c, 0.10); lum = Luminance(c); }
                }
                else
                {
                    int guard = 0;
                    while (lum > 0.66 && guard++ < 24) { c = Darken(c, 0.10); lum = Luminance(c); }
                }
                return c;
            }
        }

        private static Color RawAccent()
        {
            // HKCU\Software\Microsoft\Windows\DWM\AccentColor is 0xAABBGGRR.
            int v = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD77800));
            int r = v & 0xFF;
            int g = (v >> 8) & 0xFF;
            int b = (v >> 16) & 0xFF;
            if (r == 0 && g == 0 && b == 0) return Rgb(0x0078D4); // Windows default blue
            return Color.FromArgb(r, g, b);
        }

        public static double Luminance(Color c)
        {
            // Rec. 709 relative luminance, good enough for a contrast nudge.
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }

        public static Color Lighten(Color c, double amount)
        {
            return Color.FromArgb(
                Clamp(c.R + (int)((255 - c.R) * amount)),
                Clamp(c.G + (int)((255 - c.G) * amount)),
                Clamp(c.B + (int)((255 - c.B) * amount)));
        }

        public static Color Darken(Color c, double amount)
        {
            return Color.FromArgb(
                Clamp((int)(c.R * (1 - amount))),
                Clamp((int)(c.G * (1 - amount))),
                Clamp((int)(c.B * (1 - amount))));
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        private static Color Rgb(int hex)
        {
            return Color.FromArgb((hex >> 16) & 0xFF, (hex >> 8) & 0xFF, hex & 0xFF);
        }

        private static int ReadDword(string subKey, string name, int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (key == null) return fallback;
                    object v = key.GetValue(name);
                    if (v is int) return (int)v;
                    return fallback;
                }
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
