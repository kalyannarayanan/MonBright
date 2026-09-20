//
//  Settings.cs
//  MonBright for Windows — a plain key=value file in %APPDATA%\MonBright.
//
//  No JSON dependency, no installer, nothing outside the user's own profile.
//  Uninstalling is deleting the exe and this folder.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace MonBright
{
    internal static class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MonBright";

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _values;

        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MonBright");
            }
        }

        private static string FilePath { get { return Path.Combine(Directory, "settings.ini"); } }

        private static Dictionary<string, string> Values
        {
            get
            {
                lock (Gate)
                {
                    if (_values == null) Load();
                    return _values;
                }
            }
        }

        private static void Load()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings.Load: " + ex.Message);
            }
            _values = map;
        }

        private static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var sb = new StringBuilder();
                sb.AppendLine("# MonBright settings — safe to delete.");
                lock (Gate)
                {
                    foreach (KeyValuePair<string, string> kv in _values)
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                }
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Write("Settings.Save: " + ex.Message);
            }
        }

        private static string Get(string key, string fallback)
        {
            lock (Gate)
            {
                string v;
                return Values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        private static void Set(string key, string value)
        {
            lock (Gate) { Values[key] = value; }
            Save();
        }

        // -------------------------------------------------------------------
        // Remembered brightness, keyed by the monitor's EDID-derived id
        // -------------------------------------------------------------------

        public static int? GetBrightness(string monitorId)
        {
            string v = Get("brightness." + monitorId, null);
            int n;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return Math.Max(0, Math.Min(100, n));
            return null;
        }

        public static void SetBrightness(string monitorId, int percent)
        {
            Set("brightness." + monitorId, percent.ToString(CultureInfo.InvariantCulture));
        }

        // -------------------------------------------------------------------
        // Options
        // -------------------------------------------------------------------

        public static bool RestoreOnStart
        {
            get { return Get("restoreOnStart", "1") != "0"; }
            set { Set("restoreOnStart", value ? "1" : "0"); }
        }

        public static bool HotkeysEnabled
        {
            get { return Get("hotkeys", "1") != "0"; }
            set { Set("hotkeys", value ? "1" : "0"); }
        }

        /// <summary>
        /// False until the app has explained itself once. Windows 11 drops new
        /// tray icons into the hidden overflow, so a first run otherwise looks
        /// like nothing happened at all.
        /// </summary>
        public static bool FirstRunDone
        {
            get { return Get("firstRunDone", "0") != "0"; }
            set { Set("firstRunDone", value ? "1" : "0"); }
        }

        public static int Step
        {
            get
            {
                int n;
                if (int.TryParse(Get("step", "5"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                    && n >= 1 && n <= 25) return n;
                return 5;
            }
        }

        // -------------------------------------------------------------------
        // Start with Windows — per-user Run key, no admin rights needed
        // -------------------------------------------------------------------

        public static bool StartWithWindows
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (key == null) return false;
                        return key.GetValue(RunValue) != null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("StartWithWindows read: " + ex.Message);
                    return false;
                }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (key == null) return;
                        if (value)
                        {
                            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            key.SetValue(RunValue, "\"" + exe + "\"");
                        }
                        else if (key.GetValue(RunValue) != null)
                        {
                            key.DeleteValue(RunValue, false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("StartWithWindows write: " + ex.Message);
                }
            }
        }
    }
}
