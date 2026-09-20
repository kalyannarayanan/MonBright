//
//  Log.cs
//  MonBright for Windows — diagnostic log, written only when something fails.
//

using System;
using System.IO;
using System.Text;

namespace MonBright
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly DateTime Start = DateTime.UtcNow;
        private static string _path;
        private const long MaxBytes = 256 * 1024;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MonBright");
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception) { dir = System.IO.Path.GetTempPath(); }
                    _path = System.IO.Path.Combine(dir, "monbright.log");
                }
                return _path;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    // Keep the file from growing without bound across long uptimes.
                    try
                    {
                        var fi = new FileInfo(Path);
                        if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                    }
                    catch (Exception) { }

                    string line = string.Format("[{0:0.000}] {1}{2}",
                        (DateTime.UtcNow - Start).TotalSeconds, message, Environment.NewLine);
                    File.AppendAllText(Path, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never take the app down.
            }
        }
    }
}
