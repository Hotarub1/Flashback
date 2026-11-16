using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Flashback.Core;

/// <summary>One finished 1-second piece of video in the rolling buffer.</summary>
public sealed record Segment(string Path, double StartUtc, double EndUtc, int SessionId, string ParamsKey);

public enum RecorderState { Stopped, Starting, Recording, Retrying, Paused, Error }

public sealed class RecorderConfig
{
    public int OutputIndex;
    public int Width;
    public int Height;
    public int Fps;
    public int Cq;
    public int MaxKbps;
    public bool Cursor;
    public string Encoder = "h264_nvenc";

    /// <summary>Segments can only be joined into one clip when this matches.</summary>
    public string Key => $"{Encoder}/{Width}x{Height}@{Fps}/q{Cq}/{MaxKbps}";

    public bool SameAs(RecorderConfig? o) => o != null && o.Key == Key && o.OutputIndex == OutputIndex && o.Cursor == Cursor;
}

/// <summary>
/// Keeps one FFmpeg process capturing the main monitor into 1-second segments, and keeps it alive:
/// if FFmpeg exits or stops producing segments it is restarted automatically.
/// </summary>
public sealed class VideoRecorder
{
    // FFmpeg reuses segment file names after this many: a hard cap on disk use even if cleanup were to fail.
    // It must exceed the longest retention (315 s) plus the longest possible clip save (360 s).
    private const int SegmentWrap = 720;
    private const long PauseBelowBytes = 1536L * 1024 * 1024;
    private const long ResumeAboveBytes = 2048L * 1024 * 1024;

    private readonly string _ffmpeg;
    private readonly string _root;
    private readonly Func<RecorderConfig?> _configProvider;
    private readonly JobObject _job;

    private readonly object _lock = new();
    private readonly List<Segment> _segments = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _restart;
    private int _leases;
    private double _latestEnd;
    private bool _lowDisk;
    private long _lastDirSweep;

    private RecorderState _state = RecorderState.Stopped;
    private string _detail = "";
    private RecorderConfig? _active;

    /// <summary>How many seconds of video to keep on disk.</summary>
    public volatile int RetainSeconds = 45;

    /// <summary>Called on the recorder thread after a session that failed to stay up, with the consecutive failure count.</summary>
    public Action<int>? SessionFailed;

    public VideoRecorder(string ffmpegPath, string bufferRoot, Func<RecorderConfig?> configProvider, JobObject job)
    {
        _ffmpeg = ffmpegPath;
        _root = bufferRoot;
        _configProvider = configProvider;
        _job = job;
    }

    public (RecorderState State, string Detail, RecorderConfig? Config) Status
    {
        get { lock (_lock) return (_state, _detail, _active); }
    }

    public double LatestEndUtc
    {
        get { lock (_lock) return _latestEnd; }
    }

    /// <summary>Seconds of video currently available for a clip (segments are one second each).</summary>
    public int BufferedSeconds
    {
        get { lock (_lock) return _segments.Count; }
    }

    public List<Segment> Snapshot()
    {
        lock (_lock) return new List<Segment>(_segments);
    }

