using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Audio;
using Lumen.Core;
using Lumen.Engine;
using ScreenRecorderLib;

namespace Lumen;

public sealed class SourceItem
{
    public required string Title { get; init; }
    public string Meta { get; init; } = "";
    public IntPtr Handle { get; init; }
    public string? Device { get; init; }
}

sealed record Target(string Name, EngineSpec Spec, Native.RECT Monitor, int Width, int Height);

public partial class MainWindow : Window
{
    const int HotkeyRecord = 1, HotkeyPause = 2;

    readonly AppSettings _s = AppSettings.Load();
    readonly MicMeter _meter = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch _clock = new();
    readonly Dictionary<int, string> _processNames = new();

    List<RecordableAudioCaptureDevice> _mics = new();
    List<RecordableCamera> _cams = new();
    WindowThumbnail? _thumb;
    IntPtr _hwnd;
    bool _ready, _suppressSelection, _starting;
    int _ticks;

    const int StopTimeoutMs = 20000;
    // A static slide still yields about one captured frame per second, so 6 s of none means a stall.
    static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(6);
    // Frames piling up behind a stuck encoder show up as memory growth long before anything else.
    // Normal use depends on resolution (about 0.4–1.2 GB), so compare with the first 10 s instead.
    const int StallGrowthMb = 800;
    static readonly TimeSpan BaselineWindow = TimeSpan.FromSeconds(10);

