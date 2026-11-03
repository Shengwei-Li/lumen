using System.Windows;
using System.Windows.Interop;
using Lumen.Core;
using ScreenRecorderLib;

namespace Lumen;

public partial class Countdown : Window
{
    Countdown()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SquareCorners(hwnd);
            Recorder.SetExcludeFromCapture(hwnd, true);
        };
    }

    internal static async Task Run(Native.RECT monitor, int seconds = 3)
    {
        var w = new Countdown();
        w.Show();
        Native.Place(w, monitor, -1);
        for (int i = seconds; i > 0; i--)
        {
            w.Num.Text = i.ToString();
            await Task.Delay(1000);
        }
        w.Close();
        // Let DWM remove the window before the first frame is captured.
        await Task.Delay(80);
    }
}
