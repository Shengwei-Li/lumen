using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lumen.Engine;

/// <summary>UI-side handle on the recorder process. Events arrive on a background thread.</summary>
sealed class EngineClient : IDisposable
{
    readonly Process _process;
    int _ended;

    public event Action? Ready;
    public event Action<string>? StatusChanged;
    /// <summary>Frame counter and the recorder process memory, twice a second.</summary>
    public event Action<int, int>? FramesReported;
    /// <summary>Raised exactly once: error is null on a clean finish.</summary>
    public event Action<string?>? Ended;

    public EngineClient(EngineSpec spec)
    {
        string arg = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spec)));
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--engine {arg}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.Exited += (_, _) =>
        {
            // Drain stdout first so a final "complete" line wins over the exit.
            try { _process.WaitForExit(); } catch { }
            End("Recorder exited unexpectedly");
        };
    }

    /// <summary>Launches the process; subscribe to events before calling.</summary>
    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
    }

    void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        EngineEvent? e;
        try { e = JsonSerializer.Deserialize<EngineEvent>(line); } catch { return; }
        switch (e?.Evt)
        {
            case "ready": Ready?.Invoke(); break;
            case "status": StatusChanged?.Invoke(e.Status ?? ""); break;
            case "frames": FramesReported?.Invoke(e.Frames, e.MemoryMb); break;
            case "complete": End(null); break;
            case "failed": End(e.Error ?? "Recording failed"); break;
        }
    }

    public bool HasEnded => Volatile.Read(ref _ended) == 1;

    void End(string? error)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0) Ended?.Invoke(error);
    }

    public void Send(string command)
    {
        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch { }
    }

    /// <summary>Last resort for a recorder that will not finish. The fragmented MP4 on disk stays playable.</summary>
    public void Kill()
    {
        try { _process.Kill(true); } catch { }
        try { _process.WaitForExit(3000); } catch { }
    }

    public void Dispose() => _process.Dispose();
}
