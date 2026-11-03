using ScreenRecorderLib;

namespace Lumen.Engine;

/// <summary>Everything the recorder process needs, as plain data so it can cross the process boundary.</summary>
public sealed class EngineSpec
{
    public string Path { get; set; } = "";
    public string? LogPath { get; set; }

    public string Kind { get; set; } = "Window";      // Window | Display
    public long Window { get; set; }
    public string? Display { get; set; }
    public int[]? Rect { get; set; }                  // crop inside the display, physical px

    public string? Mic { get; set; }
    public double MicGain { get; set; } = 1;
    public string? Loopback { get; set; }

    public string? Camera { get; set; }
    public string CameraAnchor { get; set; } = "BottomRight";
    public int CameraHeight { get; set; }
    public int CameraMargin { get; set; }

    public bool Cursor { get; set; } = true;
    public int Fps { get; set; } = 30;
    public int Quality { get; set; } = 90;

    public RecorderOptions ToOptions()
    {
        RecordingSourceBase source = Kind == "Window"
            ? new WindowRecordingSource(new IntPtr(Window)) { IsBorderRequired = false, IsCursorCaptureEnabled = Cursor }
            : new DisplayRecordingSource(Display)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsBorderRequired = false,
                IsCursorCaptureEnabled = Cursor,
                SourceRect = Rect is { Length: 4 } r ? new ScreenRect(r[0], r[1], r[2], r[3]) : null,
            };

        var audio = new List<AudioSourceBase>();
        if (Mic is not null) audio.Add(new CaptureAudioSource(Mic) { Volume = (float)MicGain, ForceMono = true });
        if (Loopback is not null) audio.Add(new LoopbackAudioSource(Loopback));

        var overlays = new List<RecordingOverlayBase>();
        if (Camera is not null)
        {
            overlays.Add(new VideoCaptureOverlay(Camera)
            {
                AnchorPoint = Enum.TryParse<Anchor>(CameraAnchor, out var a) ? a : Anchor.BottomRight,
                Size = new ScreenSize(0, CameraHeight),
                Offset = new ScreenSize(CameraMargin, CameraMargin),
                Stretch = StretchMode.Uniform,
            });
        }

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { source } },
            OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, Stretch = StretchMode.Uniform },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audio.Count > 0,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = audio,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    EncoderProfile = H264Profile.High,
                },
                Quality = Quality,
                Framerate = Fps,
                // Fixed framerate re-encodes duplicate frames of a static page; with the hardware encoder
                // on this machine those backed up in memory until the pipeline stalled (SumatraPDF, ~16–60 s in).
                // Variable framerate keeps memory flat and the encoder idle while nothing changes.
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true,
                // Fragmented MP4 stays playable even if this process has to be killed.
                IsFragmentedMp4Enabled = true,
                IsLowLatencyEnabled = false,
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = Cursor },
            OverlayOptions = new OverLayOptions { Overlays = overlays },
            LogOptions = new LogOptions
            {
                IsLogEnabled = LogPath is not null,
                LogSeverityLevel = LogLevel.Warn,
                LogFilePath = LogPath,
            },
        };
    }
}
