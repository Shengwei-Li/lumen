using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.Core;

namespace Lumen;

/// <summary>Full-monitor overlay for dragging out a capture rectangle on the monitor under the cursor.</summary>
public partial class RegionSelector : Window
{
    const int MinSize = 64;

    readonly Native.RECT _monitor;
    readonly string _device;
    Point _anchor;
    bool _dragging;
    (string Device, int[] Rect)? _result;

    RegionSelector()
    {
        InitializeComponent();
        _monitor = Native.MonitorUnderCursor(out _device);

        SourceInitialized += (_, _) => Native.Cover(this, _monitor);
        ContentRendered += (_, _) =>
        {
            Native.Cover(this, _monitor);
            Activate();
            Focus();
            DrawDim(null);
        };
        SizeChanged += (_, _) => DrawDim(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Root.MouseLeftButtonDown += OnDown;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnUp;
    }

    /// <summary>Returns the display device and [x, y, w, h] in that display's physical pixels.</summary>
    public static (string Device, int[] Rect)? Pick()
    {
        var w = new RegionSelector();
        w.ShowDialog();
        return w._result;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _anchor = e.GetPosition(Root);
        _dragging = true;
        Root.CaptureMouse();
        Hint.Visibility = Visibility.Collapsed;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        DrawSelection(Span(_anchor, e.GetPosition(Root)));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleaseMouseCapture();

        var r = Span(_anchor, e.GetPosition(Root));
        var a = PointToScreen(r.TopLeft);
        var b = PointToScreen(r.BottomRight);
        int x = (int)Math.Round(a.X) - _monitor.Left;
        int y = (int)Math.Round(a.Y) - _monitor.Top;
        int w = (int)Math.Round(b.X - a.X) & ~1;   // encoders want even dimensions
        int h = (int)Math.Round(b.Y - a.Y) & ~1;

        if (w < MinSize || h < MinSize)
        {
            Sel.Visibility = SizeTag.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
            DrawDim(null);
            return;
        }

        _result = (_device, new[] { x, y, w, h });
        Close();
    }

    Rect Span(Point a, Point b)
    {
        bool lockRatio = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (lockRatio)
        {
            double h = Math.Abs(dx) * 9 / 16;
            dy = Math.Sign(dy == 0 ? 1 : dy) * h;
        }
        var end = new Point(Math.Clamp(a.X + dx, 0, Root.ActualWidth), Math.Clamp(a.Y + dy, 0, Root.ActualHeight));
        return new Rect(a, end);
    }

    void DrawSelection(Rect r)
    {
        Canvas.SetLeft(Sel, r.X);
        Canvas.SetTop(Sel, r.Y);
        Sel.Width = r.Width;
        Sel.Height = r.Height;
        Sel.Visibility = Visibility.Visible;
        DrawDim(r);

        var dpi = VisualTreeHelper.GetDpi(this);
        SizeText.Text = $"{Math.Round(r.Width * dpi.DpiScaleX):0} × {Math.Round(r.Height * dpi.DpiScaleY):0}";
        SizeTag.Visibility = Visibility.Visible;
        SizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tagY = r.Bottom + 8 + SizeTag.DesiredSize.Height > Root.ActualHeight ? r.Top - 8 - SizeTag.DesiredSize.Height : r.Bottom + 8;
        Canvas.SetLeft(SizeTag, r.Left);
        Canvas.SetTop(SizeTag, Math.Max(0, tagY));
    }

    void DrawDim(Rect? hole)
    {
        var all = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(all);
        if (hole is { } h) g.Children.Add(new RectangleGeometry(h));
        Dim.Data = g;
    }
}
