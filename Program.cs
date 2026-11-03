using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumen.Core;
using Lumen.Engine;

namespace Lumen;

public static class Program
{
    const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;
    [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(uint flags);

    // No [STAThread]: the recorder process runs on an MTA thread; the UI gets its own STA thread.
    public static int Main(string[] args)
    {
        // Native DLLs (rnnoise, the recorder's runtime) load only from the app folder and System32,
        // never from the current directory or PATH, so a planted DLL cannot be picked up.
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

        if (args.Length >= 2 && args[0] == "--engine") return EngineHost.Run(args[1]);

        // One instance: two would fight over the hotkeys, the microphone and the output file names.
        using var single = new Mutex(true, @"Local\Lumen.UI", out bool first);
        if (!first)
        {
            ActivateExisting();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"unobserved task: {e.Exception}");
            e.SetObserved();
        };

        int code = 0;
        var ui = new Thread(() =>
        {
            var app = new App();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // A recording lives in its own process, so keep the UI alive and leave a trace.
                Log.Write($"ui: {e.Exception}");
                e.Handled = true;
            };
            app.InitializeComponent();
            code = app.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return code;
    }

    static void ActivateExisting()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            using (p)
            {
                if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                Native.Restore(p.MainWindowHandle);
                return;
            }
        }
    }
}
