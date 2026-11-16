using System.IO;
using System.Text;

namespace Flashback.Core;

/// <summary>Wall-clock time as seconds since the Unix epoch. Video and audio are both stamped with this clock.</summary>
public static class Clock
{
    public static double UtcNow() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}

public static class AppPaths
{
    public static string ConfigDir { get; private set; } = "";
    public static string LocalDir { get; private set; } = "";
    public static bool IsCustom { get; private set; }

    public static void Init(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            ConfigDir = LocalDir = Path.GetFullPath(overrideDir);
            IsCustom = true;
        }
        else
        {
            ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flashback");
            LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flashback");
        }
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(TempDir);
    }

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    /// <summary>Rolling video buffer. Wiped on every start and exit.</summary>
    public static string BufferDir => Path.Combine(LocalDir, "buffer");
    public static string ThumbsDir => Path.Combine(LocalDir, "thumbs");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string TempDir => Path.Combine(LocalDir, "tmp");

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clips");
}

public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                string file = Path.Combine(AppPaths.LogDir, "app.log");
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = Path.Combine(AppPaths.LogDir, "app.old.log");
                    File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
