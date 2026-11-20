using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Flashback.Core;

/// <summary>
/// One finished piece of video in the rolling buffer, normally about a second long. Pieces of one session follow each
/// other without gaps: on a still screen a piece simply lasts until the screen changes, its last picture held.
/// </summary>
public sealed record Segment(string Path, double StartUtc, double EndUtc, int SessionId, string ParamsKey);

public enum RecorderState { Stopped, Starting, Recording, Retrying, Paused, Error }

/// <summary>How the screen is captured.</summary>
public enum CaptureMethod
{
    /// <summary>
    /// Windows Graphics Capture (FFmpeg's gfxcapture). Every frame Windows draws arrives stamped with the moment it was
    /// drawn, so each 1/60 s of the video gets exactly the right picture. Nothing arrives while the screen is still.
    /// </summary>
    GraphicsCapture,

    /// <summary>
    /// Desktop Duplication (FFmpeg's ddagrab), the fallback. It times its frames with Sleep, which Windows rounds to
    /// 15.6 ms steps for FFmpeg, so frames are taken 4 to 40 ms apart and the video judders.
    /// </summary>
    DesktopDuplication,
}

public sealed class RecorderConfig
{
    public int OutputIndex;
    public long Monitor;
    public int Width;
    public int Height;
    public int Fps;
    public int Cq;
    public int MaxKbps;
    public bool Cursor;
    public string Encoder = "h264_nvenc";
    public CaptureMethod Capture = CaptureMethod.GraphicsCapture;

    /// <summary>For tests only: gfxcapture options that replace the main monitor, e.g. a window to capture.</summary>
    internal string? TestSource;

    /// <summary>Segments can only be joined into one clip when this matches.</summary>
    public string Key => $"{(Capture == CaptureMethod.GraphicsCapture ? "wgc" : "dda")}/{Encoder}/{Width}x{Height}@{Fps}/q{Cq}/{MaxKbps}";
}

/// <summary>
/// Keeps one FFmpeg process capturing the main monitor into one-second pieces, and keeps it alive: if FFmpeg exits it
/// is restarted automatically.
/// </summary>
public sealed class VideoRecorder
{
    // FFmpeg reuses segment file names after this many: a hard cap on disk use even if cleanup were to fail.
    // It must exceed the longest retention (315 s) plus the longest possible clip save (360 s).
    private const int SegmentWrap = 720;
    private const long PauseBelowBytes = 1536L * 1024 * 1024;
    private const long ResumeAboveBytes = 2048L * 1024 * 1024;

    /// <summary>Graphics Capture takes every frame Windows draws, up to this many a second (the main monitor runs at 240 Hz).</summary>
    private const int CaptureMaxFps = 250;

    /// <summary>
    /// Graphics Capture sends nothing while the screen is still, so a long wait for a piece is not a fault. After this
    /// long the capture is restarted anyway, in case it stopped without saying so.
    /// </summary>
    private const long IdleRestartMs = 5 * 60 * 1000;

    /// <summary>After this many Graphics Capture sessions in a row fail to start, Desktop Duplication is used for a while.</summary>
    private const int FallbackAfterFailures = 3;
    private const long FallbackMs = 30 * 60 * 1000;

    /// <summary>The gap a capture restart leaves. A clip holds the picture across it, as for a still screen.</summary>
    private const double RestartAllowance = 3.0;

    /// <summary>FFmpeg's MPEG-TS muxer adds this to every timestamp (twice its default 0.7 s mux delay).</summary>
    private const double TsOffset = 1.4;

    private readonly string _ffmpeg;
    private readonly string _root;
    private readonly Func<RecorderConfig?> _configProvider;
    private readonly JobObject _job;

    private readonly object _lock = new();
    private readonly List<Segment> _segments = new();
    private readonly List<(double Start, double End)> _runs = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _restart;
    private int _leases;
    private double _latestEnd;
    private double _runStart = double.NaN;
    private long _lastPieceTick;
    private bool _lowDisk;
    private long _lastDirSweep;
    private int _wgcFailures;
    private long _fallbackUntil;
    private int _idleRestarts;
    private CaptureMethod? _loggedCapture;

    // The session FFmpeg is writing, to find the piece it has open (see OpenPiece).
    private string? _sessionDir;
    private double _sessionEpoch;
    private int _sessionId;
    private int _listed;
    private double _lastListedEnd = double.NaN;

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

    /// <summary>Milliseconds since FFmpeg last finished a piece (or since the session started).</summary>
    public long MsSinceLastPiece => Environment.TickCount64 - Volatile.Read(ref _lastPieceTick);