    EngineClient? _engine;
    string _engineStatus = "";
    string? _outPath;
    bool _stopping, _killedOnStop, _stalled, _denoiseUnavailable, _pauseHotkey;
    int _lastFrames = -1, _memoryBaseline;
    DateTime _lastFrameAt;
    RecordingBar? _bar;
    AudioTap? _tap;
    bool _processing;
    string? _lastFile;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) => { if (_ready && Mode == "Window") RefreshSources(); };
        Closing += OnClosing;
        _tick.Tick += OnTick;
    }

    string Mode => ModeWindow.IsChecked == true ? "Window" : ModeScreen.IsChecked == true ? "Screen" : "Region";

    // ── Lifecycle ───────────────────────────────────────────────

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SquareCorners(_hwnd);
        _thumb = new WindowThumbnail(_hwnd);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

        if (!Native.RegisterHotKey(_hwnd, HotkeyRecord, Native.MOD_NOREPEAT, Native.VK_F9))
            HotkeyHint.Text = "F9 unavailable";
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        (_s.Mode switch { "Screen" => ModeScreen, "Region" => ModeRegion, _ => ModeWindow }).IsChecked = true;

        _cams = Recorder.GetSystemVideoCaptureDevices();
        CameraPicker.ItemsSource = _cams;
        CameraPicker.SelectedItem = _cams.FirstOrDefault(c => c.DeviceName == _s.Camera) ?? _cams.FirstOrDefault();
        CameraSwitch.IsEnabled = _cams.Count > 0;
        CameraSwitch.IsChecked = _s.CameraOn && _cams.Count > 0;
        ((RadioButton)FindName("Corner" + _s.CameraCorner) ?? CornerBottomRight).IsChecked = true;
        (_s.CameraSize switch { "S" => SizeS, "L" => SizeL, _ => SizeM }).IsChecked = true;

        _mics = Recorder.GetSystemAudioCaptureDevices();
        MicPicker.ItemsSource = _mics;
        MicPicker.SelectedItem = _mics.FirstOrDefault(m => m.DeviceName == _s.Mic)
                                 ?? _mics.FirstOrDefault(m => m.IsDefaultDevice)
                                 ?? _mics.FirstOrDefault();
        GainSlider.Value = _s.MicGain;
        GainText.Text = $"{_s.MicGain * 100:0}%";
        SystemSwitch.IsChecked = _s.SystemAudio;
        DenoiseSwitch.IsChecked = _s.Denoise;

        (_s.Fps == 60 ? Fps60 : Fps30).IsChecked = true;
        (_s.Quality == "Standard" ? QualityStd : QualityHigh).IsChecked = true;
        CountdownSwitch.IsChecked = _s.Countdown;
        CursorSwitch.IsChecked = _s.Cursor;
        OutDirText.Text = _s.OutputDir;
        OutDirText.ToolTip = _s.OutputDir;

        _ready = true;
        AudioTap.CleanStale();
        AudioFinisher.Debug = Log;
        UpdateCameraPanel();
        ApplyMode();
        StateChanged += (_, _) => UpdateMonitoring();
        IsVisibleChanged += (_, _) => UpdateMonitoring();
        UpdateMonitoring();
    }

    /// <summary>
    /// The level meter keeps the microphone open, which Windows shows as "in use". Hold it only
    /// while the window is on screen or a recording is running, and idle the 30 Hz timer otherwise.
    /// </summary>
    void UpdateMonitoring()
    {
        bool wanted = _engine is not null || (IsVisible && WindowState != WindowState.Minimized);
        if (wanted && !_meter.IsOpen) OpenMeter();
        else if (!wanted && _meter.IsOpen) _meter.Close();
        if (wanted) _tick.Start();
        else _tick.Stop();
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_processing)
        {
            e.Cancel = true;
            return;
        }
        _s.Save();
        _tick.Stop();
        // With stdin closed the recorder process finishes the file on its own.
        _engine?.Send("stop");
        _engine?.Dispose();
        _tap?.Stop();
        _tap?.Delete();
        Native.UnregisterHotKey(_hwnd, HotkeyRecord);
        SetPauseHotkey(false);
        _thumb?.Dispose();
        _meter.Dispose();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            if (wParam.ToInt32() == HotkeyRecord) ToggleRecord();
            else if (wParam.ToInt32() == HotkeyPause) TogglePause();
            handled = true;
        }
        return IntPtr.Zero;
    }

    void OnTick(object? sender, EventArgs e)
    {
        double level = _meter.Read(_s.MicGain);
        MicLevel.Level = level;
        _bar?.Update(level, _clock.Elapsed);
        if (++_ticks % 15 == 0) _thumb?.Refresh();
    }

    // ── Sources & preview ───────────────────────────────────────

    void ApplyMode()
    {
        bool region = Mode == "Region";
        ListArea.Visibility = region ? Visibility.Collapsed : Visibility.Visible;
        RegionArea.Visibility = region ? Visibility.Visible : Visibility.Collapsed;
        RefreshSources();
        UpdatePreview();
    }

    void RefreshSources()
    {
        if (Mode == "Region") return;

        var current = SourceList.SelectedItem as SourceItem;
        var items = new List<SourceItem>();
        SourceItem? keep;

        if (Mode == "Window")
        {
            int self = Environment.ProcessId;
            foreach (var w in Recorder.GetWindows())
            {
                if (w.Pid == self || string.IsNullOrWhiteSpace(w.Title) || !w.IsValidWindow()) continue;
                string proc = ProcessName(w.Pid);
                items.Add(new SourceItem
                {
                    Title = w.Title,
                    Meta = w.IsMinmimized() ? $"{proc} · minimized" : proc,
                    Handle = w.Handle,
                });
            }
            ListTitle.Text = $"Windows  {items.Count}";
            keep = items.FirstOrDefault(i => current is not null && i.Handle == current.Handle)
                   ?? items.FirstOrDefault(i => current is null && i.Title == _s.WindowTitle);
        }
        else
        {
            int n = 1;
            foreach (var d in Recorder.GetDisplays())
            {
                var r = Native.MonitorByDevice(d.DeviceName);
                items.Add(new SourceItem
                {
                    Title = string.IsNullOrWhiteSpace(d.FriendlyName) ? $"Display {n}" : $"Display {n}  {d.FriendlyName}",
                    Meta = r is { } m ? $"{m.Width} × {m.Height}" : "",
                    Device = d.DeviceName,
                });
                n++;
            }
            ListTitle.Text = $"Displays  {items.Count}";
            keep = items.FirstOrDefault(i => i.Device == (current?.Device ?? _s.Display)) ?? items.FirstOrDefault();
        }

        // Refreshing on every activation must not shift rows under the cursor, so keep the list when nothing changed.
        if (SourceList.ItemsSource is List<SourceItem> old && old.Count == items.Count &&
            old.Zip(items).All(p => p.First.Handle == p.Second.Handle && p.First.Device == p.Second.Device &&
                                    p.First.Title == p.Second.Title && p.First.Meta == p.Second.Meta))
            return;

        _suppressSelection = true;
        SourceList.ItemsSource = items;
        SourceList.SelectedItem = keep;
        _suppressSelection = false;
        UpdatePreview();
    }

    string ProcessName(int? pid)
    {
        if (pid is not { } id) return "";
        if (_processNames.TryGetValue(id, out var name)) return name;
        try { name = Process.GetProcessById(id).ProcessName; } catch { name = ""; }
        if (_processNames.Count > 512) _processNames.Clear();
        return _processNames[id] = name;
    }

    void UpdatePreview()
    {
        _thumb?.Detach();
        switch (Mode)
        {
            case "Window":
                if (SourceList.SelectedItem is SourceItem w)
                {
                    SetPreviewText("", "");
                    _thumb?.Attach(w.Handle);
                    FitThumb();
                }
                else SetPreviewText("", "No window selected");
                break;

            case "Screen":
                if (SourceList.SelectedItem is SourceItem d) SetPreviewText(d.Meta, d.Title);
                else SetPreviewText("", "No display selected");
                break;

            default:
                if (_s.Region is { Length: 4 } r && _s.RegionDisplay is not null)
                    SetPreviewText($"{r[2]} × {r[3]}", $"Origin  {r[0]}, {r[1]}");
                else SetPreviewText("", "No region selected");
                break;
        }
    }

    void SetPreviewText(string main, string sub)
    {
        PreviewMain.Text = main;
        PreviewMain.Visibility = main.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreviewSub.Text = sub;
        PreviewSub.Visibility = sub.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void FitThumb()
    {
        if (_thumb is not { IsAttached: true } || !PreviewWell.IsLoaded) return;
        const double pad = 10;
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = PreviewWell.TransformToAncestor(this).Transform(new Point(0, 0));
        _thumb.Fit(new Native.RECT
        {
            Left = (int)Math.Round((p.X + pad) * dpi.DpiScaleX),
            Top = (int)Math.Round((p.Y + pad) * dpi.DpiScaleY),
            Right = (int)Math.Round((p.X + PreviewWell.ActualWidth - pad) * dpi.DpiScaleX),
            Bottom = (int)Math.Round((p.Y + PreviewWell.ActualHeight - pad) * dpi.DpiScaleY),
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(FitThumb, DispatcherPriority.Render);
    }

    // ── Recording ───────────────────────────────────────────────

    void ToggleRecord()
    {
        if (_engine is not null) StopRecording();
        else if (!_starting && !_processing) StartRecording();
    }

    void TogglePause()
    {
        if (_engine is null || _stopping) return;
        if (_engineStatus == "Recording")
        {
            _engine.Send("pause");
            if (_tap is not null) _tap.Paused = true;
            _clock.Stop();
        }
        else if (_engineStatus == "Paused")
        {
            _engine.Send("resume");
            if (_tap is not null) _tap.Paused = false;
            _clock.Start();
        }
    }

    async void StartRecording()
    {
        var target = ResolveTarget(out var error);
        if (target is null)
        {
            ShowNotice(error);
            if (!IsVisible) { Show(); Activate(); }
            return;
        }

        if (FreeSpace(_s.OutputDir) is { } free && free < MinFreeBytes)
        {
            ShowNotice($"Low disk space  {free / (double)(1L << 30):0.0} GB free");
            if (!IsVisible) { Show(); Activate(); }
            return;
        }

        _starting = true;
        _s.Save();
        EngineClient? engine = null;
        try
        {
            Directory.CreateDirectory(_s.OutputDir);
            _outPath = OutputPath(target.Name);
            var spec = BuildSpec(target, _outPath);

            _thumb?.Detach();
            Hide();

            // The recorder warms up (about a second) while the countdown runs.
            var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine = new EngineClient(spec);
            var owner = engine;
            engine.Ready += () => ready.TrySetResult(null);
            engine.StatusChanged += st => Dispatcher.BeginInvoke(() => { if (_engine == owner) OnEngineStatus(st); });
            engine.FramesReported += (f, mb) => Dispatcher.BeginInvoke(() => { if (_engine == owner) OnFrames(f, mb); });
            engine.Ended += err =>
            {
                ready.TrySetResult(err ?? "Recorder exited unexpectedly");
                Dispatcher.BeginInvoke(() => { if (_engine == owner) Finish(err); });
            };
            engine.Start();

            if (_s.Countdown) await Countdown.Run(target.Monitor);
            else await Task.Delay(200);

            var first = await Task.WhenAny(ready.Task, Task.Delay(15000));
            if (first != ready.Task) throw new InvalidOperationException("Recorder did not start");
            if (ready.Task.Result is { } startError) throw new InvalidOperationException(startError);

            _denoiseUnavailable = false;
            if (_s.Denoise && MicPicker.SelectedItem is RecordableAudioCaptureDevice mic)
            {
                // Created on a pool (MTA) thread: WASAPI objects made on the UI thread route every
                // capture call through the UI message loop, and stall whenever the UI is busy.
                string id = mic.DeviceName, name = mic.FriendlyName;
                bool system = _s.SystemAudio;
                float gain = (float)_s.MicGain;
                _tap = await Task.Run(() => AudioTap.Start(id, name, system, gain));
                _denoiseUnavailable = _tap is null;
            }

            _engine = engine;
            // An exit during the warm-up was ignored (it was not ours yet); do not start a dead recorder.
            if (engine.HasEnded) throw new InvalidOperationException("Recorder exited unexpectedly");
            _engineStatus = "";
            _stopping = _killedOnStop = false;
            engine.Send("go");
            SetPauseHotkey(true);
            UpdateMonitoring();
            _clock.Restart();
            _lastFrames = -1;
            _memoryBaseline = 0;
            _stalled = false;
            _lastFrameAt = DateTime.UtcNow;

            _bar = new RecordingBar();
            _bar.PauseRequested += TogglePause;
            _bar.StopRequested += StopRecording;
            _bar.ShowOn(target.Monitor);
        }
        catch (Exception ex)
        {
            _engine = null;
            SetPauseHotkey(false);
            if (engine is not null)
            {
                engine.Send("cancel");
                _ = Task.Run(() => { engine.Kill(); engine.Dispose(); });
            }
            var tap = _tap;
            _tap = null;
            if (tap is not null) _ = Task.Run(() => { tap.Stop(); tap.Delete(); });
            Show();
            UpdatePreview();
            ShowNotice("Could not start: " + ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    void OnEngineStatus(string status)
    {
        _engineStatus = status;
        if (Enum.TryParse<RecorderStatus>(status, out var st)) _bar?.SetStatus(st);
        // Frames do not advance while paused; restart the stall clock from here.
        _lastFrameAt = DateTime.UtcNow;
        _bar?.SetStalled(false);
    }

    void OnFrames(int frames, int memoryMb)
    {
        if (frames != _lastFrames)
        {
            _lastFrames = frames;
            _lastFrameAt = DateTime.UtcNow;
        }
        if (_clock.Elapsed < BaselineWindow) _memoryBaseline = Math.Max(_memoryBaseline, memoryMb);
        bool frozen = _engineStatus == "Recording" && DateTime.UtcNow - _lastFrameAt > StallAfter;
        bool piling = _memoryBaseline > 0 && memoryMb > _memoryBaseline + StallGrowthMb;
        bool stalled = !_stopping && (frozen || piling);
        if (stalled && !_stalled)
            Log($"stall warning at {_clock.Elapsed:hh\\:mm\\:ss}: frames {frames} (unchanged {(DateTime.UtcNow - _lastFrameAt).TotalSeconds:0.0}s), memory {memoryMb} MB, baseline {_memoryBaseline} MB");
        _stalled = stalled;
        _bar?.SetStalled(stalled);
    }

    async void StopRecording()
    {
        if (_engine is null || _stopping) return;
        var engine = _engine;
        _stopping = true;
        _clock.Stop();
        _bar?.SetFinishing();
        engine.Send("stop");

        // A healthy recorder finishes within a second or two. One that does not is stuck in the
        // native encoder; its fragmented MP4 is still readable once the process is gone.
        await Task.Delay(StopTimeoutMs);
        if (_engine == engine)
        {
            _killedOnStop = true;
            await Task.Run(engine.Kill);
        }
    }

    async void Finish(string? error)
    {
        var duration = _clock.Elapsed;
        _clock.Reset();
        _bar?.Close();
        _bar = null;
        _engine?.Dispose();
        _engine = null;
        _engineStatus = "";
        _stopping = false;
        SetPauseHotkey(false);
        var tap = _tap;
        _tap = null;

        Show();
        Activate();
        UpdatePreview();
        UpdateMonitoring();
        if (tap is not null) await Task.Run(tap.Stop);

        string? path = _outPath;
        if (error is not null) Log($"recording ended with: {error}{(_killedOnStop ? " (killed after stop timeout)" : "")}  {path}");

        if (path is null || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            tap?.Delete();
            ShowNotice("Recording failed: " + (error ?? "no output file"));
            return;
        }

        // A recorder that died or hung still leaves everything up to its last fragment on disk.
        string suffix = error is null ? "" : _killedOnStop ? "  ·  Recovered" : "  ·  Partial";
        if (_denoiseUnavailable) suffix += "  ·  Not denoised";
        if (tap is not null)
        {
            _processing = true;
            RecordBtn.IsEnabled = false;
            ShowProgress("Processing audio  0%");
            var progress = new Progress<double>(p => ShowProgress($"Processing audio  {p * 100:0}%"));
            try
            {
                // A salvaged file is shorter than the clock by design, so only check normal finishes.
                var expected = error is null ? duration : TimeSpan.Zero;
                await Task.Run(() => AudioFinisher.Run(path, tap, expected, progress));
            }
            catch (Exception ex)
            {
                // The untouched original stays in place; record why for later.
                suffix += "  ·  Not denoised";
                Log($"denoise failed for {path}\n{ex}");
            }
            finally
            {
                tap.Delete();
                _processing = false;
                RecordBtn.IsEnabled = true;
            }
        }

        _lastFile = path;
        long bytes = new FileInfo(path).Length;
        string size = bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
                    : bytes >= 10L << 20 ? $"{bytes / (double)(1 << 20):0} MB"
                    : $"{bytes / (double)(1 << 20):0.0} MB";
        LastText.Text = $"{Path.GetFileName(path)}  ·  {duration:hh\\:mm\\:ss}  ·  {size}{suffix}";
        LastText.ToolTip = path;
        Notice.Visibility = Visibility.Collapsed;
        LastPanel.Visibility = Visibility.Visible;
    }

    static void Log(string message) => Core.Log.Write(message);

    const long MinFreeBytes = 2L << 30;

    static long? FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return null; }   // network paths and the like: do not block on what we cannot measure
    }

    void SetPauseHotkey(bool on)
    {
        if (on == _pauseHotkey) return;
        _pauseHotkey = on && Native.RegisterHotKey(_hwnd, HotkeyPause, Native.MOD_NOREPEAT, Native.VK_F10);
        if (!on) Native.UnregisterHotKey(_hwnd, HotkeyPause);
    }

    Target? ResolveTarget(out string error)
    {
        error = "";
        switch (Mode)
        {
            case "Window":
            {
                if (SourceList.SelectedItem is not SourceItem w || !Native.IsWindow(w.Handle))
                {
                    error = "Select a window";
                    return null;
                }
                if (Native.IsIconic(w.Handle))
                {
                    error = "Window is minimized";
                    return null;
                }
                var b = Native.WindowBounds(w.Handle);
                var spec = new EngineSpec { Kind = "Window", Window = w.Handle.ToInt64() };
                return new Target(w.Title, spec, Native.MonitorOfWindow(w.Handle), b.Width, b.Height);
            }
            case "Screen":
            {
                if (SourceList.SelectedItem is not SourceItem d || d.Device is null || Native.MonitorByDevice(d.Device) is not { } m)
                {
                    error = "Select a display";
                    return null;
                }
                var spec = new EngineSpec { Kind = "Display", Display = d.Device };
                return new Target("Display", spec, m, m.Width, m.Height);
            }
            default:
            {
                if (_s.Region is not { Length: 4 } r || _s.RegionDisplay is null || Native.MonitorByDevice(_s.RegionDisplay) is not { } m)
                {
                    error = "Select a region";
                    return null;
                }
                if (r[0] + r[2] > m.Width || r[1] + r[3] > m.Height)
                {
                    error = "Region is outside the display";
                    return null;
                }
                var spec = new EngineSpec { Kind = "Display", Display = _s.RegionDisplay, Rect = r };
                return new Target("Region", spec, m, r[2], r[3]);
            }
        }
    }

    EngineSpec BuildSpec(Target target, string path)
    {
        var spec = target.Spec;
        spec.Path = path;
        spec.LogPath = Path.Combine(AppSettings.Dir, "recorder.log");
        spec.Fps = _s.Fps;
        spec.Cursor = _s.Cursor;
        spec.Quality = _s.Quality == "Standard" ? 70 : 90;

        if (MicPicker.SelectedItem is RecordableAudioCaptureDevice mic)
        {
            spec.Mic = mic.DeviceName;
            spec.MicGain = _s.MicGain;
        }
        if (_s.SystemAudio)
        {
            var output = Recorder.GetSystemAudioLoopbackDevices();
            spec.Loopback = (output.FirstOrDefault(d => d.IsDefaultDevice) ?? output.FirstOrDefault())?.DeviceName;
        }
        if (_s.CameraOn && CameraPicker.SelectedItem is RecordableCamera cam)
        {
            double share = _s.CameraSize switch { "S" => 0.18, "L" => 0.33, _ => 0.25 };
            spec.Camera = cam.DeviceName;
            spec.CameraAnchor = _s.CameraCorner;
            spec.CameraHeight = (int)Math.Round(target.Height * share) & ~1;
            spec.CameraMargin = (int)Math.Round(target.Height * 0.03);
        }
        return spec;
    }

    string OutputPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length > 40) clean = clean[..40].Trim();
        string stem = $"{DateTime.Now:yyyy-MM-dd HH.mm} {clean}";
        string path = Path.Combine(_s.OutputDir, stem + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(_s.OutputDir, $"{stem} ({i}).mp4");
        return path;
    }

    void ShowNotice(string message)
    {
        Notice.Foreground = (Brush)FindResource("Signal");
        Notice.Text = message;
        Notice.ToolTip = message;
        LastPanel.Visibility = Visibility.Collapsed;
        Notice.Visibility = Visibility.Visible;
    }

    void ShowProgress(string message)
    {
        Notice.Foreground = (Brush)FindResource("Muted");
        Notice.Text = message;
        LastPanel.Visibility = Visibility.Collapsed;
        Notice.Visibility = Visibility.Visible;
    }

    void OpenMeter()
    {
        var mic = MicPicker.SelectedItem as RecordableAudioCaptureDevice;
        _meter.Open(mic?.DeviceName, mic?.FriendlyName);
    }

    void UpdateCameraPanel()
    {
        bool on = CameraSwitch.IsChecked == true;
        CameraPanel.IsEnabled = on;
        CameraPanel.Opacity = on ? 1 : 0.4;
    }

    // ── UI events ───────────────────────────────────────────────

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Mode = Mode;
        Notice.Visibility = Visibility.Collapsed;
        ApplyMode();
    }

    void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressSelection) return;
        if (SourceList.SelectedItem is SourceItem item)
        {
            if (Mode == "Window") _s.WindowTitle = item.Title;
            else _s.Display = item.Device;
        }
        Notice.Visibility = Visibility.Collapsed;
        UpdatePreview();
    }

    void PreviewWell_SizeChanged(object sender, SizeChangedEventArgs e) => FitThumb();

    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();

    async void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await Task.Delay(150);
        var picked = RegionSelector.Pick();
        Show();
        Activate();
        if (picked is { } p)
        {
            _s.RegionDisplay = p.Device;
            _s.Region = p.Rect;
            _s.Save();
            Notice.Visibility = Visibility.Collapsed;
        }
        UpdatePreview();
    }

    void Camera_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.CameraOn = CameraSwitch.IsChecked == true;
        UpdateCameraPanel();
    }

    void CameraPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && CameraPicker.SelectedItem is RecordableCamera c) _s.Camera = c.DeviceName;
    }

    void Corner_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.CameraCorner = (string)((FrameworkElement)sender).Tag;
    }

    void CamSize_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.CameraSize = (string)((FrameworkElement)sender).Tag;
    }

    void MicPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (MicPicker.SelectedItem is RecordableAudioCaptureDevice m) _s.Mic = m.DeviceName;
        if (_meter.IsOpen) OpenMeter();
    }

    void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _s.MicGain = Math.Round(e.NewValue, 2);
        GainText.Text = $"{_s.MicGain * 100:0}%";
    }

    void System_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.SystemAudio = SystemSwitch.IsChecked == true;
    }

    void Denoise_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.Denoise = DenoiseSwitch.IsChecked == true;
    }

    void Fps_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.Fps = int.Parse((string)((FrameworkElement)sender).Tag);
    }

    void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.Quality = (string)((FrameworkElement)sender).Tag;
    }

    void Countdown_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.Countdown = CountdownSwitch.IsChecked == true;
    }

    void Cursor_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) _s.Cursor = CursorSwitch.IsChecked == true;
    }

    void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _s.OutputDir, Title = "Output Folder" };
        if (dlg.ShowDialog(this) != true) return;
        _s.OutputDir = dlg.FolderName;
        OutDirText.Text = _s.OutputDir;
        OutDirText.ToolTip = _s.OutputDir;
        _s.Save();
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile is not null && File.Exists(_lastFile))
            Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true });
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile is null) return;
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(explorer, $"/select,\"{_lastFile}\"");
    }

    void Record_Click(object sender, RoutedEventArgs e) => ToggleRecord();
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
