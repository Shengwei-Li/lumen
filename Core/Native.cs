using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lumen.Core;

/// <summary>Thin Win32 / DWM surface. All rectangles are physical pixels.</summary>
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_F9 = 0x78, VK_F10 = 0x79;

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    static MONITORINFOEX Info(IntPtr monitor)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(monitor, ref info);
        return info;
    }

    public static RECT MonitorUnderCursor(out string device)
    {
        GetCursorPos(out var pt);
        var info = Info(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
        device = info.szDevice;
        return info.rcMonitor;
    }

    public static RECT MonitorOfWindow(IntPtr hwnd) => Info(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).rcMonitor;

    public static RECT? MonitorByDevice(string device)
    {
        RECT? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var info = Info(m);
            if (string.Equals(info.szDevice, device, StringComparison.OrdinalIgnoreCase)) found = info.rcMonitor;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Visible bounds of a window, without the invisible resize border.</summary>
    public static RECT WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return r;
    }

    public static void Restore(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
    }

    public static void SquareCorners(IntPtr hwnd)
    {
        int pref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>Centers a shown window horizontally on a monitor; top &lt; 0 centers vertically too.</summary>
    public static void Place(Window w, RECT monitor, int top)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        // Twice: moving across monitors with different DPI resizes the window after the first move.
        for (int pass = 0; pass < 2; pass++)
        {
            GetWindowRect(hwnd, out var r);
            int x = monitor.Left + (monitor.Width - r.Width) / 2;
            int y = top < 0 ? monitor.Top + (monitor.Height - r.Height) / 2 : monitor.Top + top;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    public static void Cover(Window w, RECT monitor)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, SWP_NOACTIVATE);
    }
}