    /// <summary>Seconds of video a clip could cover right now.</summary>
    public int BufferedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_segments.Count == 0) return 0;
                int i = _segments.Count - 1;
                double end = _segments[i].EndUtc, now = Clock.UtcNow();
                if (_state == RecorderState.Recording && CoveredLocked(end, now)) end = now;
                double start = _segments[i].StartUtc;
                for (i--; i >= 0 && CanJoinLocked(_segments[i], _segments[i + 1]); i--) start = _segments[i].StartUtc;
                return (int)Math.Max(0, end - start);
            }
        }
    }

    public List<Segment> Snapshot()
    {
        lock (_lock) return new List<Segment>(_segments);
    }

    /// <summary>
    /// True if FFmpeg was running for all of [<paramref name="from"/>, <paramref name="to"/>], allowing for restarts of
    /// a few seconds. With Graphics Capture a stretch without new pieces then means the screen did not change.
    /// </summary>
    public bool WasRecording(double from, double to)
    {
        lock (_lock) return CoveredLocked(from, to);
    }

    /// <summary>
    /// Whether a clip can run from <paramref name="earlier"/> into <paramref name="later"/>, holding the earlier
    /// piece's last picture in between: across a gap of up to 5 s (a capture restart), or any gap while FFmpeg was
    /// running (a still screen).
    /// </summary>
    public bool CanJoin(Segment earlier, Segment later)
    {
        lock (_lock) return CanJoinLocked(earlier, later);
    }

    private bool CanJoinLocked(Segment earlier, Segment later)
    {
        if (earlier.ParamsKey != later.ParamsKey) return false;
        double gap = later.StartUtc - earlier.EndUtc;
        if (gap < -0.05) return false;
        return gap <= 5.0 || CoveredLocked(earlier.EndUtc, later.StartUtc);
    }

    private bool CoveredLocked(double from, double to)
    {
        var runs = new List<(double Start, double End)>(_runs);
        if (!double.IsNaN(_runStart)) runs.Add((_runStart, Clock.UtcNow()));
        double t = from;
        foreach (var (start, end) in runs.OrderBy(r => r.Start))
        {
            if (t >= to) break;
            if (end <= t) continue;
            if (start > t + RestartAllowance) return false;
            t = Math.Max(t, end);
        }
        return t >= to - 0.05;
    }

    /// <summary>While a lease is held no buffered segment is deleted.</summary>
    public void BeginRead() => Interlocked.Increment(ref _leases);
    public void EndRead() => Interlocked.Decrement(ref _leases);

    /// <summary>
    /// The piece FFmpeg is still writing, or null. With Graphics Capture a piece is only finished when the screen
    /// changes, so on a still screen this piece holds the latest picture (and the last moments before the screen went
    /// still). FFmpeg writes every frame to disk as it goes, so the file can be read while it is open. Its end is not
    /// known yet: it is given as its start, and a clip holds its picture up to the press.
    /// </summary>
    public Segment? OpenPiece()
    {
        string? dir;
        double epoch, lastEnd;
        int session, listed;
        string key;
        lock (_lock)
        {
            if (_state != RecorderState.Recording || _active is not { Capture: CaptureMethod.GraphicsCapture } cfg || _sessionDir == null) return null;
            (dir, epoch, session, listed, lastEnd, key) = (_sessionDir, _sessionEpoch, _sessionId, _listed, _lastListedEnd, cfg.Key);
        }
        try
        {
            string path = Path.Combine(dir, $"seg_{listed % SegmentWrap:D5}.ts");
            if (!File.Exists(path) || new FileInfo(path).Length < 188) return null;
            // Pieces follow each other exactly, so it starts where the last listed one ended (the first one: read it).
            double start = listed == 0 ? (TsFirstPts(path) ?? double.NaN) - TsOffset : lastEnd;
            if (double.IsNaN(start) || start < 0) return null;
            return new Segment(path, epoch + start, epoch + start, session, key);
        }
        catch (IOException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// How a session ended. <see cref="Immediate"/>: start the next one right away, keeping the state.
    /// <see cref="StartFailed"/>: FFmpeg exited before finishing a single piece.
    /// </summary>
    private readonly record struct SessionEnd(bool Healthy, string Info, bool Immediate, bool StartFailed, bool MissingFilter);

    private void Run()
    {
        int sessionId = 0, failures = 0;
        bool keepState = false;
        DeleteBufferRoot();
        try { Directory.CreateDirectory(_root); }
        catch (Exception ex) { Log.Error("Cannot create buffer folder", ex); }

        while (!_stop)
        {
            if (!DiskOk(out string why))
            {
                SetState(RecorderState.Paused, why);
                keepState = false;
                _wake.WaitOne(5000);
                continue;
            }

            RecorderConfig? cfg = null;
            try { cfg = _configProvider(); }
            catch (Exception ex) { Log.Error("Could not build the recorder configuration", ex); }
            if (cfg == null)
            {
                SetState(RecorderState.Error, "Recorder could not be configured");
                keepState = false;
                _wake.WaitOne(3000);
                continue;
            }
            bool fallback = cfg.Capture == CaptureMethod.GraphicsCapture && Environment.TickCount64 < _fallbackUntil;
            if (fallback) cfg.Capture = CaptureMethod.DesktopDuplication;
            if (_loggedCapture != cfg.Capture)
            {
                _loggedCapture = cfg.Capture;
                Log.Info("Recorder: capturing with " + (cfg.Capture == CaptureMethod.GraphicsCapture ? "Windows Graphics Capture" : "Desktop Duplication"));
            }

            _restart = false;
            sessionId++;
            SessionEnd end;
            try
            {
                string dir = Path.Combine(_root, $"s{sessionId:D5}");
                Directory.CreateDirectory(dir);
                end = RunSession(cfg, sessionId, dir, failures, keepState, fallback);
            }
            catch (Exception ex)
            {
                end = new SessionEnd(false, ex.Message, false, true, false);
                Log.Error("Recorder session failed", ex);
            }

            if (cfg.Capture == CaptureMethod.GraphicsCapture) NoteGraphicsCapture(end);

            keepState = false;
            if (_stop) break;
            if (_restart)
            {
                _restart = false;
                failures = 0;
                continue;
            }
            if (end.Immediate)
            {
                failures = 0;
                keepState = true;
                continue;
            }
            failures = end.Healthy ? 0 : failures + 1;
            SetState(RecorderState.Retrying, end.Info);
            if (!end.Healthy)
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

    /// <summary>Falls back to Desktop Duplication for a while if Graphics Capture keeps failing to start.</summary>
    private void NoteGraphicsCapture(SessionEnd end)
    {
        if (!end.StartFailed)
        {
            _wgcFailures = 0;
            return;
        }
        _wgcFailures++;
        if (!end.MissingFilter && _wgcFailures < FallbackAfterFailures) return;
        _wgcFailures = 0;
        _fallbackUntil = Environment.TickCount64 + FallbackMs;
        Log.Warn(end.MissingFilter
            ? "This FFmpeg has no Windows Graphics Capture (gfxcapture); using Desktop Duplication for 30 minutes"
            : $"Windows Graphics Capture failed to start {FallbackAfterFailures} times ({end.Info}); using Desktop Duplication for 30 minutes");
    }

    private SessionEnd RunSession(RecorderConfig cfg, int sessionId, string dir, int priorFailures, bool keepState, bool fallback)
    {
        bool wgc = cfg.Capture == CaptureMethod.GraphicsCapture;
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

        if (!keepState) SetState(RecorderState.Starting, "");
        proc.Start();
        _job.Add(proc);
        try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        proc.BeginErrorReadLine();
        lock (_lock)
        {
            _active = cfg;
            _runStart = Clock.UtcNow();
            (_sessionDir, _sessionEpoch, _sessionId, _listed, _lastListedEnd) = (dir, epoch, sessionId, 0, double.NaN);
        }

        var csv = new CsvTail(Path.Combine(dir, "seg.csv"));
        long started = Environment.TickCount64, lastProgress = started, lastDisk = started;
        Volatile.Write(ref _lastPieceTick, started);
        int lines = 0;
        Segment? open = null;
        string reason;

        while (true)
        {
            if (_stop) { reason = "stop"; break; }
            if (_restart) { reason = "restart"; break; }

            if (Ingest(csv, dir, epoch, sessionId, cfg, ref lines))
            {
                lastProgress = Environment.TickCount64;
                _idleRestarts = 0;
                SetState(RecorderState.Recording, "");
            }
            long now = Environment.TickCount64;
            if (proc.HasExited) { reason = "exit"; break; }
            if (wgc)
            {
                // A still screen sends no frames, so silence is normal. FFmpeg failing to start exits within a
                // second or two; once it has been up for a few seconds it is recording.
                if (now - started > 3000) SetState(RecorderState.Recording, "");
                if (now - lastProgress > IdleRestartMs)
                {
                    // The open piece holds the still picture: keep it when FFmpeg is stopped.
                    open = OpenPiece();
                    reason = "idle";
                    break;
                }
            }
            // Desktop Duplication repeats the last frame on a still screen, so silence means capture is stuck
            // (sleep, display change, driver reset).
            else if (now - lastProgress > (lines > 0 ? 6000 : 12000)) { reason = "stall"; break; }
            if (fallback && now >= _fallbackUntil) { reason = "retry"; break; }
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
        Ingest(csv, dir, epoch, sessionId, cfg, ref lines);
        lock (_lock)
        {
            double ended = Clock.UtcNow();
            // Nothing was being written to it (the screen was still), so the file is complete.
            if (open != null && _segments.All(s => s.Path != open.Path))
            {
                _segments.Add(open with { EndUtc = ended });
                _latestEnd = ended;
            }
            if (!double.IsNaN(_runStart)) _runs.Add((_runStart, ended));
            _runStart = double.NaN;
            _sessionDir = null;
            double old = ended - 2 * 3600;
            _runs.RemoveAll(r => r.End < old);
        }

        // Only what FFmpeg said in its final moments explains why it stopped.
        List<string> recent;
        lock (tail)
        {
            long newest = tail.Count > 0 ? tail.Last().Tick : 0;
            recent = tail.Where(t => newest - t.Tick < 3000).Select(t => t.Line).ToList();
        }
        // While the screen stays unavailable (lock screen) the same failure repeats: keep it out of the log.
        bool quiet = lines < 10 && priorFailures >= 3 && (priorFailures + 1) % 20 != 0;
        bool missingFilter = recent.Any(l => l.Contains("No such filter", StringComparison.OrdinalIgnoreCase));
        string exitInfo;
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
            case "idle":
                // On a still screen this repeats every 5 minutes: log the first and then one an hour.
                if (_idleRestarts++ % 12 == 0)
                    Log.Info("Recorder: no new video for 5 minutes (the screen may be still), restarting capture");
                return new SessionEnd(true, "", true, false, false);
            case "retry":
                Log.Info("Recorder: trying Windows Graphics Capture again");
                return new SessionEnd(true, "", true, false, false);
            case "disk":
                exitInfo = "Low disk space";
                break;
            default:
                exitInfo = reason;
                break;
        }
        return new SessionEnd(lines >= 10, exitInfo, false, reason == "exit" && lines == 0, missingFilter);
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

    private bool Ingest(CsvTail csv, string dir, double epoch, int sessionId, RecorderConfig cfg, ref int lines)
    {
        var fresh = csv.ReadNew();
        if (fresh.Count == 0) return false;
        Volatile.Write(ref _lastPieceTick, Environment.TickCount64);
        var pieces = new List<Segment>();
        foreach (var (file, listedStart, end) in fresh)
        {
            int index = lines++;
            string path = Path.Combine(dir, file);
            double start = listedStart;
            if (index == 0)
            {
                // The list always gives the first piece a start of 0. With Desktop Duplication its first frame is
                // frozen while the encoder starts, so it is not used. Graphics Capture's first frame is real (and on a
                // still screen it is the picture): its time is read from the file instead.
                if (cfg.Capture != CaptureMethod.GraphicsCapture || TsFirstPts(path) is not double first) continue;
                start = first - TsOffset;
                if (start < 0 || start >= end) continue;
            }
            if (end <= start) continue;
            pieces.Add(new Segment(path, epoch + start, epoch + end, sessionId, cfg.Key));
        }
        lock (_lock)
        {
            if (sessionId == _sessionId)
            {
                _listed = lines;
                _lastListedEnd = fresh[^1].End;
            }
            // A piece kept from a stopped session (see OpenPiece) may already be in the list.
            pieces.RemoveAll(p => _segments.Any(s => s.Path == p.Path));
            if (pieces.Count == 0) return true;
            _segments.AddRange(pieces);
            _latestEnd = pieces[^1].EndUtc;
        }
        return true;
    }

    /// <summary>The first video timestamp in an MPEG-TS file, in seconds, or null if it cannot be read.</summary>
    internal static double? TsFirstPts(string path)
    {
        try
        {
            byte[] b = new byte[188 * 512];
            int n = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                while (n < b.Length)
                {
                    int r = fs.Read(b, n, b.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
            }
            for (int p = 0; p + 188 <= n; p += 188)
            {
                if (b[p] != 0x47) return null;
                bool unitStart = (b[p + 1] & 0x40) != 0;
                int adaptation = (b[p + 3] >> 4) & 3;
                int i = p + 4;
                if (adaptation == 2 || !unitStart) continue;
                if (adaptation == 3) i += 1 + b[p + 4];
                if (i + 14 > p + 188) continue;
                // A PES packet of a video stream (0xE0-0xEF) that carries a PTS.
                if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1 || b[i + 3] < 0xE0 || b[i + 3] > 0xEF) continue;
                if ((b[i + 7] & 0x80) == 0) continue;
                long pts = ((long)((b[i + 9] >> 1) & 7) << 30) | ((long)b[i + 10] << 22) | ((long)(b[i + 11] >> 1) << 15)
                           | ((long)b[i + 12] << 7) | ((long)b[i + 13] >> 1);
                return pts / 90000.0;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
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
        bool wgc = c.Capture == CaptureMethod.GraphicsCapture;

        string source;
        string[] keyframes;
        if (wgc)
        {
            // Each frame gets the wall-clock time it was drawn (seconds since `epoch`): Windows' timestamp plus a smoothed
            // offset to the wall clock, re-anchored if the two drift half a second apart (as after a long still). The
            // time is then moved back to the start of its 1/fps slot, and -fps_mode vfr keeps only the first frame in
            // each slot, so every slot shows the picture from its start. The frame rate is left unset so the encoder
            // takes it from the 1/fps time base instead of from the capture rate.
            source = $"gfxcapture={c.TestSource ?? $"hmonitor={c.Monitor}"}:max_framerate={CaptureMaxFps}:capture_cursor={(c.Cursor ? 1 : 0)},"
                     + "setpts=strip_fps=1:expr='"
                     + $"st(1,time(0)-{e}-T);st(0,if(eq(N,0)+gt(abs(ld(1)-ld(0)),0.5),ld(1),ld(0)+(ld(1)-ld(0))*0.002));"
                     + $"floor((T+ld(0))*{fps})/({fps}*TB)',settb=1/{fps}";
            // Frames only arrive when the screen changes, so keyframes (and with them the one-second pieces) are
            // placed by time.
            keyframes = ["-g", (c.Fps * 10).ToString(), "-forced-idr", "1", "-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t,prev_forced_t+1))"];
        }
        else
        {
            // Stamp every frame with wall-clock time (seconds since `epoch`), smoothed so capture jitter does not show.
            source = $"ddagrab=output_idx={c.OutputIndex}:framerate={fps}:draw_mouse={(c.Cursor ? 1 : 0)},"
                     + $"setpts='st(1,time(0)-{e}-T);st(0,if(eq(N,0),ld(1),ld(0)+(ld(1)-ld(0))*0.002));(T+ld(0))/TB'";
            keyframes = c.Encoder == "h264_nvenc" ? ["-g", fps] : ["-g", fps, "-keyint_min", fps];
        }

        string chain;
        var enc = new List<string>();
        if (c.Encoder == "h264_nvenc")
        {
            // Frames go from the screen to the encoder without leaving the GPU.
            chain = $"{source}[v]";
            enc.AddRange(
            [
                "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", c.Cq.ToString(), "-b:v", "0",
                "-maxrate", $"{c.MaxKbps}k", "-bufsize", $"{c.MaxKbps * 2}k", "-bf", "0",
            ]);
        }
        else
        {
            chain = $"{source},hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p[v]";
            enc.AddRange(
            [
                "-c:v", "libx264", "-preset", "veryfast", "-crf", Math.Max(16, c.Cq - 3).ToString(),
                "-maxrate", $"{c.MaxKbps}k", "-bufsize", $"{c.MaxKbps * 2}k", "-sc_threshold", "0", "-bf", "0",
            ]);
        }
        enc.AddRange(keyframes);
        // Frames only come when the screen changes, so the encoder must not hold any back waiting for more: on a
        // still screen the last ones would never reach the buffer.
        if (wgc) enc.AddRange(c.Encoder == "h264_nvenc" ? ["-delay", "0"] : ["-tune", "zerolatency"]);

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-nostdin",
            "-filter_complex", chain, "-map", "[v]", "-fps_mode", wgc ? "vfr" : "cfr",
        };
        a.AddRange(enc);
        a.AddRange(["-f", "segment", "-segment_time", "1", "-segment_format", "mpegts", "-reset_timestamps", "1"]);
        // Every frame goes to disk as soon as it is encoded, so the piece still open on a still screen can be read.
        if (wgc) a.AddRange(["-segment_format_options", "flush_packets=1"]);
        a.AddRange(["-segment_wrap", SegmentWrap.ToString(), "-segment_list", "seg.csv", "-segment_list_type", "csv", "seg_%05d.ts"]);
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
