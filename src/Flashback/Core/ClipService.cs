using System.Globalization;
using System.IO;
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
    private readonly AppAudioCapture _apps;
    private readonly JobObject _job;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// When the hotkey sound plays, the clip's audio ends this long before the press. Measured on this PC, the sound
    /// reaches the recorded desktop audio 15–35 ms after the press; devices with less output delay would leave less room.
    /// </summary>
    private const double SoundCutLeadSeconds = 0.05;

    public ClipService(string ffmpeg, VideoRecorder recorder, AudioSource desktop, AudioSource mic, AppAudioCapture apps, JobObject job)
    {
        _ffmpeg = ffmpeg;
        _recorder = recorder;
        _desktop = desktop;
        _mic = mic;
        _apps = apps;
        _job = job;
    }

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <param name="silenceAfterPress">
    /// The hotkey sound plays right after this call. The clip's video runs up to a second past the press (to the end of
    /// that one-second segment); its audio is ended just before the press instead, so the sound is not heard in the clip.
    /// </param>
    public async Task<ClipResult> SaveAsync(AppSettings settings, bool silenceAfterPress = false)
    {
        double pressed = Clock.UtcNow();
        if (!await _gate.WaitAsync(0)) return new ClipResult(false, null, "A clip is already being saved", 0);
        try
        {
            return await Task.Run(() => Save(settings, pressed, silenceAfterPress));
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

    private ClipResult Save(AppSettings s, double pressed, bool silenceAfterPress)
    {
        // Wait (at most ~2.5 s) for the segment containing the key press to be finished, so the clip
        // includes everything right up to the moment the hotkey was hit.
        long waitStart = Environment.TickCount64;
        while (_recorder.LatestEndUtc < pressed && Environment.TickCount64 - waitStart < 2500
               && _recorder.Status.State is RecorderState.Recording or RecorderState.Starting)
            Thread.Sleep(40);

        _recorder.BeginRead();
        string listFile = Path.Combine(AppPaths.TempDir, $"clip_{Guid.NewGuid():N}.ffconcat");
        string fullProbeListFile = Path.ChangeExtension(listFile, ".full.ffconcat");
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
            ClipStems? stems = s.DesktopAudio || s.Microphone
                ? BuildAudio(chosen, s, silenceAfterPress ? pressed - SoundCutLeadSeconds : null)
                : null;

            File.WriteAllText(listFile, ConcatList(chosen, quickProbe: true), new UTF8Encoding(false));

            Directory.CreateDirectory(s.SaveFolder);
            long needed = (long)(seconds * 8_000_000) + 200L * 1024 * 1024;
            string? root = Path.GetPathRoot(Path.GetFullPath(s.SaveFolder));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < needed)
                return new ClipResult(false, null, $"Not enough free space on {root.TrimEnd('\\')}", 0);

            string finalPath = ClipLibrary.UniquePath(s.SaveFolder, $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
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
                // Preset p3 converts a 90 s 1440p clip about 40% faster than p5; on real footage the quality
                // difference measured under 1 VMAF point, which is not visible.
                int outH = s.ClipHeight;
                int outW = (int)Math.Round(srcW * (double)outH / srcH / 2) * 2;
                (int cq, int kbps) = AppSettings.VideoQuality(s.Quality, outW, outH, fps);
                (code, err) = Mux(listFile, partFile, stems, timeout,
                    ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"],
                    [
                        "-vf", $"scale_cuda={outW}:{outH}", "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "hq", "-rc", "vbr",
                        "-cq", cq.ToString(), "-b:v", "0", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k", "-g", (fps * 2).ToString(),
                    ]);
                if (code != 0)
                {
                    Log.Warn($"Resize while saving failed ({code}), keeping native resolution. {Ffmpeg.LastLine(err)}");
                    TryDelete(partFile);
                }
            }
            if (code != 0) (code, err) = Mux(listFile, partFile, stems, timeout, [], ["-c:v", "copy"]);
            if (code != 0 && !err.StartsWith("Timed out", StringComparison.Ordinal))
            {
                // Let FFmpeg probe every piece in full, exactly as before the quick probe existed.
                // Skipped after a time-out so a hung save cannot run long enough for the buffer to wrap around.
                Log.Warn($"Saving failed ({code}), retrying with full probing. {Ffmpeg.LastLine(err)}");
                TryDelete(partFile);
                File.WriteAllText(fullProbeListFile, ConcatList(chosen, quickProbe: false), new UTF8Encoding(false));
                (code, err) = Mux(fullProbeListFile, partFile, stems, timeout, [], ["-c:v", "copy"]);
            }
            if (code != 0 && !err.StartsWith("Timed out", StringComparison.Ordinal) && stems is { Separate: true })
            {
                // Last resort: one mixed audio track, exactly as clips were saved before they carried separate
                // tracks. The clip is kept even if the separate tracks cannot be written.
                Log.Warn($"Saving failed ({code}), retrying with a single mixed audio track. {Ffmpeg.LastLine(err)}");
                TryDelete(partFile);
                (code, err) = Mux(fullProbeListFile, partFile, stems, timeout, [], ["-c:v", "copy"], mixOnly: true);
            }

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
            TryDelete(fullProbeListFile);
            if (partFile != null) TryDelete(partFile);
        }
    }

    /// <summary>
    /// The FFmpeg concat list for the chosen segments. By default FFmpeg decodes about 7 frames of every one-second
    /// piece on a single CPU thread when it opens it, only to learn what the first piece already told it. That took
    /// about 90 ms per piece, 8 seconds for a 90 s clip. <paramref name="quickProbe"/> limits that to the first packet.
    /// The video that comes out is byte-for-byte the same.
    /// </summary>
    private static string ConcatList(List<Segment> chosen, bool quickProbe)
    {
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (Segment seg in chosen)
        {
            list.Append("file '").Append(seg.Path.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
            if (quickProbe) list.Append("option probesize 32\n");
            list.Append("duration ").Append((seg.EndUtc - seg.StartUtc).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
        return list.ToString();
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

    /// <summary>
    /// Lays desktop and microphone audio out against the chosen video, sample-accurately by wall-clock time.
    /// Each source is rendered on its own; the clip gets them as separate tracks plus their mix. With separate app
    /// tracks on, the desktop sound is split into one track per app when those tracks account for it.
    /// </summary>
    private ClipStems BuildAudio(List<Segment> chosen, AppSettings s, double? silenceFromUtc)
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
        short[]? desktop = s.DesktopAudio ? new short[totalFrames * 2] : null;
        short[]? mic = s.Microphone ? new short[totalFrames * 2] : null;
        long offset = 0, desktopFrames = 0, micFrames = 0;
        foreach (var (start, end) in parts)
        {
            if (desktop != null) desktopFrames += _desktop.Ring.Render(start, end, desktop, offset, 1f);
            if (mic != null) micFrames += _mic.Ring.Render(start, end, mic, offset, s.MicVolume / 100f);
            offset += (long)Math.Round((end - start) * AudioRing.SampleRate);
        }

        List<ClipTrack>? apps = null;
        string appNote = "";
        if (desktop != null && s.SeparateAppAudio && _apps.IsRunning)
        {
            try
            {
                apps = AppTracks.Build(_apps.Snapshot(), parts, totalFrames, desktop, out appNote);
            }
            catch (Exception ex)
            {
                Log.Error("Building the app tracks failed; saving one desktop track", ex);
                apps = null;
                appNote = "failed";
            }
        }

        long silenced = 0;
        if (silenceFromUtc is double cut)
        {
            if (desktop != null) silenced = SilenceFrom(desktop, parts, cut);
            if (mic != null) silenced = SilenceFrom(mic, parts, cut);
            foreach (ClipTrack t in apps ?? []) SilenceFrom(t.Pcm, parts, cut);
        }
        Log.Info($"Clip audio: desktop {desktopFrames / (double)AudioRing.SampleRate:F1}s, microphone {micFrames / (double)AudioRing.SampleRate:F1}s of {totalFrames / (double)AudioRing.SampleRate:F1}s"
                 + (silenced > 0 ? $", last {silenced / (double)AudioRing.SampleRate:F2}s silenced (after the hotkey press)" : "")
                 + (appNote.Length == 0 ? "" : apps != null ? $"; app tracks: {appNote}" : $"; one desktop track because {appNote}"));

        var tracks = new List<ClipTrack>();
        if (apps != null) tracks.AddRange(apps);
        else if (desktop != null) tracks.Add(new ClipTrack(ClipAudio.DesktopTitle, desktop));
        if (mic != null) tracks.Add(new ClipTrack(ClipAudio.MicTitle, mic));
        if (apps == null) return new ClipStems(tracks);
        // The mix stays the full desktop capture plus the microphone, so it has every sound even if no app track caught it.
        return new ClipStems(tracks, mic != null ? [desktop!, mic] : [desktop!]);
    }

    /// <summary>
    /// Fades the audio out over the 20 ms before <paramref name="cutUtc"/> and silences everything after it.
    /// Returns how many frames were silenced (0 if the clip ends before the cut).
    /// </summary>
    internal static long SilenceFrom(short[] pcm, List<(double Start, double End)> parts, double cutUtc)
    {
        long totalFrames = pcm.Length / 2, offset = 0, cutFrame = -1;
        foreach (var (start, end) in parts)
        {
            if (cutUtc < end)
            {
                cutFrame = offset + Math.Max(0, (long)Math.Round((cutUtc - start) * AudioRing.SampleRate));
                break;
            }
            offset += (long)Math.Round((end - start) * AudioRing.SampleRate);
        }
        if (cutFrame < 0 || cutFrame >= totalFrames) return 0;

        long fadeFrames = Math.Min(cutFrame, AudioRing.SampleRate / 50);
        for (long i = 0; i < fadeFrames; i++)
        {
            long f = cutFrame - fadeFrames + i;
            float gain = 1f - (i + 1) / (float)(fadeFrames + 1);
            pcm[f * 2] = (short)(pcm[f * 2] * gain);
            pcm[f * 2 + 1] = (short)(pcm[f * 2 + 1] * gain);
        }
        Array.Clear(pcm, (int)(cutFrame * 2), (int)((totalFrames - cutFrame) * 2));
        return totalFrames - cutFrame;
    }

    private (int Code, string Err) Mux(string listFile, string output, ClipStems? stems, int timeoutMs,
        string[] inputOptions, string[] videoOptions, bool mixOnly = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-y" };
        args.AddRange(inputOptions);
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile]);
        if (stems != null) ClipAudio.AddPipeInput(args, stems);
        args.AddRange(["-map", "0:v:0"]);
        if (stems != null) ClipAudio.AddOutputs(args, stems, 1, mixOnly);
        args.AddRange(videoOptions);
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", output]);

        Action<Stream>? feed = stems == null ? null : stems.WriteTo;
        return Ffmpeg.Run(_ffmpeg, args, timeoutMs, _job, feed);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
