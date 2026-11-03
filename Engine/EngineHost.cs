using System.Text;
using System.Text.Json;
using ScreenRecorderLib;

namespace Lumen.Engine;

/// <summary>
/// The recorder process (<c>Lumen.exe --engine &lt;spec&gt;</c>). Keeping the native recorder out of the UI
/// process means a stalled encoder can be killed without taking Lumen, or the file, down with it.
/// stdin:  go | pause | resume | stop       stdout: one JSON event per line
/// </summary>
static class EngineHost
{
    static readonly object Gate = new();

    public static int Run(string specArg)
    {
        var spec = JsonSerializer.Deserialize<EngineSpec>(Encoding.UTF8.GetString(Convert.FromBase64String(specArg)))!;
        var done = new ManualResetEventSlim();
        Recorder rec;
        try
        {
            rec = Recorder.CreateRecorder(spec.ToOptions());
        }
        catch (Exception ex)
        {
            Send(new EngineEvent { Evt = "failed", Error = ex.Message });
            return 1;
        }

        rec.OnStatusChanged += (_, e) => Send(new EngineEvent { Evt = "status", Status = e.Status.ToString() });
        rec.OnRecordingComplete += (_, e) => { Send(new EngineEvent { Evt = "complete", Path = e.FilePath }); done.Set(); };
        rec.OnRecordingFailed += (_, e) => { Send(new EngineEvent { Evt = "failed", Error = e.Error, Path = e.FilePath }); done.Set(); };
        Send(new EngineEvent { Evt = "ready" });

        bool started = false;
        Timer? frames = null;
        string? line;
        // Recorder calls are made from this one thread; stdin closing (Lumen gone) also stops cleanly.
        while ((line = Console.In.ReadLine()) is not null && !done.IsSet)
        {
            string cmd = line.Trim();
            try
            {
                if (cmd == "go" && !started)
                {
                    started = true;
                    rec.Record(spec.Path);
                    frames = new Timer(_ => Send(new EngineEvent { Evt = "frames", Frames = rec.CurrentFrameNumber, MemoryMb = (int)(Environment.WorkingSet >> 20) }), null, 500, 500);
                }
                else if (cmd == "pause") rec.Pause();
                else if (cmd == "resume") rec.Resume();
                else if (cmd == "stop" && started) { rec.Stop(); break; }
                else if (cmd == "cancel" && !started) break;
            }
            catch (Exception ex)
            {
                Send(new EngineEvent { Evt = "failed", Error = ex.Message, Path = spec.Path });
                return 1;
            }
        }

        if (started)
        {
            if (line is null) rec.Stop();
            // Lumen kills a stuck recorder after 20 s; if Lumen itself is gone, nobody would. Do it ourselves.
            if (!done.Wait(TimeSpan.FromSeconds(60))) Environment.Exit(2);
        }

        // Make sure no frame report is mid-flight against a disposed recorder.
        if (frames is not null)
        {
            using var drained = new ManualResetEvent(false);
            if (frames.Dispose(drained)) drained.WaitOne(1000);
        }
        rec.Dispose();
        return 0;
    }

    static void Send(EngineEvent e)
    {
        lock (Gate)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(e));
            Console.Out.Flush();
        }
    }
}

public sealed class EngineEvent
{
    public string Evt { get; set; } = "";
    public string? Status { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
    public int Frames { get; set; }
    public int MemoryMb { get; set; }
}
