using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Flashback.Core;

/// <summary>The audio one app played, as kept for clips. An app that restarted shows up once per process, under the same key.</summary>
/// <param name="Own">Flashback's own sound (clip playback in the player, the hotkey sound). It never becomes a track, but
/// it is in the desktop capture, so the check that the app tracks account for the desktop sound has to know it.</param>
public sealed record AppAudioRing(string Key, string Name, AudioRing Ring, bool Own = false);

/// <summary>
/// Gives every app that plays sound on the default output its own capture, so clips can carry one audio track per app
/// (the game, Discord, a browser) next to the full desktop mix.
///
/// The apps are the processes in the Windows volume mixer. Windows can only capture a process together with the
/// processes it started, so an app whose children are other apps playing sound (Steam, which starts the games, or a
/// game launcher) is left out instead of capturing the game twice; its sound is still in the desktop mix. Several
/// processes of one app (Discord, Chrome) are merged into one track when the clip is saved.
/// </summary>
public sealed class AppAudioCapture
{
    private const int PollMs = 1000;
    private const int MaxDepth = 64;

    private sealed class Tracked(AppAudioRing app, AudioSource source)
    {
        public readonly AppAudioRing App = app;
        public readonly AudioSource Source = source;
        public double StoppedUtc = double.NaN;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Tracked> _active = new();
    private readonly List<Tracked> _retired = new();
    private readonly Dictionary<(int Pid, long Created), string> _names = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly int _ownPid = Environment.ProcessId;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _retainSeconds = 50;
    private string _lastError = "";

    /// <summary>Process loopback exists from Windows 10 version 2004 (build 19041).</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public bool IsRunning => _thread != null;

    /// <summary>Seconds of history to keep, also for apps that have closed in the meantime.</summary>
    public int RetainSeconds
    {
        get => _retainSeconds;
        set
        {
            _retainSeconds = value;
            lock (_lock)
            {
                foreach (Tracked t in _active.Values) t.App.Ring.RetainSeconds = value;
                foreach (Tracked t in _retired) t.App.Ring.RetainSeconds = value;
            }
        }
    }

    public void Start()
    {
        if (_thread != null || !Supported) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "App audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null) return;
        _stop = true;
        _wake.Set();
        _thread.Join(5000);
        _thread = null;
        lock (_lock)
        {
            foreach (Tracked t in _active.Values) t.Source.Stop();
            _active.Clear();
            _retired.Clear();
        }
    }

    /// <summary>Every app's audio currently kept, including apps that closed within <see cref="RetainSeconds"/>.</summary>
    public List<AppAudioRing> Snapshot()
    {
        lock (_lock) return _active.Values.Concat(_retired).Select(t => t.App).ToList();
    }

