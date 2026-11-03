using System.IO;
using System.Text.Json;

namespace Lumen.Core;

public sealed class AppSettings
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public string Mode { get; set; } = "Window";
    public string? WindowTitle { get; set; }
    public string? Display { get; set; }
    public string? RegionDisplay { get; set; }
    public int[]? Region { get; set; }

    public bool CameraOn { get; set; }
    public string? Camera { get; set; }
    public string CameraCorner { get; set; } = "BottomRight";
    public string CameraSize { get; set; } = "M";

    public string? Mic { get; set; }
    public double MicGain { get; set; } = 1.0;
    public bool SystemAudio { get; set; }
    public bool Denoise { get; set; } = true;

    public int Fps { get; set; } = 30;
    public string Quality { get; set; } = "High";
    public bool Countdown { get; set; } = true;
    public bool Cursor { get; set; } = true;
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Lumen");

    public static AppSettings Load()
    {
        AppSettings s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"settings unreadable, using defaults: {ex.Message}");
        }
        s.Normalize();
        return s;
    }

    /// <summary>The file is user-editable; clamp everything before it reaches the recorder.</summary>
    void Normalize()
    {
        if (Mode is not ("Window" or "Screen" or "Region")) Mode = "Window";
        if (Fps is not (30 or 60)) Fps = 30;
        if (Quality is not ("Standard" or "High")) Quality = "High";
        if (CameraSize is not ("S" or "M" or "L")) CameraSize = "M";
        if (CameraCorner is not ("TopLeft" or "TopRight" or "BottomLeft" or "BottomRight")) CameraCorner = "BottomRight";
        MicGain = double.IsFinite(MicGain) ? Math.Clamp(MicGain, 0.5, 2.0) : 1.0;
        if (Region is not { Length: 4 } r || r[0] < 0 || r[1] < 0 || r[2] < 64 || r[3] < 64) Region = null;
        if (string.IsNullOrWhiteSpace(OutputDir) || !Path.IsPathFullyQualified(OutputDir)) OutputDir = new AppSettings().OutputDir;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write then swap, so a crash mid-write cannot leave a truncated settings file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex}");
        }
    }
}
