//
//  Displays.cs
//  MonBright for Windows — enumerate displays and give each a stable identity.
//
//  Windows reports almost every monitor as "Generic PnP Monitor", which is
//  useless in a UI. The real name lives in the monitor's EDID blob, which
//  Windows caches in the registry under the device's instance key. We parse
//  it the same way the macOS build parses the EDID it reads over I2C, so a
//  given monitor gets the same MANU-PPPP-SSSSSSSS id on both platforms.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace MonBright
{
    internal sealed class DisplayInfo
    {
        /// <summary>"internal", or an EDID-derived id such as MSI-30B8-0000ABCD.</summary>
        public string Id;
        public string Name;
        public bool IsInternal;
        public IntPtr HMonitor;
        public Native.RECT Bounds;
        public bool IsPrimary;

        /// <summary>e.g. \\.\DISPLAY2</summary>
        public string DeviceName;

        /// <summary>e.g. DISPLAY\NCP0052\4&amp;883a54a&amp;0&amp;UID265988_0 — internal panels only.</summary>
        public string WmiInstanceName;

        /// <summary>
        /// True when this display has a usable DDC/CI handle. Needed as a
        /// separate flag because <see cref="PhysicalHandle"/> is an opaque
        /// value that is legitimately 0 on some machines — treating zero as
        /// "no handle" silently disables working monitors.
        /// </summary>
        public bool HasDdc;

        /// <summary>DDC handle for external monitors. Only meaningful when <see cref="HasDdc"/>.</summary>
        public IntPtr PhysicalHandle;

        /// <summary>Kept so the handle can be released on rescan.</summary>
        public Native.PHYSICAL_MONITOR[] PhysicalArray;
    }

    internal static class Displays
    {
        /// <summary>
        /// Enumerate every connected display. Caller owns the returned list and
        /// must call <see cref="Release"/> on it before enumerating again.
        /// </summary>
        public static List<DisplayInfo> Enumerate()
        {
            var result = new List<DisplayInfo>();

            // Which device instances expose a WMI brightness interface? Exactly
            // the built-in panel does; that is our internal/external test and it
            // is tied directly to the API we would use to drive it.
            HashSet<string> wmiPanels = WmiBrightness.PanelInstanceNames();

            var handles = new List<IntPtr>();
            Native.MonitorEnumProc cb = delegate(IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d)
            {
                handles.Add(h);
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            foreach (IntPtr h in handles)
            {
                var mi = new Native.MONITORINFOEX();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                if (!Native.GetMonitorInfo(h, ref mi)) continue;

                var info = new DisplayInfo();
                info.HMonitor = h;
                info.Bounds = mi.rcMonitor;
                info.DeviceName = mi.szDevice;
                info.IsPrimary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;

                string hardwareId, instanceId;
                GetDeviceIds(mi.szDevice, out hardwareId, out instanceId);

                // Internal panel? Compare against the WMI instance names.
                string devKey = null;
                if (hardwareId != null && instanceId != null)
                    devKey = ("DISPLAY\\" + hardwareId + "\\" + instanceId).ToUpperInvariant();
                info.IsInternal = devKey != null && wmiPanels.Contains(devKey);
                if (info.IsInternal)
                    info.WmiInstanceName = WmiBrightness.FindInstance(devKey);

                // Name + id from the cached EDID blob.
                byte[] edid = ReadEdid(hardwareId, instanceId);
                string edidName = null, edidId = null;
                if (edid != null) ParseEdid(edid, out edidId, out edidName);

                if (info.IsInternal)
                {
                    info.Id = "internal";
                    info.Name = !string.IsNullOrEmpty(edidName) ? edidName : "Built-in Display";
                }
                else
                {
                    info.Id = edidId != null ? edidId : "unknown-" + (hardwareId != null ? hardwareId : mi.szDevice);
                    info.Name = !string.IsNullOrEmpty(edidName) ? edidName : FallbackName(mi.szDevice);
                    info.HasDdc = OpenPhysical(h, out info.PhysicalHandle, out info.PhysicalArray);
                }

                result.Add(info);
            }

            DisambiguateIds(result);
            SortForDisplay(result);
            return result;
        }

        /// <summary>Release the DDC handles held by an enumeration result.</summary>
        public static void Release(List<DisplayInfo> displays)
        {
            if (displays == null) return;
            foreach (DisplayInfo d in displays)
            {
                if (d.PhysicalArray != null && d.PhysicalArray.Length > 0)
                {
                    try { Native.DestroyPhysicalMonitors((uint)d.PhysicalArray.Length, d.PhysicalArray); }
                    catch (Exception ex) { Log.Write("DestroyPhysicalMonitors: " + ex.Message); }
                }
                d.PhysicalArray = null;
                d.PhysicalHandle = IntPtr.Zero;
                d.HasDdc = false;
            }
        }

        /// <summary>The display the mouse cursor is currently on, or null.</summary>
        public static DisplayInfo UnderCursor(List<DisplayInfo> displays)
        {
            if (displays == null || displays.Count == 0) return null;
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return null;
            IntPtr hmon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            foreach (DisplayInfo d in displays)
                if (d.HMonitor == hmon) return d;
            return null;
        }

        // -------------------------------------------------------------------
        // Identity plumbing
        // -------------------------------------------------------------------

        /// <summary>
        /// Turn \\.\DISPLAY2 into its hardware id (MSI30B8) and device instance
        /// (4&amp;883a54a&amp;0&amp;UID198147), via the device interface path.
        /// </summary>
        private static void GetDeviceIds(string deviceName, out string hardwareId, out string instanceId)
        {
            hardwareId = null;
            instanceId = null;

            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            if (!Native.EnumDisplayDevices(deviceName, 0, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME))
                return;

            // \\?\DISPLAY#MSI30B8#4&883a54a&0&UID198147#{e6f07b5f-...}
            string id = dd.DeviceID;
            if (string.IsNullOrEmpty(id)) return;
            string[] parts = id.Split('#');
            if (parts.Length < 3) return;
            hardwareId = parts[1];
            instanceId = parts[2];
        }

        private static byte[] ReadEdid(string hardwareId, string instanceId)
        {
            if (hardwareId == null || instanceId == null) return null;
            string path = "SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\" + hardwareId + "\\" + instanceId + "\\Device Parameters";
            try
            {
                // The SYSTEM hive is not WOW64-redirected, so the default view
                // is correct whether this process is 32- or 64-bit.
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) return null;
                    return key.GetValue("EDID") as byte[];
                }
            }
            catch (Exception ex)
            {
                Log.Write("ReadEdid " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Parse the 128-byte EDID base block. Mirrors parseEDID() in the
        /// macOS build's setter.swift so ids match across platforms.
        /// </summary>
        internal static bool ParseEdid(byte[] e, out string id, out string name)
        {
            id = null;
            name = null;
            if (e == null || e.Length < 128) return false;

            // Header must be 00 FF FF FF FF FF FF 00.
            if (e[0] != 0x00 || e[7] != 0x00) return false;
            for (int i = 1; i <= 6; i++) if (e[i] != 0xFF) return false;

            // Bytes 8-9: three 5-bit letters, big-endian, 'A' == 1.
            int manuRaw = (e[8] << 8) | e[9];
            var manu = new StringBuilder(3);
            manu.Append((char)(((manuRaw >> 10) & 0x1F) + 0x40));
            manu.Append((char)(((manuRaw >> 5) & 0x1F) + 0x40));
            manu.Append((char)((manuRaw & 0x1F) + 0x40));

            int product = (e[11] << 8) | e[10];
            uint serial = (uint)((e[15] << 24) | (e[14] << 16) | (e[13] << 8) | e[12]);

            // Descriptor blocks at 54/72/90/108; tag 0xFC carries the model name.
            for (int off = 54; off <= 108; off += 18)
            {
                if (off + 18 > e.Length) break;
                if (e[off] == 0 && e[off + 1] == 0 && e[off + 2] == 0 && e[off + 3] == 0xFC)
                {
                    var sb = new StringBuilder(13);
                    for (int i = off + 5; i < off + 18; i++)
                    {
                        if (e[i] == 0x0A || e[i] == 0x00) break;
                        sb.Append((char)e[i]);
                    }
                    string trimmed = sb.ToString().Trim();
                    if (trimmed.Length > 0) { name = trimmed; break; }
                }
            }

            id = string.Format(CultureInfo.InvariantCulture, "{0}-{1:X4}-{2:X8}", manu, product, serial);
            return true;
        }

        /// <summary>
        /// Two identical monitors produce identical EDIDs (same model, serial
        /// often 0). Fall back to the GPU port UID for those so the ids stay
        /// distinct — port-stable rather than cable-stable, but unambiguous.
        /// </summary>
        private static void DisambiguateIds(List<DisplayInfo> list)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DisplayInfo d in list)
            {
                int n;
                counts.TryGetValue(d.Id, out n);
                counts[d.Id] = n + 1;
            }

            foreach (DisplayInfo d in list)
            {
                if (counts[d.Id] <= 1) continue;
                string hardwareId, instanceId;
                GetDeviceIds(d.DeviceName, out hardwareId, out instanceId);
                string uid = ExtractUid(instanceId);
                if (uid != null) d.Id = d.Id + "-" + uid;
                else d.Id = d.Id + "-" + d.DeviceName.Replace("\\", "").Replace(".", "");
            }
        }

        private static string ExtractUid(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            int i = instanceId.IndexOf("UID", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? instanceId.Substring(i) : null;
        }

        private static string FallbackName(string deviceName)
        {
            // \\.\DISPLAY2 -> "Display 2"
            string digits = "";
            for (int i = deviceName.Length - 1; i >= 0; i--)
            {
                if (char.IsDigit(deviceName[i])) digits = deviceName[i] + digits;
                else if (digits.Length > 0) break;
            }
            return digits.Length > 0 ? "Display " + digits : "External Display";
        }

        /// <summary>Internal panel first, then left-to-right by screen position.</summary>
        private static void SortForDisplay(List<DisplayInfo> list)
        {
            list.Sort(delegate(DisplayInfo a, DisplayInfo b)
            {
                if (a.IsInternal != b.IsInternal) return a.IsInternal ? -1 : 1;
                int c = a.Bounds.left.CompareTo(b.Bounds.left);
                if (c != 0) return c;
                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Acquire the DDC/CI handle for a monitor.
        ///
        /// The success of this call is reported by the return value, never by
        /// testing the handle against zero: dxva2 hands back an opaque value
        /// that is genuinely 0 on some machines and works perfectly.
        /// </summary>
        private static bool OpenPhysical(IntPtr hmon, out IntPtr handle, out Native.PHYSICAL_MONITOR[] arr)
        {
            handle = IntPtr.Zero;
            arr = null;
            try
            {
                uint count = 0;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, ref count))
                {
                    Log.Write("GetNumberOfPhysicalMonitorsFromHMONITOR failed err=0x" +
                              System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString("X8"));
                    return false;
                }
                if (count == 0)
                {
                    Log.Write("GetNumberOfPhysicalMonitorsFromHMONITOR reported 0 monitors");
                    return false;
                }

                var monitors = new Native.PHYSICAL_MONITOR[count];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, count, monitors))
                {
                    Log.Write("GetPhysicalMonitorsFromHMONITOR failed err=0x" +
                              System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString("X8") +
                              " count=" + count);
                    return false;
                }

                arr = monitors;
                handle = monitors[0].hPhysicalMonitor;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("OpenPhysical: " + ex.Message);
                return false;
            }
        }
    }
}
