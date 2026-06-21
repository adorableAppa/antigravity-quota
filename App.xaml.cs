using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ModernWpf;

namespace AntigravityQuota
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private const string MutexName = "AntigravityQuota_SingleInstance_Mutex";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int HWND_BROADCAST = 0xffff;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // Send restore message to the already running instance
                uint msg = RegisterWindowMessage("AntigravityQuota_Restore_Message");
                SendNotifyMessage((IntPtr)HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);

                // Exit immediately
                Shutdown();
                return;
            }

            base.OnStartup(e);
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ObjectDisposedException) {}
                catch (ApplicationException) {}
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
