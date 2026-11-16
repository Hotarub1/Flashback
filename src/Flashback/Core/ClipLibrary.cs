using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback.Core;

/// <summary>One saved clip as shown in the Clips page.</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private string _durationText = "";

    public ClipItem(FileInfo file)
    {
        Path = file.FullName;
        Name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        Modified = file.LastWriteTime;
        Size = file.Length;
        Key = MakeKey(file);
    }

    public string Path { get; }
    public string Name { get; }
    public DateTime Modified { get; }
    public long Size { get; }
    /// <summary>Changes whenever the file's path, size or timestamp changes.</summary>
    public string Key { get; }
    public bool ThumbRequested { get; set; }

    public string Subtitle
    {
        get
        {
            string when = Modified.Date == DateTime.Today ? "Today " + Modified.ToString("t", CultureInfo.CurrentCulture)
                : Modified.Date == DateTime.Today.AddDays(-1) ? "Yesterday " + Modified.ToString("t", CultureInfo.CurrentCulture)
                : Modified.ToString("MMM d, yyyy  ", CultureInfo.CurrentCulture) + Modified.ToString("t", CultureInfo.CurrentCulture);
            return $"{when}  ·  {ClipLibrary.FormatSize(Size)}";
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public string DurationText
    {
        get => _durationText;
        set { _durationText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string MakeKey(FileInfo f)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{f.FullName.ToLowerInvariant()}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"));
        return System.Convert.ToHexString(hash, 0, 10);
    }
}

public static partial class ClipLibrary
{
    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    public static List<FileInfo> Scan(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) return [];
            return dir.GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the clips folder: " + ex.Message);
            return [];
        }
    }

    /// <summary>Removes half-written clips left behind if the app was killed mid-save.</summary>
    public static void RemoveStaleParts(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string f in Directory.GetFiles(folder, "Clip *.mp4.part"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(10)) File.Delete(f);
            }
        }
        catch { }
    }

    /// <summary>Creates (or reuses) the cached thumbnail and duration for a clip. Safe to call off the UI thread.</summary>
    public static (ImageSource? Image, double Seconds) LoadThumb(string ffmpeg, string clipPath, string key, JobObject? job)
    {
        string jpg = Path.Combine(AppPaths.ThumbsDir, key + ".jpg");
        string meta = Path.Combine(AppPaths.ThumbsDir, key + ".txt");
        double seconds = 0;
        try
        {
            if (File.Exists(meta)) double.TryParse(File.ReadAllText(meta), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            if (!File.Exists(jpg) || !File.Exists(meta))
            {
                var (_, err) = Ffmpeg.Run(ffmpeg,
                    ["-hide_banner", "-nostdin", "-y", "-ss", "0.5", "-i", clipPath, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", jpg],
                    30000, job, null, lowPriority: true);
                Match m = DurationRegex().Match(err);
                if (m.Success)
                    seconds = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                              + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                File.WriteAllText(meta, seconds.ToString("F3", CultureInfo.InvariantCulture));
            }
            if (!File.Exists(jpg)) return (null, seconds);
            var bmp = new BitmapImage();
            using (var fs = new FileStream(jpg, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 400;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return (bmp, seconds);
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail failed for {clipPath}: {ex.Message}");
            return (null, seconds);
        }
    }

    /// <summary>Deletes cached thumbnails whose clip no longer exists.</summary>
    public static void PruneThumbs(HashSet<string> liveKeys)
    {
        try
        {
            foreach (string f in Directory.GetFiles(AppPaths.ThumbsDir))
            {
                if (!liveKeys.Contains(Path.GetFileNameWithoutExtension(f))) File.Delete(f);
            }
        }
        catch { }
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : $"{bytes / (double)(1L << 20):F1} MB";
}
