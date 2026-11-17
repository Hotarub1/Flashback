using System.Globalization;
using System.IO;

namespace Flashback.Core;

/// <summary>A trimmed copy of a clip to write: the part to keep and how loud each audio track should be in it.</summary>
public sealed record TrimRequest(
    string Source, string Output, double Start, double End, float FirstGain, float SecondGain, ClipInfo Info, string Quality);

/// <summary>
/// Writes trimmed copies of clips. The video is copied untouched when the trim starts at the beginning, otherwise it
/// is re-encoded from the chosen start (a cut on a frame that is not a keyframe cannot be stream-copied cleanly).
/// Audio is remixed from the clip's own tracks with the requested volumes, so the copy again carries the mix plus
/// the separate tracks and can be trimmed once more later.
/// </summary>
public static class ClipEditor
{
    /// <returns>Null on success, otherwise a message for the user.</returns>
    public static string? Export(string ffmpeg, TrimRequest r, JobObject? job, Action<double>? progress)
    {
        double duration = r.End - r.Start;
        if (duration < 0.1) return "The trimmed clip would be shorter than a tenth of a second.";
        if (!File.Exists(r.Source)) return "The clip no longer exists.";

        string part = r.Output + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Output)!);
            string? root = Path.GetPathRoot(Path.GetFullPath(r.Output));
            long needed = Math.Min(new FileInfo(r.Source).Length, (long)(duration * 8_000_000)) + 200L * 1024 * 1024;
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < needed)
                return $"Not enough free space on {root.TrimEnd('\\')}.";

            bool copyVideo = r.Start < 0.001;
            int fps = (int)Math.Round(r.Info.Fps);
            if (fps <= 0) fps = 60;
            (int cq, int kbps) = AppSettings.VideoQuality(r.Quality, Math.Max(r.Info.Width, 16), Math.Max(r.Info.Height, 16), fps);
            string gop = (fps * 2).ToString();

            var attempts = new List<(string Name, string[] InputOptions, string[] VideoOptions, int TimeoutMs)>();
            if (copyVideo)
            {
                attempts.Add(("copy", [], ["-c:v", "copy"], 60000 + (int)(duration * 1000)));
            }
            else
            {
                string[] nvenc =
                [
                    "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "hq", "-rc", "vbr", "-cq", cq.ToString(), "-b:v", "0",
                    "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-g", gop,
                ];
                int gpuTimeout = 60000 + (int)(duration * 3000);
                attempts.Add(("GPU", ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"], nvenc, gpuTimeout));
                attempts.Add(("GPU encoder", [], nvenc, gpuTimeout));
                attempts.Add(("CPU", [],
                [
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", Math.Max(16, cq - 3).ToString(),
                    "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-g", gop, "-pix_fmt", "yuv420p",
                ], 60000 + (int)(duration * 8000)));
            }

            string err = "";
            foreach (var (name, inputOptions, videoOptions, timeout) in attempts)
            {
                TryDelete(part);
                int code;
                (code, err) = Run(ffmpeg, r, part, duration, copyVideo, inputOptions, videoOptions, timeout, job, progress);
                if (code == 0 && File.Exists(part) && new FileInfo(part).Length >= 1024)
                {
                    File.Move(part, r.Output);
                    Log.Info($"Trim saved: {r.Output} ({r.Start:F2}s to {r.End:F2}s, volumes {r.FirstGain:F2}/{r.SecondGain:F2}, video {name})");
                    return null;
                }
                Log.Warn($"Trim with {name} video failed ({code}): {Ffmpeg.LastLine(err)}");
                if (err.StartsWith("Timed out", StringComparison.Ordinal)) break;
            }
            return "The trimmed clip could not be written: " + Ffmpeg.LastLine(err);
        }
        catch (Exception ex)
        {
            Log.Error("Trim failed", ex);
            return ex.Message;
        }
        finally
        {
            TryDelete(part);
        }
    }

    private static (int Code, string Err) Run(string ffmpeg, TrimRequest r, string output, double duration, bool copyVideo,
        string[] inputOptions, string[] videoOptions, int timeoutMs, JobObject? job, Action<double>? progress)
    {
        string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-nostdin", "-y" };
        args.AddRange(inputOptions);
        if (!copyVideo) args.AddRange(["-ss", F(r.Start)]);
        args.AddRange(["-i", r.Source]);
        bool cutEnd = r.End < r.Info.Seconds - 0.02 || !copyVideo;
        if (cutEnd) args.AddRange(["-t", F(duration)]);

        args.AddRange(["-map", "0:v:0"]);
        if (r.Info.HasStems)
        {
            // Volume per track, the mix as the sum (clamped to 16-bit range like the recorder does), the tracks again on their own.
            args.AddRange(["-filter_complex",
                $"[0:a:1]volume={F(r.FirstGain)},aformat=sample_fmts=s16,asplit[a1][a2];" +
                $"[0:a:2]volume={F(r.SecondGain)},aformat=sample_fmts=s16,asplit[b1][b2];" +
                "[a1][b1]amix=inputs=2:normalize=0,aformat=sample_fmts=s16[mix]",
                "-map", "[mix]", "-map", "[a2]", "-map", "[b2]"]);
            ClipAudio.AddCodec(args, [ClipAudio.MixTitle, ClipAudio.DesktopTitle, ClipAudio.MicTitle]);
        }
        else if (r.Info.AudioStreams > 0)
        {
            args.AddRange(["-filter_complex", $"[0:a:0]volume={F(r.FirstGain)},aformat=sample_fmts=s16[mix]", "-map", "[mix]"]);
            ClipAudio.AddCodec(args, [r.Info.AudioTitles[0].Length > 0 ? r.Info.AudioTitles[0] : ClipAudio.MixTitle]);
        }
        args.AddRange(videoOptions);
        args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-f", "mp4", output]);

        Action<string>? onLine = progress == null ? null : line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                progress(Math.Clamp(us / 1e6 / duration, 0, 1));
            else if (line.StartsWith("progress=end", StringComparison.Ordinal)) progress(1);
        };
        return Ffmpeg.Run(ffmpeg, args, timeoutMs, job, null, lowPriority: false, onStdoutLine: onLine);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
