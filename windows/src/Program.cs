//
//  Program.cs
//  MonBright for Windows — entry point.
//

using System;
using System.Threading;
using System.Windows.Forms;

namespace MonBright
{
    internal static class Program
    {
        private const string MutexName = "Local\\MonBright.SingleInstance";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew) return;   // already running in this session

                // Must happen before any window exists. The manifest also
                // declares PerMonitorV2; whichever lands first wins and the
                // other call simply fails, which is harmless.
                try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
                catch (Exception) { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // TrayApp's constructor runs before Application.Run installs
                // the WinForms context, and the monitor workers post UI updates
                // through whatever context exists at that moment. Install it
                // explicitly so those posts land on this thread.
                SynchronizationContext.SetSynchronizationContext(
                    new WindowsFormsSynchronizationContext());

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("Unhandled UI exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("Unhandled exception: " + e.ExceptionObject);
                };

                try
                {
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    Log.Write("Fatal: " + ex);
                    throw;
                }
                finally
                {
                    GC.KeepAlive(mutex);
                }
            }
        }
    }
}
