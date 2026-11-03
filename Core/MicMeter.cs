using NAudio.CoreAudioApi;

namespace Lumen.Core;

/// <summary>
/// Keeps a shared-mode stream open on the microphone so its endpoint peak meter
/// is live, and reads it as a 0..1 level on a -60..0 dB scale.
/// </summary>
sealed class MicMeter : IDisposable
{
    MMDevice? _device;
    WasapiCapture? _capture;
    double _level;

    public void Open(string? id, string? friendlyName)
    {
        Close();
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice? device = null;
            if (!string.IsNullOrEmpty(id))
            {
                try { device = en.GetDevice(id); } catch { }
            }
            device ??= en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                         .FirstOrDefault(d => d.FriendlyName == friendlyName);
            device ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

            _device = device;
            _capture = new WasapiCapture(device);
            _capture.StartRecording();
        }
        catch
        {
            Close();
        }
    }

    public double Read(double gain)
    {
        if (_device is null) return 0;
        double peak;
        try { peak = _device.AudioMeterInformation.MasterPeakValue * gain; }
        catch { return 0; }

        double db = peak <= 1e-5 ? -60 : 20 * Math.Log10(peak);
        double target = Math.Clamp((db + 60) / 60, 0, 1);
        // Fast attack, slow release: reads like a hardware meter.
        _level = target > _level ? target : _level * 0.88 + target * 0.12;
        return _level;
    }

    public bool IsOpen => _device is not null;

    /// <summary>Releases the microphone, so Windows stops showing it as in use.</summary>
    public void Close()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
        _device?.Dispose();
        _device = null;
        _level = 0;
    }

    public void Dispose() => Close();
}
