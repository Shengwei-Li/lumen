using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Lumen.Audio;

/// <summary>
/// Runs beside the recorder: captures the microphone, denoises it with RNNoise, mixes in
/// system audio, and writes a 48 kHz stereo track to a temp WAV. It also keeps an 8 kHz
/// copy of the raw (undenoised) mix, used afterwards to line the clean track up with the video.
/// </summary>
sealed class AudioTap
{
    public const int Rate = 48000;
    public const int RefRate = 8000;
    const int Decimate = Rate / RefRate;
    const int LoopCapacity = Rate / 2;   // frames of system audio kept ahead of the mic, at most

    public string MixPath { get; }
    public string RefPath { get; }
    public volatile bool Paused;

    readonly WasapiCapture _mic;
    readonly WasapiLoopbackCapture? _loop;
    readonly Resampler _micRs;
    readonly Resampler? _loopRs;
    readonly Denoiser _denoiser = new();
    readonly WaveFileWriter _mix;
    readonly BinaryWriter _ref;
    readonly float _gain;
    readonly object _gate = new();
    readonly CountdownEvent _stopped;

    float[] _micRaw = Array.Empty<float>(), _mono = Array.Empty<float>(), _loopRaw = Array.Empty<float>(), _stereo = Array.Empty<float>();
    readonly float[] _pending = new float[Denoiser.FrameSize];
    readonly float[] _frame = new float[Denoiser.FrameSize];
    readonly float[] _loopFrame = new float[Denoiser.FrameSize * 2];
    readonly byte[] _out = new byte[Denoiser.FrameSize * 4];
    int _pendingCount;
    bool _closed;

    readonly float[] _ring = new float[LoopCapacity * 2];
    int _ringStart, _ringCount;

    AudioTap(MMDevice mic, bool systemAudio, float gain)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Lumen");
        Directory.CreateDirectory(dir);
        string id = Guid.NewGuid().ToString("N");
        MixPath = Path.Combine(dir, id + ".mix.wav");
        RefPath = Path.Combine(dir, id + ".ref.pcm");
        _gain = gain;

        // Event-driven with a 100 ms buffer: polling a 20 ms buffer dropped 3–11% of the audio
        // under load, so the clean track came out short and the end of the video went silent.
        _mic = new WasapiCapture(mic, true, 100);
        _micRs = new Resampler(_mic.WaveFormat.SampleRate, Rate, 1);
        _mic.DataAvailable += OnMic;

        if (systemAudio)
        {
            _loop = new WasapiLoopbackCapture();
            _loopRs = new Resampler(_loop.WaveFormat.SampleRate, Rate, 2);
            _loop.DataAvailable += OnLoop;
        }

        _stopped = new CountdownEvent(_loop is null ? 1 : 2);
        _mic.RecordingStopped += (_, _) => _stopped.Signal();
        if (_loop is not null) _loop.RecordingStopped += (_, _) => _stopped.Signal();

