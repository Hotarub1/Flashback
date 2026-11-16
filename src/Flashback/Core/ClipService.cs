using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Flashback.Core;

public sealed record ClipResult(bool Ok, string? Path, string? Error, double Seconds, double StartUtc = 0);

/// <summary>Turns the last N seconds of buffered video and audio into an MP4 file.</summary>
public sealed class ClipService
{
    private readonly string _ffmpeg;
    private readonly VideoRecorder _recorder;
    private readonly AudioSource _desktop;
    private readonly AudioSource _mic;
    private readonly JobObject _job;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipService(string ffmpeg, VideoRecorder recorder, AudioSource desktop, AudioSource mic, JobObject job)
    {
        _ffmpeg = ffmpeg;
        _recorder = recorder;
        _desktop = desktop;
        _mic = mic;
        _job = job;
    }

    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<ClipResult> SaveAsync(AppSettings settings)
    {
        double pressed = Clock.UtcNow();
        if (!await _gate.WaitAsync(0)) return new ClipResult(false, null, "A clip is already being saved", 0);
        try
        {
            return await Task.Run(() => Save(settings, pressed));
        }
        catch (Exception ex)
        {
            Log.Error("Saving a clip failed", ex);
            return new ClipResult(false, null, ex.Message, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ClipResult Save(AppSettings s, double pressed)
    {
        // Wait (at most ~2.5 s) for the segment containing the key press to be finished, so the clip
        // includes everything right up to the moment the hotkey was hit.
        long waitStart = Environment.TickCount64;
        while (_recorder.LatestEndUtc < pressed && Environment.TickCount64 - waitStart < 2500
               && _recorder.Status.State is RecorderState.Recording or RecorderState.Starting)
            Thread.Sleep(40);

        _recorder.BeginRead();
        string listFile = Path.Combine(AppPaths.TempDir, $"clip_{Guid.NewGuid():N}.ffconcat");
        string? partFile = null;
        try
        {
            List<Segment> chosen = ChooseSegments(_recorder.Snapshot(), pressed, s.ClipSeconds);
            if (chosen.Count == 0)
            {
                var st = _recorder.Status;
                string why = st.State == RecorderState.Recording
                    ? "Nothing has been recorded yet. Try again in a few seconds."
                    : $"Recording is not running ({(st.Detail.Length > 0 ? st.Detail : st.State.ToString())})";
                return new ClipResult(false, null, why, 0);
            }
            double seconds = chosen.Sum(x => x.EndUtc - x.StartUtc);

            // Give the last few milliseconds of audio time to arrive from the sound card.
            Thread.Sleep(150);
            short[]? pcm = s.DesktopAudio || s.Microphone ? BuildAudio(chosen, s) : null;

            var list = new StringBuilder("ffconcat version 1.0\n");
            foreach (Segment seg in chosen)
            {
                list.Append("file '").Append(seg.Path.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                list.Append("duration ").Append((seg.EndUtc - seg.StartUtc).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(listFile, list.ToString(), new UTF8Encoding(false));

            Directory.CreateDirectory(s.SaveFolder);
            long needed = (long)(seconds * 8_000_000) + 200L * 1024 * 1024;
            string? root = Path.GetPathRoot(Path.GetFullPath(s.SaveFolder));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < needed)
                return new ClipResult(false, null, $"Not enough free space on {root.TrimEnd('\\')}", 0);

            string finalPath = UniquePath(s.SaveFolder, $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
            partFile = finalPath + ".part";

            RecorderConfig? cfg = _recorder.Status.Config;
            int srcW = cfg?.Width ?? 0, srcH = cfg?.Height ?? 0, fps = cfg?.Fps ?? 60;
            bool wantScale = s.ClipHeight > 0 && srcH > s.ClipHeight && srcW > 0;
            int timeout = 60000 + (int)(seconds * 1000);

            long muxStart = Environment.TickCount64;
            (int code, string err) = (-1, "");
            if (wantScale)
            {
                // Smaller clip resolution: convert on the GPU while saving. If that fails for any reason,
                // fall through and save at native resolution instead of losing the clip.
                int outH = s.ClipHeight;
                int outW = (int)Math.Round(srcW * (double)outH / srcH / 2) * 2;
                (int cq, int kbps) = AppSettings.VideoQuality(s.Quality, outW, outH, fps);
                (code, err) = Mux(listFile, partFile, pcm, timeout,
                    ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"],
                    [
                        "-vf", $"scale_cuda={outW}:{outH}", "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr",
                        "-cq", cq.ToString(), "-b:v", "0", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-g", (fps * 2).ToString(),
                    ]);
                if (code != 0)
                {
                    Log.Warn($"Resize while saving failed ({code}), keeping native resolution. {Ffmpeg.LastLine(err)}");
                    TryDelete(partFile);
                }
            }
            if (code != 0) (code, err) = Mux(listFile, partFile, pcm, timeout, [], ["-c:v", "copy"]);

            if (code != 0 || !File.Exists(partFile) || new FileInfo(partFile).Length < 1024)
            {
                Log.Error($"Clip mux failed ({code}): {err}");
                TryDelete(partFile);
                return new ClipResult(false, null, "Could not write the clip: " + Ffmpeg.LastLine(err), 0);
            }
            File.Move(partFile, finalPath);
            partFile = null;
            Log.Info($"Clip saved: {finalPath} ({seconds:F1}s, {chosen.Count} segments, {new FileInfo(finalPath).Length / 1048576.0:F1} MB, written in {(Environment.TickCount64 - muxStart) / 1000.0:F1}s)");
            return new ClipResult(true, finalPath, null, seconds, chosen[0].StartUtc);
        }
        finally
        {
            _recorder.EndRead();
            TryDelete(listFile);
            if (partFile != null) TryDelete(partFile);
        }
    }

    /// <summary>Walks back from the newest segment collecting the requested amount of compatible, recent footage.</summary>
    private static List<Segment> ChooseSegments(List<Segment> all, double pressed, int wantSeconds)
    {
        var chosen = new List<Segment>();
        double total = 0;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            Segment seg = all[i];
            if (seg.StartUtc > pressed + 0.5) continue;
            if (chosen.Count == 0)
            {
                if (pressed - seg.EndUtc > 5.0) break;                    // newest footage is stale: recorder is down
            }
            else
            {
                Segment next = chosen[^1];
                if (seg.ParamsKey != next.ParamsKey) break;               // resolution/quality changed here
                if (next.StartUtc - seg.EndUtc > 5.0) break;              // recording was interrupted for a while
            }
            if (!File.Exists(seg.Path)) break;
            chosen.Add(seg);
            total += seg.EndUtc - seg.StartUtc;
            if (total >= wantSeconds - 0.01) break;
        }
        chosen.Reverse();
        return chosen;
    }

    /// <summary>Lays desktop and microphone audio out against the chosen video, sample-accurately by wall-clock time.</summary>
    private short[] BuildAudio(List<Segment> chosen, AppSettings s)
    {
        // Group back-to-back segments so each uninterrupted stretch of video gets one stretch of audio.
        var parts = new List<(double Start, double End)>();
        foreach (Segment seg in chosen)
        {
            if (parts.Count > 0 && Math.Abs(seg.StartUtc - parts[^1].End) < 0.02)
                parts[^1] = (parts[^1].Start, seg.EndUtc);
            else
                parts.Add((seg.StartUtc, seg.EndUtc));
        }
        long totalFrames = parts.Sum(p => (long)Math.Round((p.End - p.Start) * AudioRing.SampleRate));
        var pcm = new short[totalFrames * 2];
        long offset = 0, desktopFrames = 0, micFrames = 0;
        foreach (var (start, end) in parts)
        {
            if (s.DesktopAudio) desktopFrames += _desktop.Ring.Render(start, end, pcm, offset, 1f);
            if (s.Microphone) micFrames += _mic.Ring.Render(start, end, pcm, offset, s.MicVolume / 100f);
            offset += (long)Math.Round((end - start) * AudioRing.SampleRate);
        }
        Log.Info($"Clip audio: desktop {desktopFrames / (double)AudioRing.SampleRate:F1}s, microphone {micFrames / (double)AudioRing.SampleRate:F1}s of {totalFrames / (double)AudioRing.SampleRate:F1}s");
        return pcm;
    }

    private (int Code, string Err) Mux(string listFile, string output, short[]? pcm, int timeoutMs, string[] inputOptions, string[] videoOptions)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y" };
        args.AddRange(inputOptions);
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile]);
        if (pcm != null) args.AddRange(["-f", "s16le", "-ar", AudioRing.SampleRate.ToString(), "-ac", "2", "-i", "pipe:0"]);
        args.AddRange(["-map", "0:v:0"]);
        if (pcm != null) args.AddRange(["-map", "1:a:0"]);
        args.AddRange(videoOptions);
        if (pcm != null) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", output]);

        Action<Stream>? feed = pcm == null ? null : stdin =>
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(pcm.AsSpan());
            for (int i = 0; i < bytes.Length; i += 1 << 16)
                stdin.Write(bytes.Slice(i, Math.Min(1 << 16, bytes.Length - i)));
        };
        return Ffmpeg.Run(_ffmpeg, args, timeoutMs, _job, feed);
    }

    private static string UniquePath(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(folder, $"{name} ({i}).mp4");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
