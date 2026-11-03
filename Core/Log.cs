using System.IO;

namespace Lumen.Core;

/// <summary>Append-only diagnostics in %APPDATA%\Lumen\lumen.log. Never throws.</summary>
static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 1 << 20;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Dir);
                string path = Path.Combine(AppSettings.Dir, "lumen.log");
                // Keep one previous generation instead of growing without bound.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTime.Now:s} {message}\n");
            }
        }
        catch { }
    }
}
