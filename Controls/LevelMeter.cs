using System.Windows;
using System.Windows.Media;

namespace Lumen.Controls;

/// <summary>Segmented level meter. The last segments light in the signal colour: you are clipping.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(LevelMeter),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OnBrushProperty = DependencyProperty.Register(
        nameof(OnBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffBrushProperty = DependencyProperty.Register(
        nameof(OffBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotBrushProperty = DependencyProperty.Register(
        nameof(HotBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }
    public Brush HotBrush { get => (Brush)GetValue(HotBrushProperty); set => SetValue(HotBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        int n = Math.Max(1, Segments);
        const double gap = 2;
        double w = (ActualWidth - gap * (n - 1)) / n;
        if (w <= 0) return;

        int lit = (int)Math.Round(Level * n);
        int hotFrom = (int)Math.Ceiling(n * 0.9);
        for (int i = 0; i < n; i++)
        {
            var brush = i >= lit ? OffBrush : i >= hotFrom ? HotBrush : OnBrush;
            double x = Math.Round(i * (w + gap));
            double right = Math.Round(i * (w + gap) + w);
            dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(1, right - x), ActualHeight));
        }
    }
}
