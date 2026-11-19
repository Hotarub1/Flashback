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

    /// <summary>
    /// What the clip is called on screen: "Today, 5:22 PM" for a clip that still has the name it was saved with, or
    /// the name the user gave it.
    /// </summary>
    public string Title => ClipLibrary.Title(Name, Modified);

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

    /// <summary>The name a clip is saved with ("Clip 2026-11-18 17-22-41"), and that of a trimmed copy of it.</summary>
    [GeneratedRegex(@"^Clip \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?<trimmed> \(trimmed\)(?: \(\d+\))?)?(?: \(\d+\))?$")]
    private static partial Regex SavedNameRegex();

    public static string Title(string name, DateTime when)
    {
        Match m = SavedNameRegex().Match(name);
        if (!m.Success) return name;
        string title = $"{DayName(when.Date)}, {when.ToString("t", CultureInfo.CurrentCulture)}";
        return m.Groups["trimmed"].Success ? title + " · trimmed" : title;
    }

    /// <summary>"Today", "Yesterday", the weekday for the past week, otherwise the date.</summary>
    public static string DayName(DateTime day)
    {
        int ago = (DateTime.Today - day.Date).Days;
        return ago switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => day.ToString("dddd", CultureInfo.CurrentCulture),
            _ => day.Year == DateTime.Today.Year ? day.ToString("MMM d", CultureInfo.CurrentCulture) : day.ToString("MMM d, yyyy", CultureInfo.CurrentCulture),
        };
    }

    private static readonly string[] Ones =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
         "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty"];

    private static string Number(int n) => n < 20 ? Ones[n] : n % 10 == 0 ? Tens[n / 10] : $"{Tens[n / 10]}-{Ones[n % 10]}";

    /// <summary>A clip length in words: "Forty-five seconds", "One minute, thirty seconds".</summary>
    public static string LengthInWords(double seconds)
    {
        int total = (int)Math.Round(seconds);
        if (total <= 0) return "";
        int minutes = total / 60, rest = total % 60;
        string Part(int n, string unit) => $"{Number(n)} {unit}{(n == 1 ? "" : "s")}";
        string text = minutes == 0 ? Part(rest, "second")
            : minutes >= 60 ? FormatDuration(total)
            : rest == 0 ? Part(minutes, "minute")
            : $"{Part(minutes, "minute")}, {Part(rest, "second")}";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

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

    /// <summary>A free file name in <paramref name="folder"/> for <paramref name="name"/>.mp4, numbered if that is taken.</summary>
    public static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(folder, $"{name} ({i}).mp4");
        return path;
    }

    /// <summary>Removes half-written clips (saves or trims) left behind if the app was killed mid-write.</summary>
    public static void RemoveStaleParts(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (string f in Directory.GetFiles(folder, "*.mp4.part"))
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

    /// <summary>
    /// A larger still of the clip (the thumbnail's frame) for the "Latest" panel, where the thumbnail would look soft.
    /// Cached next to the thumbnail. Safe to call off the UI thread.
    /// </summary>
    public static ImageSource? LoadPoster(string ffmpeg, string clipPath, string key, JobObject? job)
    {
        string jpg = Path.Combine(AppPaths.ThumbsDir, key + "-poster.jpg");
        try
        {
            if (!File.Exists(jpg))
                Ffmpeg.Run(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-ss", "0.5", "-i", clipPath, "-frames:v", "1", "-vf", "scale=1280:-2", "-q:v", "3", jpg],
                    30000, job, null, lowPriority: true);
            if (!File.Exists(jpg)) return null;
            var bmp = new BitmapImage();
            using (var fs = new FileStream(jpg, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Poster failed for {clipPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>How many frames a filmstrip holds, side by side: frame i is from i/12 of the way into the clip.</summary>
    public const int FilmstripFrames = 12;

    /// <summary>
    /// A strip of frames from across the clip for the player's video bar, cached next to the thumbnail. Only
    /// keyframes are decoded, so it is quick even for long clips. Safe to call off the UI thread.
    /// </summary>
    public static ImageSource? LoadFilmstrip(string ffmpeg, string clipPath, string key, double seconds, JobObject? job)
    {
        if (seconds <= 0) return null;
        string jpg = Path.Combine(AppPaths.ThumbsDir, key + "-strip.jpg");
        try
        {
            if (!File.Exists(jpg))
            {
                string rate = (FilmstripFrames / seconds).ToString("0.######", CultureInfo.InvariantCulture);
                Ffmpeg.Run(ffmpeg,
                    ["-hide_banner", "-nostdin", "-y", "-skip_frame", "nokey", "-i", clipPath, "-an", "-sn",
                     "-vf", $"fps={rate},scale=192:-2,tile={FilmstripFrames}x1", "-frames:v", "1", "-q:v", "5", jpg],
                    30000, job, null, lowPriority: true);
            }
            if (!File.Exists(jpg)) return null;
            var bmp = new BitmapImage();
            using (var fs = new FileStream(jpg, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Filmstrip failed for {clipPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deletes cached thumbnails and filmstrips whose clip no longer exists.</summary>
    public static void PruneThumbs(HashSet<string> liveKeys)
    {
        try
        {
            foreach (string f in Directory.GetFiles(AppPaths.ThumbsDir))
            {
                string key = Path.GetFileName(f).Split('.', '-')[0];
                if (!liveKeys.Contains(key)) File.Delete(f);
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
