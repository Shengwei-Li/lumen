using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Lumen.Core;
using ScreenRecorderLib;

namespace Lumen;

/// <summary>Floating control strip shown while recording. Invisible to every screen capture, ours included.</summary>
public partial class RecordingBar : Window
{
    public event Action? PauseRequested;
    public event Action? StopRequested;

    bool _finishing;

    public RecordingBar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SquareCorners(hwnd);
            Recorder.SetExcludeFromCapture(hwnd, true);
        };
        Root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    internal void ShowOn(Native.RECT monitor)
    {
        Show();
        Native.Place(this, monitor, 20);
    }

    public void Update(double level, TimeSpan elapsed)
    {
        if (_finishing) return;
        Meter.Level = level;
        Time.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    public void SetStatus(RecorderStatus status)
    {
        bool paused = status == RecorderStatus.Paused;
        Dot.Fill = paused ? null : (System.Windows.Media.Brush)FindResource("Signal");
        Dot.Stroke = paused ? (System.Windows.Media.Brush)FindResource("MutedOnInk") : null;
        Time.Foreground = (System.Windows.Media.Brush)FindResource(paused ? "MutedOnInk" : "Paper");
        PauseBtn.Content = paused ? "\uE768" : "\uE769";
        PauseBtn.ToolTip = paused ? "Resume  F10" : "Pause  F10";
    }

    /// <summary>The recorder stopped producing frames: say so now, not after an hour of lecture.</summary>
    public void SetStalled(bool stalled)
    {
        if (_finishing) return;
        StallText.Visibility = stalled ? Visibility.Visible : Visibility.Collapsed;
        Meter.Visibility = stalled ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetFinishing()
    {
        _finishing = true;
        StallText.Visibility = Visibility.Collapsed;
        Meter.Visibility = Visibility.Visible;
        Time.Text = "Saving";
        Meter.Level = 0;
        PauseBtn.IsEnabled = StopBtn.IsEnabled = false;
    }

    void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
}
