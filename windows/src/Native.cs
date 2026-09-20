//
//  Native.cs
//  MonBright for Windows — every P/Invoke the app makes, in one place.
//
//  Nothing here needs elevation. dxva2.dll is the public Monitor
//  Configuration API; the rest is ordinary user32/dwmapi window plumbing.
//

using System;
using System.Runtime.InteropServices;

namespace MonBright
{
    internal static class Native
    {
        // ---------------------------------------------------------------
        // Geometry
        // ---------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int left, top, right, bottom;
            public int Width { get { return right - left; } }
            public int Height { get { return bottom - top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        internal const uint MONITORINFOF_PRIMARY = 1;
        internal const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        // ---------------------------------------------------------------
        // DDC/CI — the public Monitor Configuration API
        // ---------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetMonitorBrightness(IntPtr h, ref uint min, ref uint cur, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool SetMonitorBrightness(IntPtr h, uint val);

        // Low-level fallback. VCP code 0x10 is Luminance — the same code the
        // macOS build writes over I2C.
        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, ref uint cur, ref uint max);

        internal const byte VCP_LUMINANCE = 0x10;

        // ---------------------------------------------------------------
        // Global hotkeys
        // ---------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_WIN = 0x0008;
        internal const uint MOD_NOREPEAT = 0x4000;

        internal const int WM_HOTKEY = 0x0312;
        internal const int WM_DISPLAYCHANGE = 0x007E;
        internal const int WM_DPICHANGED = 0x02E0;

        // ---------------------------------------------------------------
        // DWM — rounded corners, dark titlebar, backdrop
        // ---------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        internal const int DWMWCP_ROUND = 2;

        // ---------------------------------------------------------------
        // Per-monitor DPI
        // ---------------------------------------------------------------

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        internal const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        /// <summary>Effective DPI of the monitor containing <paramref name="pt"/>, or 96.</summary>
        internal static int DpiAt(POINT pt)
        {
            try
            {
                IntPtr hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx > 0)
                    return (int)dx;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 96;
        }

        // ---------------------------------------------------------------
        // Foreground / focus behaviour for the flyout
        // ---------------------------------------------------------------

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        /// <summary>Required after Bitmap.GetHicon(), which allocates an unmanaged icon.</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Pulls an icon out of a file at an exact pixel size. Unlike
        /// Icon.ExtractAssociatedIcon, which only ever hands back a 32px frame,
        /// this picks the closest frame in the embedded .ico - so a large icon
        /// stays sharp instead of being upscaled from 32px.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int PrivateExtractIcons(
            string file, int index, int cx, int cy,
            IntPtr[] icons, IntPtr[] ids, int count, uint flags);

        // ---- Dim overlay (DimOverlay.cs) ---------------------------------

        internal const int WS_EX_TOPMOST     = 0x00000008;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_TOOLWINDOW  = 0x00000080;
        internal const int WS_EX_LAYERED     = 0x00080000;
        internal const int WS_EX_NOACTIVATE  = 0x08000000;

        /// <summary>Keep a window out of screenshots and screen sharing. Windows 10 2004+.</summary>
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    }
}