    /// <summary>Names of the apps that played sound in the last <paramref name="seconds"/>, for the settings page.</summary>
    public List<string> RecentlyHeard(double seconds)
    {
        double since = Clock.UtcNow() - seconds;
        lock (_lock)
        {
            return _active.Values.Concat(_retired).Where(t => !t.App.Own && t.App.Ring.LastUtc >= since)
                .Select(t => t.App.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void Run()
    {
        Log.Info("App audio: separate app tracks on");
        MMDeviceEnumerator? enumerator = null;
        while (!_stop)
        {
            try
            {
                enumerator ??= new MMDeviceEnumerator();
                Poll(enumerator);
                _lastError = "";
            }
            catch (Exception ex)
            {
                string msg = $"App audio: could not list the apps playing sound ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message})";
                if (msg != _lastError)
                {
                    _lastError = msg;
                    Log.Warn(msg);
                }
                enumerator?.Dispose();
                enumerator = null;
            }
            _wake.WaitOne(PollMs);
        }
        enumerator?.Dispose();
    }

    private void Poll(MMDeviceEnumerator enumerator)
    {
        Dictionary<int, string> sessions = ListSessions(enumerator);
        Dictionary<int, ProcessEntry> processes = ProcessEntry.Snapshot();
        var created = new Dictionary<int, long>();
        long Created(int pid)
        {
            if (!created.TryGetValue(pid, out long t)) created[pid] = t = ProcessEntry.CreationTime(pid);
            return t;
        }

        // Only processes that are still running. Flashback itself is captured too, but only to explain its own sound.
        var candidates = sessions.Keys.Where(pid => processes.ContainsKey(pid)).ToList();

        bool IsDescendant(int child, int ancestor)
        {
            int pid = child;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                if (!processes.TryGetValue(pid, out ProcessEntry? p)) return false;
                int parent = p.ParentId;
                if (parent == 0 || parent == pid || !processes.ContainsKey(parent)) return false;
                // A parent id can belong to a newer process that reused the number; a real parent is older.
                long tp = Created(parent), tc = Created(pid);
                if (tp != 0 && tc != 0 && tp > tc) return false;
                if (parent == ancestor) return true;
                pid = parent;
            }
            return false;
        }

        string Exe(int pid) => processes[pid].ExeName;

        // Capturing a process captures everything it started. One that started another app playing sound (or
        // Flashback itself, whose own sounds must stay out of app tracks) gets no track of its own.
        var conflicted = new HashSet<int>(candidates.Where(p => IsDescendant(_ownPid, p) ||
            candidates.Any(r => r != p && !Exe(r).Equals(Exe(p), StringComparison.OrdinalIgnoreCase) && IsDescendant(r, p))));
        var targets = new HashSet<int>(candidates.Where(p => !conflicted.Contains(p) &&
            !candidates.Any(q => q != p && !conflicted.Contains(q) && Exe(q).Equals(Exe(p), StringComparison.OrdinalIgnoreCase) && IsDescendant(p, q))));

        double now = Clock.UtcNow();
        List<AudioSource> toStop = [];
        lock (_lock)
        {
            foreach (int pid in _active.Keys.Where(pid => !targets.Contains(pid)).ToList())
            {
                Tracked t = _active[pid];
                _active.Remove(pid);
                toStop.Add(t.Source);
                if (conflicted.Contains(pid))
                {
                    // It started another app that plays sound; its capture would contain that app too. Drop what it has.
                    Log.Info($"App audio: no separate track for \"{t.App.Name}\" (process {pid}) because it started {DescribeChildren(pid)}");
                    continue;
                }
                t.StoppedUtc = now;
                _retired.Add(t);
            }
            foreach (int pid in targets.Where(pid => !_active.ContainsKey(pid)))
            {
                string exe = Exe(pid);
                bool own = pid == _ownPid;
                string name = own ? "Flashback" : FriendlyName(pid, Created(pid), exe, sessions[pid]);
                var source = new AudioSource(pid, name);
                var app = new AppAudioRing(own ? " flashback" : exe.ToLowerInvariant(), name, source.Ring, own);
                app.Ring.RetainSeconds = _retainSeconds;
                _active[pid] = new Tracked(app, source);
                source.Start();
                Log.Info(own ? "App audio: capturing Flashback's own sound (never a track; only to account for it in the desktop sound)"
                    : $"App audio: capturing \"{name}\" ({exe}, process {pid})");
            }
            _retired.RemoveAll(t => now - t.StoppedUtc > _retainSeconds + 5 || t.App.Ring.LastUtc < now - _retainSeconds);
            foreach (var key in _names.Keys.Where(k => !processes.ContainsKey(k.Pid)).ToList()) _names.Remove(key);
        }
        foreach (AudioSource s in toStop) s.Stop(keepAudio: true);

        string DescribeChildren(int pid) => string.Join(", ",
            candidates.Where(r => r != pid && IsDescendant(r, pid)).Select(Exe).Append(IsDescendant(_ownPid, pid) ? "Flashback" : null)
                .OfType<string>().Distinct());
    }

    /// <summary>Process id and display name of every audio session on the default output.</summary>
    private static Dictionary<int, string> ListSessions(MMDeviceEnumerator enumerator)
    {
        var result = new Dictionary<int, string>();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        SessionCollection sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using AudioSessionControl session = sessions[i];
            try
            {
                if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                int pid = (int)session.GetProcessID;
                if (pid <= 4) continue;
                string display = session.DisplayName ?? "";
                if (!result.TryGetValue(pid, out string? existing) || existing.Length == 0) result[pid] = display;
            }
            catch
            {
                // A session can disappear while it is being read.
            }
        }
        return result;
    }

    private string FriendlyName(int pid, long created, string exe, string sessionName)
    {
        if (_names.TryGetValue((pid, created), out string? cached)) return cached;
        string name = sessionName.Trim();
        if (name.Length == 0 || name.StartsWith('@'))
        {
            name = "";
            try
            {
                string? path = ProcessEntry.ImagePath(pid);
                if (path != null)
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    name = (info.FileDescription ?? "").Trim();
                    if (name.Length == 0 || name.Length > 40 || name.Contains('\\')) name = (info.ProductName ?? "").Trim();
                    if (name.Length > 40) name = "";
                }
            }
            catch
            {
                // Protected processes do not let us read their file; the exe name will do.
            }
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exe);
        }
        _names[(pid, created)] = name;
        return name;
    }

    /// <summary>One running process, from a ToolHelp snapshot (needs no access to the process itself).</summary>
    private sealed record ProcessEntry(int ParentId, string ExeName)
    {
        private const uint SnapProcess = 0x2;
        private const uint QueryLimitedInformation = 0x1000;

        public static Dictionary<int, ProcessEntry> Snapshot()
        {
            var result = new Dictionary<int, ProcessEntry>();
            IntPtr snap = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snap == new IntPtr(-1)) throw new InvalidOperationException("Process snapshot failed: " + Marshal.GetLastWin32Error());
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    result[(int)e.th32ProcessID] = new ProcessEntry((int)e.th32ParentProcessID, e.szExeFile);
            }
            finally
            {
                CloseHandle(snap);
            }
            return result;
        }

        /// <summary>When the process started (FILETIME), or 0 if Windows will not say.</summary>
        public static long CreationTime(int pid)
        {
            IntPtr h = OpenProcess(QueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                return GetProcessTimes(h, out long created, out _, out _, out _) ? created : 0;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public static string? ImagePath(int pid)
        {
            IntPtr h = OpenProcess(QueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var buffer = new char[1024];
                int size = buffer.Length;
                return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
