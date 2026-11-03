using System.Runtime.InteropServices;

namespace Lumen.Core;

/// <summary>
/// Live DWM mirror of another window, drawn into ours. It shows exactly what
/// Windows.Graphics.Capture will see, including content hidden behind other windows.
/// </summary>
sealed class WindowThumbnail(IntPtr host) : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    const int DESTINATION = 0x1, OPACITY = 0x4, VISIBLE = 0x8, CLIENT_ONLY = 0x10;

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref PROPERTIES props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out Native.SIZE size);

    IntPtr _thumb;
    Native.RECT _area;

    public bool IsAttached => _thumb != IntPtr.Zero;

    public void Attach(IntPtr source)
    {
        Detach();
        if (DwmRegisterThumbnail(host, source, out var t) == 0) _thumb = t;
    }

    public void Detach()
    {
        if (_thumb == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
    }

    /// <summary>Fits the mirror inside <paramref name="area"/> (host pixels), keeping aspect.</summary>
    public void Fit(Native.RECT area)
    {
        _area = area;
        Refresh();
    }

    /// <summary>Re-fits after the source window changes size.</summary>
    public void Refresh()
    {
        if (_thumb == IntPtr.Zero || DwmQueryThumbnailSourceSize(_thumb, out var src) != 0 || src.cx <= 0 || src.cy <= 0)
            return;

        double scale = Math.Min((double)_area.Width / src.cx, (double)_area.Height / src.cy);
        int w = (int)(src.cx * scale), h = (int)(src.cy * scale);
        int x = _area.Left + (_area.Width - w) / 2, y = _area.Top + (_area.Height - h) / 2;

        var props = new PROPERTIES
        {
            dwFlags = DESTINATION | OPACITY | VISIBLE | CLIENT_ONLY,
            rcDestination = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void Dispose() => Detach();
}
