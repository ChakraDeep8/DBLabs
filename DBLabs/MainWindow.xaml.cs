using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DBLabs
{
    /// <summary>
    /// The whole application: a title bar and the labelling workspace. Everything else the tool
    /// does — collecting from a stream, managing labels, exporting crops — lives inside that view
    /// or in windows it opens.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            StateChanged += (_, _) => UpdateRestoreGlyph();
            UpdateRestoreGlyph();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdateRestoreGlyph()
        {
            RestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
            RestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        // =====================================================================
        // Maximize sizing
        // =====================================================================

        /// <summary>
        /// WindowStyle="None" windows are not clamped to the monitor's work area the way
        /// normal-chrome ones are, so maximizing can size past the screen edge or under the
        /// taskbar, and DPI shifts it further. Handling WM_GETMINMAXINFO and sizing from the
        /// correct monitor's real work area is the fix that holds up on multi-monitor setups.
        /// </summary>
        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(MaximizeBoundsHook);
        }

        private IntPtr MaximizeBoundsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                ClampToMonitorWorkArea(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ClampToMonitorWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return;

            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

            var work = info.rcWork;
            var bounds = info.rcMonitor;
            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);

            // Relative to the monitor's own top-left, not the virtual desktop.
            mmi.ptMaxPosition.X = work.Left - bounds.Left;
            mmi.ptMaxPosition.Y = work.Top - bounds.Top;
            mmi.ptMaxSize.X = work.Right - work.Left;
            mmi.ptMaxSize.Y = work.Bottom - work.Top;
            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public int dwFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MINMAXINFO
            {
                public POINT ptReserved;
                public POINT ptMaxSize;
                public POINT ptMaxPosition;
                public POINT ptMinTrackSize;
                public POINT ptMaxTrackSize;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        }
    }
}
