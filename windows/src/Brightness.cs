//
//  Brightness.cs
//  MonBright for Windows — the two ways a screen's backlight can be driven.
//
//    External monitors -> DDC/CI over the video cable, via dxva2.dll's public
//                         Monitor Configuration API. Same VCP code (0x10,
//                         Luminance) the macOS build writes over I2C.
//    Built-in panel    -> WMI's WmiMonitorBrightnessMethods.WmiSetBrightness,
//                         the same path the Windows brightness slider uses.
//
//  Neither needs administrator rights.
//

using System;
using System.Collections.Generic;
using System.Management;

namespace MonBright
{
    // -----------------------------------------------------------------------
    // DDC/CI — external monitors
    // -----------------------------------------------------------------------

    internal sealed class DdcRange
    {
        public uint Min;
        public uint Max = 100;
        public bool Known;
    }

    internal static class Ddc
    {
        /// <summary>
        /// Read the monitor's brightness range and current value. Ranges are
        /// not always 0-100 — some panels report 0-10 or 20-80 — so everything
        /// downstream works in percent and converts here.
        /// </summary>
        // Note: `handle` is never validated against zero. It is an opaque DDC
        // handle, and zero is a legal value — callers gate on DisplayInfo.HasDdc.
        public static bool TryRead(IntPtr handle, DdcRange range, out int percent)
        {
            percent = 0;

            uint min = 0, cur = 0, max = 0;
            if (Native.GetMonitorBrightness(handle, ref min, ref cur, ref max) && max > min)
            {
                range.Min = min;
                range.Max = max;
                range.Known = true;
                percent = ToPercent(cur, min, max);
                return true;
            }

            uint vcur = 0, vmax = 0;
            if (Native.GetVCPFeatureAndVCPFeatureReply(handle, Native.VCP_LUMINANCE, IntPtr.Zero, ref vcur, ref vmax)
                && vmax > 0)
            {
                range.Min = 0;
                range.Max = vmax;
                range.Known = true;
                percent = ToPercent(vcur, 0, vmax);
                return true;
            }

            Log.Write("Ddc.TryRead failed err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        public static bool Write(IntPtr handle, DdcRange range, int percent)
        {
            uint raw = FromPercent(percent, range.Min, range.Max);

            if (Native.SetMonitorBrightness(handle, raw)) return true;

            // Some monitors reject the high-level call but honour a raw VCP write.
            if (Native.SetVCPFeature(handle, Native.VCP_LUMINANCE, raw)) return true;

            Log.Write("Ddc.Write(" + percent + ") failed err=" +
                      System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        private static int ToPercent(uint value, uint min, uint max)
        {
            if (max <= min) return 0;
            if (value < min) value = min;
            if (value > max) value = max;
            return (int)Math.Round((value - min) * 100.0 / (max - min));
        }

        private static uint FromPercent(int percent, uint min, uint max)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (max <= min) return min;
            return (uint)Math.Round(min + percent / 100.0 * (max - min));
        }
    }

    // -----------------------------------------------------------------------
    // WMI — the built-in laptop panel
    // -----------------------------------------------------------------------

    internal static class WmiBrightness
    {
        // Normalised device key (DISPLAY\NCP0052\4&...&UID265988, upper case)
        // -> raw WMI InstanceName (…_0). Rebuilt whenever displays change.
        private static Dictionary<string, string> _panels;
        private static readonly object Gate = new object();

        /// <summary>Device keys of every panel exposing a WMI brightness interface.</summary>
        public static HashSet<string> PanelInstanceNames()
        {
            EnsureLoaded(false);
            lock (Gate) return new HashSet<string>(_panels.Keys, StringComparer.Ordinal);
        }

        public static string FindInstance(string deviceKey)
        {
            EnsureLoaded(false);
            lock (Gate)
            {
                string raw;
                return _panels.TryGetValue(deviceKey, out raw) ? raw : null;
            }
        }

        public static void Invalidate()
        {
            lock (Gate) _panels = null;
        }

        private static void EnsureLoaded(bool force)
        {
            lock (Gate)
            {
                if (_panels != null && !force) return;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "root\\wmi", "SELECT InstanceName FROM WmiMonitorBrightnessMethods"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementBaseObject mo in results)
                        {
                            using (mo)
                            {
                                object v = mo["InstanceName"];
                                if (v == null) continue;
                                string raw = v.ToString();
                                map[Normalise(raw)] = raw;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Desktops have no internal panel; the class may not exist.
                    Log.Write("WmiBrightness enumerate: " + ex.Message);
                }
                _panels = map;
            }
        }

        /// <summary>DISPLAY\NCP0052\4&amp;...&amp;UID265988_0 -> …UID265988, upper case.</summary>
        private static string Normalise(string instanceName)
        {
            string s = instanceName;
            int cut = s.LastIndexOf('_');
            if (cut > 0) s = s.Substring(0, cut);
            return s.ToUpperInvariant();
        }

        public static bool TryRead(string instanceName, out int percent)
        {
            percent = 0;
            if (string.IsNullOrEmpty(instanceName)) return false;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        using (mo)
                        {
                            object name = mo["InstanceName"];
                            if (name == null || !string.Equals(name.ToString(), instanceName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            object cur = mo["CurrentBrightness"];
                            if (cur == null) continue;
                            percent = Convert.ToInt32(cur);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("WmiBrightness.TryRead: " + ex.Message);
            }
            return false;
        }

        public static bool Write(string instanceName, int percent)
        {
            if (string.IsNullOrEmpty(instanceName)) return false;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            try
            {
                // Matching in code rather than WQL avoids escaping the
                // backslash-heavy InstanceName inside a query string.
                using (var searcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject mbo in results)
                    {
                        var mo = mbo as ManagementObject;
                        if (mo == null) { mbo.Dispose(); continue; }
                        using (mo)
                        {
                            object name = mo["InstanceName"];
                            if (name == null || !string.Equals(name.ToString(), instanceName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                            return true;
                        }
                    }
                }
                Log.Write("WmiBrightness.Write: instance not found " + instanceName);
            }
            catch (Exception ex)
            {
                Log.Write("WmiBrightness.Write(" + percent + "): " + ex.Message);
            }
            return false;
        }
    }
}