    /// <summary>While a lease is held no buffered segment is deleted.</summary>
    public void BeginRead() => Interlocked.Increment(ref _leases);
    public void EndRead() => Interlocked.Decrement(ref _leases);

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "VideoRecorder" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(8000);
        _thread = null;
    }

    /// <summary>Retry now if the recorder is waiting between attempts. Does nothing to a healthy session.</summary>
    public void Wake() => _wake.Set();

    public void RequestRestart(string reason)
    {
        Log.Info("Recorder restart requested: " + reason);
        _restart = true;
        _wake.Set();
    }

    private void SetState(RecorderState state, string detail)
    {
        lock (_lock)
        {
            if (_state == state && _detail == detail) return;
            _state = state;
            _detail = detail;
        }
        Log.Info($"Recorder: {state} {detail}");
    }

    private void Run()
    {
        int sessionId = 0, failures = 0;
        DeleteBufferRoot();
        try { Directory.CreateDirectory(_root); }
        catch (Exception ex) { Log.Error("Cannot create buffer folder", ex); }

        while (!_stop)
        {
            if (!DiskOk(out string why))
            {
                SetState(RecorderState.Paused, why);
                _wake.WaitOne(5000);
                continue;
            }

            RecorderConfig? cfg = null;
            try { cfg = _configProvider(); }
            catch (Exception ex) { Log.Error("Could not build the recorder configuration", ex); }
            if (cfg == null)
            {
                SetState(RecorderState.Error, "Recorder could not be configured");
                _wake.WaitOne(3000);
                continue;
            }

            _restart = false;
            sessionId++;
            bool healthy = false;
            string exitInfo = "";
            try
            {
                string dir = Path.Combine(_root, $"s{sessionId:D5}");
                Directory.CreateDirectory(dir);
                healthy = RunSession(cfg, sessionId, dir, failures, out exitInfo);
            }
            catch (Exception ex)
            {
                exitInfo = ex.Message;
                Log.Error("Recorder session failed", ex);
            }

            if (_stop) break;
            if (_restart)
            {
                _restart = false;
                failures = 0;
                continue;
            }
            failures = healthy ? 0 : failures + 1;
            SetState(RecorderState.Retrying, exitInfo);
            if (!healthy)
            {
                try { SessionFailed?.Invoke(failures); }
                catch (Exception ex) { Log.Error("SessionFailed handler", ex); }
            }
            // Retry almost immediately the first time (a display change is over in a moment), then ease off
            // while the screen stays unavailable, for example on the lock screen.
            _wake.WaitOne(failures <= 1 ? 250 : failures <= 5 ? 1000 : failures <= 15 ? 2000 : 5000);
        }

        lock (_lock)
        {
            _segments.Clear();
            _active = null;
        }
        SetState(RecorderState.Stopped, "");
        DeleteBufferRoot();
    }

    /// <returns>True when the session recorded for long enough to count as healthy.</returns>
    private bool RunSession(RecorderConfig cfg, int sessionId, string dir, int priorFailures, out string exitInfo)
    {
        double epoch = Math.Round(Clock.UtcNow(), 3);
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in BuildArgs(cfg, epoch)) psi.ArgumentList.Add(a);

        var tail = new Queue<(long Tick, string Line)>();
        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (tail)
            {
                tail.Enqueue((Environment.TickCount64, e.Data));
                while (tail.Count > 30) tail.Dequeue();
            }
        };

        SetState(RecorderState.Starting, "");
        proc.Start();
        _job.Add(proc);
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        proc.BeginErrorReadLine();
        lock (_lock) _active = cfg;

        var csv = new CsvTail(Path.Combine(dir, "seg.csv"));
        long lastProgress = Environment.TickCount64, lastDisk = lastProgress;
        int lines = 0;
        string reason;

        while (true)
        {
            if (_stop) { reason = "stop"; break; }
            if (_restart) { reason = "restart"; break; }

            if (Ingest(csv, dir, epoch, sessionId, cfg.Key, ref lines))
            {
                lastProgress = Environment.TickCount64;
                SetState(RecorderState.Recording, "");
            }
            long now = Environment.TickCount64;
            if (proc.HasExited) { reason = "exit"; break; }
            // Segments arrive once a second. Silence means capture is stuck (sleep, display change, driver reset).
            if (now - lastProgress > (lines > 0 ? 6000 : 12000)) { reason = "stall"; break; }
            if (now - lastDisk > 5000)
            {
                lastDisk = now;
                if (!DiskOk(out _)) { reason = "disk"; break; }
            }
            Janitor(sessionId);
            _wake.WaitOne(200);
        }

        if (!proc.HasExited)
        {
            try { proc.Kill(true); } catch { }
        }
        try { proc.WaitForExit(3000); } catch { }
        Ingest(csv, dir, epoch, sessionId, cfg.Key, ref lines);

        // Only what FFmpeg said in its final moments explains why it stopped.
        List<string> recent;
        lock (tail)
        {
            long newest = tail.Count > 0 ? tail.Last().Tick : 0;
            recent = tail.Where(t => newest - t.Tick < 3000).Select(t => t.Line).ToList();
        }
        // While the screen stays unavailable (lock screen) the same failure repeats: keep it out of the log.
        bool quiet = lines < 10 && priorFailures >= 3 && (priorFailures + 1) % 20 != 0;
        switch (reason)
        {
            case "exit":
                int code = 0;
                try { code = proc.ExitCode; } catch { }
                exitInfo = Explain(recent, code);
                if (!quiet) Log.Warn($"FFmpeg exited ({code}) after {lines} segments. " + string.Join(" | ", recent));
                break;
            case "stall":
                exitInfo = "Capture stalled, restarting";
                if (!quiet) Log.Warn($"FFmpeg stalled after {lines} segments. " + string.Join(" | ", recent));
                break;
            case "disk":
                exitInfo = "Low disk space";
                break;
            default:
                exitInfo = reason;
                break;
        }
        return lines >= 10;
    }

    private static string Explain(List<string> recent, int code)
    {
        string all = string.Join(" ", recent);
        if (all.Contains("887a0026", StringComparison.OrdinalIgnoreCase))
            return "Screen capture was interrupted by a display change";
        if (all.Contains("80070005", StringComparison.OrdinalIgnoreCase) || all.Contains("887a0022", StringComparison.OrdinalIgnoreCase))
            return "The screen cannot be captured right now (lock screen or administrator prompt)";
        string first = recent.FirstOrDefault(l => l.Contains("fail", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase))
                       ?? recent.FirstOrDefault() ?? "";
        return first.Length > 0 ? Shorten(first) : $"Capture stopped (code {code})";
    }

    private static string Shorten(string line)
    {
        int i = line.IndexOf("] ", StringComparison.Ordinal);
        if (line.StartsWith('[') && i > 0) line = line[(i + 2)..];
        return line.Length > 140 ? line[..140] + "…" : line;
    }

    private bool Ingest(CsvTail csv, string dir, double epoch, int sessionId, string key, ref int lines)
    {
        var fresh = csv.ReadNew();
        if (fresh.Count == 0) return false;
        lock (_lock)
        {
            foreach (var (file, start, end) in fresh)
            {
                int index = lines++;
                // Segment 0 holds a frozen frame while the encoder spins up, so it is never used.
                if (index == 0 || end <= start) continue;
                _segments.Add(new Segment(Path.Combine(dir, file), epoch + start, epoch + end, sessionId, key));
                _latestEnd = epoch + end;
            }
        }
        return true;
    }

    private void Janitor(int currentSession)
    {
        if (Volatile.Read(ref _leases) > 0) return;
        double cutoff = Clock.UtcNow() - RetainSeconds;
        List<Segment>? expired = null;
        HashSet<int>? live = null;
        long now = Environment.TickCount64;
        bool sweep = now - _lastDirSweep > 5000;
        lock (_lock)
        {
            int n = 0;
            while (n < _segments.Count && _segments[n].EndUtc < cutoff) n++;
            if (n > 0)
            {
                expired = _segments.GetRange(0, n);
                _segments.RemoveRange(0, n);
            }
            if (sweep) live = _segments.Select(s => s.SessionId).ToHashSet();
        }
        if (expired != null)
        {
            foreach (Segment s in expired)
            {
                try { File.Delete(s.Path); } catch { }
            }
        }
        if (!sweep || live == null) return;
        _lastDirSweep = now;
        try
        {
            foreach (string d in Directory.GetDirectories(_root, "s*"))
            {
                string name = Path.GetFileName(d);
                if (name.Length > 1 && int.TryParse(name.AsSpan(1), out int id) && id != currentSession && !live.Contains(id))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
        }
        catch { }
    }

    private bool DiskOk(out string why)
    {
        why = "";
        try
        {
            string? driveRoot = Path.GetPathRoot(_root);
            if (string.IsNullOrEmpty(driveRoot)) return true;
            long free = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (free < PauseBelowBytes) _lowDisk = true;
            else if (free > ResumeAboveBytes) _lowDisk = false;
            if (_lowDisk) why = $"Drive {driveRoot.TrimEnd('\\')} is almost full. Recording resumes once 2 GB is free.";
            return !_lowDisk;
        }
        catch
        {
            return true;
        }
    }

    private void DeleteBufferRoot()
    {
        try
        {
            // Only ever delete a folder literally named "buffer" inside our own data folder.
            if (Path.GetFileName(_root.TrimEnd('\\', '/')) != "buffer") return;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear the buffer folder: " + ex.Message);
        }
    }

    private static List<string> BuildArgs(RecorderConfig c, double epoch)
    {
        string e = epoch.ToString("F3", CultureInfo.InvariantCulture);
        string fps = c.Fps.ToString(CultureInfo.InvariantCulture);
        // Stamp every frame with wall-clock time (seconds since `epoch`), smoothed so capture jitter does not show.
        string pts = $"setpts='st(1,time(0)-{e}-T);st(0,if(eq(N,0),ld(1),ld(0)+(ld(1)-ld(0))*0.002));(T+ld(0))/TB'";
        string grab = $"ddagrab=output_idx={c.OutputIndex}:framerate={fps}:draw_mouse={(c.Cursor ? 1 : 0)}";

        string chain;
        string[] enc;
        if (c.Encoder == "h264_nvenc")
        {
            // Frames go from the screen to the encoder without leaving the GPU.
            chain = $"{grab},{pts}[v]";
            enc =
            [
                "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", c.Cq.ToString(), "-b:v", "0",
                "-maxrate", $"{c.MaxKbps}k", "-bufsize", $"{c.MaxKbps * 2}k", "-g", fps, "-bf", "0",
            ];
        }
        else
        {
            chain = $"{grab},{pts},hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v]";
            enc =
            [
                "-c:v", "libx264", "-preset", "veryfast", "-crf", Math.Max(16, c.Cq - 3).ToString(),
                "-maxrate", $"{c.MaxKbps}k", "-bufsize", $"{c.MaxKbps * 2}k",
                "-g", fps, "-keyint_min", fps, "-sc_threshold", "0", "-bf", "0",
            ];
        }

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-nostdin",
            "-filter_complex", chain, "-map", "[v]", "-fps_mode", "cfr",
        };
        a.AddRange(enc);
        a.AddRange(
        [
            "-f", "segment", "-segment_time", "1", "-segment_format", "mpegts", "-reset_timestamps", "1",
            "-segment_wrap", SegmentWrap.ToString(), "-segment_list", "seg.csv", "-segment_list_type", "csv", "seg_%05d.ts",
        ]);
        return a;
    }

    /// <summary>Reads lines FFmpeg appends to its segment list, while FFmpeg still has the file open.</summary>
    private sealed class CsvTail(string path)
    {
        private long _pos;
        private string _partial = "";

        public List<(string File, double Start, double End)> ReadNew()
        {
            var result = new List<(string, double, double)>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= _pos) return result;
                fs.Seek(_pos, SeekOrigin.Begin);
                var buf = new byte[fs.Length - _pos];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                _pos += read;
                string text = _partial + Encoding.UTF8.GetString(buf, 0, read);
                int last = text.LastIndexOf('\n');
                if (last < 0)
                {
                    _partial = text;
                    return result;
                }
                _partial = text[(last + 1)..];
                foreach (string raw in text[..last].Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] p = line.Split(',');
                    if (p.Length >= 3
                        && double.TryParse(p[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        && double.TryParse(p[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double e))
                        result.Add((string.Join(",", p[..^2]), s, e));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }
    }
}
