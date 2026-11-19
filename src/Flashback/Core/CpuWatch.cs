using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Flashback.Core;

/// <summary>
/// Watches the CPU use of Flashback's own threads. Windows runs one thread inside Flashback for every per-app capture
/// (they start in AudioSes.dll). After the PC wakes from a long sleep those threads can get stuck copying memory flat
/// out, together using every core. They are not Flashback's threads, so a capture cannot see this by itself; this
/// catches it and has the app captures reopened. It also writes to the log which threads were busy whenever
/// Flashback uses a lot of CPU for a whole minute, so another runaway can be told apart at once.
/// </summary>
public sealed class CpuWatch : IDisposable
{
    internal static int IntervalMs = 5000;
    /// <summary>Share of one core Windows' capture threads may use between them; normally a fraction of a percent.</summary>
    internal static double MaxCaptureShare = 0.25;
    /// <summary>Cores Flashback may use, averaged over a minute, before the busiest threads are logged.</summary>
    internal static double ReportCores = 1.0;
    private const int ReportWindowMs = 60_000;
    private const int ReportEveryMs = 10 * 60_000;
    private const string CaptureModule = "AudioSes.dll";

    private readonly AppAudioCapture _apps;
    private readonly object _lock = new();
    private Timer? _timer;
    private Dictionary<int, ThreadCpu.Entry>? _last;
    private long _lastTick;
    private readonly Dictionary<string, long> _window = new(StringComparer.OrdinalIgnoreCase);
    private long _windowStart;
    private long _lastReport = long.MinValue / 2;

    public CpuWatch(AppAudioCapture apps) => _apps = apps;

    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => Check(), null, IntervalMs, IntervalMs);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    internal void Check()
    {
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            long tick = Environment.TickCount64;
            List<ThreadCpu.Entry> now = ThreadCpu.Sample();
            Dictionary<int, ThreadCpu.Entry>? last = _last;
            _last = now.ToDictionary(e => e.Id);
            if (last == null || tick - _lastTick <= 0)
            {
                _lastTick = _windowStart = tick;
                return;
            }
            double ms = tick - _lastTick;
            _lastTick = tick;

            long captureCpu = 0;
            foreach (ThreadCpu.Entry e in now)
            {
                // A thread id that now belongs to a different thread counts from zero.
                long used = last.TryGetValue(e.Id, out ThreadCpu.Entry before) && before.Owner == e.Owner ? e.Cpu - before.Cpu : e.Cpu;
                if (used <= 0) continue;
                if (e.Owner.Equals(CaptureModule, StringComparison.OrdinalIgnoreCase)) captureCpu += used;
                _window[e.Owner] = _window.GetValueOrDefault(e.Owner) + used;
            }
            _apps.CheckWindowsCaptureCpu(captureCpu / 10_000.0 / ms, CountOwner(now, CaptureModule));

            if (tick - _windowStart < ReportWindowMs) return;
            double windowMs = tick - _windowStart;
            double cores = _window.Values.Sum() / 10_000.0 / windowMs;
            if (cores >= ReportCores && tick - _lastReport >= ReportEveryMs)
            {
                _lastReport = tick;
                string busiest = string.Join(", ", _window.OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => $"{kv.Key}{(CountOwner(now, kv.Key) is > 1 and var n ? $" ({n} threads)" : "")} {kv.Value / 10_000.0 / windowMs:F2}"));
                Log.Warn($"Flashback kept {cores:F2} CPU cores busy on average over the last minute. Busiest threads, in cores: {busiest}");
            }
            _window.Clear();
            _windowStart = tick;
        }
        catch (Exception ex)
        {
            Log.Error("CPU check failed", ex);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private static int CountOwner(List<ThreadCpu.Entry> threads, string owner) =>
        threads.Count(e => e.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// CPU time of each thread in Flashback's process, labelled with the name a Flashback thread gave itself or, for
/// any other thread, the DLL it started in (Windows' audio threads start in AudioSes.dll).
/// </summary>
public static class ThreadCpu
{
    public readonly record struct Entry(int Id, string Owner, long Cpu);

    private const uint SnapThread = 0x4;
    private const uint QueryInformation = 0x40;
    private const int Win32StartAddress = 9;
    private const uint FromAddressUnchangedRefcount = 0x4 | 0x2;

    private static readonly ConcurrentDictionary<int, string> Names = new();
    private static readonly ConcurrentDictionary<IntPtr, string> Modules = new();

    /// <summary>Names the calling thread in <see cref="Sample"/> until <see cref="Forget"/>.</summary>
    public static void Name(string name) => Names[(int)GetCurrentThreadId()] = name;

    public static void Forget() => Names.TryRemove((int)GetCurrentThreadId(), out _);

    public static List<Entry> Sample()
    {
        var result = new List<Entry>();
        uint self = (uint)Environment.ProcessId;
        IntPtr snap = CreateToolhelp32Snapshot(SnapThread, 0);
        if (snap == new IntPtr(-1)) return result;
        try
        {
            var e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            for (bool ok = Thread32First(snap, ref e); ok; ok = Thread32Next(snap, ref e))
            {
                if (e.th32OwnerProcessID != self) continue;
                IntPtr h = OpenThread(QueryInformation, false, e.th32ThreadID);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!GetThreadTimes(h, out _, out _, out long kernel, out long user)) continue;
                    int id = (int)e.th32ThreadID;
                    string owner = Names.TryGetValue(id, out string? name) ? name : StartModule(h);
                    result.Add(new Entry(id, owner, kernel + user));
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    private static string StartModule(IntPtr thread)
    {
        if (NtQueryInformationThread(thread, Win32StartAddress, out IntPtr start, IntPtr.Size, IntPtr.Zero) != 0) return "?";
        if (!GetModuleHandleExW(FromAddressUnchangedRefcount, start, out IntPtr module)) return "?";
        return Modules.GetOrAdd(module, m =>
        {
            var sb = new StringBuilder(520);
            uint n = GetModuleFileNameW(m, sb, (uint)sb.Capacity);
            return n == 0 ? "?" : Path.GetFileName(sb.ToString());
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out IntPtr info, int length, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