        _mix = new WaveFileWriter(MixPath, new WaveFormat(Rate, 16, 2));
        _ref = new BinaryWriter(new FileStream(RefPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
    }

    public static AudioTap? Start(string? micId, string? micName, bool systemAudio, float gain)
    {
        AudioTap? tap = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice? mic = null;
            if (!string.IsNullOrEmpty(micId))
            {
                try { mic = en.GetDevice(micId); } catch { }
            }
            mic ??= en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.FriendlyName == micName);
            if (mic is null) return null;

            tap = new AudioTap(mic, systemAudio, gain);
            tap._mic.StartRecording();
            tap._loop?.StartRecording();
            return tap;
        }
        catch
        {
            tap?.Stop();
            tap?.Delete();
            return null;
        }
    }

    void OnMic(object? sender, WaveInEventArgs e)
    {
        if (Paused || e.BytesRecorded == 0) return;
        var fmt = _mic.WaveFormat;
        int frames = Pcm.Decode(e.Buffer, e.BytesRecorded, fmt, ref _micRaw);
        int ch = fmt.Channels;
        if (_mono.Length < frames) _mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += _micRaw[f * ch + c];
            _mono[f] = sum / ch;
        }

        var resampled = _micRs.Process(_mono, frames, out int n);
        lock (_gate)
        {
            if (_closed) return;
            for (int i = 0; i < n; i++)
            {
                _pending[_pendingCount++] = resampled[i];
                if (_pendingCount == Denoiser.FrameSize)
                {
                    ProcessFrame();
                    _pendingCount = 0;
                }
            }
        }
    }

    void ProcessFrame()
    {
        TakeLoop(_loopFrame, Denoiser.FrameSize);

        // The reference mirrors what the recorder itself mixes (raw mic plus system audio), so the
        // alignment still locks when the system audio drowns out the mic, e.g. a film on headphones.
        for (int i = 0; i < Denoiser.FrameSize; i += Decimate)
        {
            float acc = 0;
            for (int k = 0; k < Decimate; k++)
            {
                int j = i + k;
                acc += _pending[j] * _gain + (_loopFrame[2 * j] + _loopFrame[2 * j + 1]) * 0.5f;
            }
            _ref.Write(Pcm.ToShort(acc / Decimate));
        }

        for (int i = 0; i < Denoiser.FrameSize; i++) _frame[i] = _pending[i] * 32768f;
        _denoiser.Process(_frame);

        for (int i = 0; i < Denoiser.FrameSize; i++)
        {
            float voice = _frame[i] / 32768f * _gain;
            short l = Pcm.ToShort(voice + _loopFrame[2 * i]);
            short r = Pcm.ToShort(voice + _loopFrame[2 * i + 1]);
            _out[4 * i] = (byte)l; _out[4 * i + 1] = (byte)(l >> 8);
            _out[4 * i + 2] = (byte)r; _out[4 * i + 3] = (byte)(r >> 8);
        }
        _mix.Write(_out, 0, _out.Length);
    }

    void OnLoop(object? sender, WaveInEventArgs e)
    {
        if (Paused || e.BytesRecorded == 0 || _loop is null || _loopRs is null) return;
        var fmt = _loop.WaveFormat;
        int frames = Pcm.Decode(e.Buffer, e.BytesRecorded, fmt, ref _loopRaw);
        int ch = fmt.Channels;
        if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            _stereo[2 * f] = _loopRaw[f * ch];
            _stereo[2 * f + 1] = ch > 1 ? _loopRaw[f * ch + 1] : _loopRaw[f * ch];
        }

        var resampled = _loopRs.Process(_stereo, frames, out int n);
        lock (_ring)
        {
            for (int i = 0; i < n; i++)
            {
                if (_ringCount == LoopCapacity)
                {
                    // The two devices' clocks drift; drop the oldest rather than fall behind.
                    _ringStart = (_ringStart + 1) % LoopCapacity;
                    _ringCount--;
                }
                int w = (_ringStart + _ringCount) % LoopCapacity;
                _ring[2 * w] = resampled[2 * i];
                _ring[2 * w + 1] = resampled[2 * i + 1];
                _ringCount++;
            }
        }
    }

    void TakeLoop(float[] dst, int frames)
    {
        Array.Clear(dst);
        if (_loop is null) return;
        lock (_ring)
        {
            int n = Math.Min(frames, _ringCount);
            for (int i = 0; i < n; i++)
            {
                int r = (_ringStart + i) % LoopCapacity;
                dst[2 * i] = _ring[2 * r];
                dst[2 * i + 1] = _ring[2 * r + 1];
            }
            _ringStart = (_ringStart + n) % LoopCapacity;
            _ringCount -= n;
        }
    }

    public void Stop()
    {
        try { _mic.StopRecording(); } catch { }
        try { _loop?.StopRecording(); } catch { }
        _stopped.Wait(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _mix.Dispose();
            _ref.Dispose();
        }
        _mic.Dispose();
        _loop?.Dispose();
        _denoiser.Dispose();
    }

    /// <summary>Removes temp tracks left behind by a session that was killed or crashed.</summary>
    public static void CleanStale()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Path.Combine(Path.GetTempPath(), "Lumen")).EnumerateFiles())
                if (DateTime.Now - f.LastWriteTime > TimeSpan.FromMinutes(5))
                    try { f.Delete(); } catch { }
        }
        catch { }
    }

    public void Delete()
    {
        try { File.Delete(MixPath); } catch { }
        try { File.Delete(RefPath); } catch { }
    }
}
