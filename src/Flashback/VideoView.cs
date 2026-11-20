using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Flashback.Core;

namespace Flashback;

/// <summary>
/// Shows a clip's video: the Media Foundation Media Engine (what the Windows Media Player app plays with) draws it
/// straight into a child window and times each frame to the screen itself. WPF's MediaElement handed every frame to
/// WPF's own renderer, which put them up at uneven moments: on a 240 Hz screen its frames stayed up anywhere from 4 to
/// 38 ms instead of 16.7 and about one in thirteen never showed, so smooth clips juddered in the app. Through the
/// engine they stay up 16.7 ms, give or take a refresh, and none are skipped.
///
/// The element sizes itself to the video's shape (centre it to letterbox). Being a window of its own, WPF cannot draw
/// over it: <see cref="CutBottom"/> leaves a strip clear for the full-screen control bar. Mouse input passes through to
/// the WPF window underneath.
/// </summary>
public sealed class VideoView : HwndHost
{
    private IntPtr _hwnd;
    private Mf.IMFMediaEngineEx? _engine;
    private EngineEvents? _events;
    private string? _pending;
    private int _generation;
    private bool _wantPlay;
    private double _aspect;
    private double _cutBottom;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    /// <summary>The clip's length in seconds; 0 until it has been opened.</summary>
    public double Duration { get; private set; }
    public bool HasDuration => Duration > 0;
    public int NaturalWidth { get; private set; }
    public int NaturalHeight { get; private set; }
    public bool HasAudio => _engine is { } engine && Call(() => engine.HasAudio() != 0);

    /// <summary>Where the picture is, in seconds. Setting it seeks to that exact frame, also while paused.</summary>
    public double Position
    {
        get
        {
            if (_engine is not { } engine) return 0;
            double t = Call(() => engine.GetCurrentTime());
            return double.IsFinite(t) ? t : 0;
        }
        set
        {
            if (_engine is { } engine) Call(() => engine.SetCurrentTime(Math.Max(0, value)));
        }
    }

    private double _volume = 1;
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_engine is { } engine) Call(() => engine.SetVolume(_volume));
        }
    }

    private bool _muted;
    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_engine is { } engine) Call(() => engine.SetMuted(value));
        }
    }

    /// <summary>How much of the bottom of the picture (in WPF units) to leave out, so WPF content there shows.</summary>
    public double CutBottom
    {
        get => _cutBottom;
        set
        {
            if (Math.Abs(value - _cutBottom) < 0.5) return;
            _cutBottom = value;
            ApplyCut();
        }
    }

    /// <summary>Opens a clip; it starts once <see cref="Play"/> is called.</summary>
    public void Open(string path)
    {
        Close();
        if (_hwnd == IntPtr.Zero) _pending = path; // the window is made when the element is first shown, the engine with it
        else CreateEngine(path);
    }

    /// <summary>Stops and lets go of the file, so it can be renamed or deleted.</summary>
    public void Close()
    {
        _pending = null;
        _wantPlay = false;
        _generation++;
        Duration = 0;
        NaturalWidth = NaturalHeight = 0;
        if (_aspect != 0)
        {
            _aspect = 0;
            InvalidateMeasure();
        }
        if (_engine is { } engine)
        {
            _engine = null;
            try { engine.Shutdown(); } catch { }
            Marshal.ReleaseComObject(engine);
        }
        _events = null;
    }

    public void Play()
    {
        _wantPlay = true;
        if (_engine is { } engine) Call(() => engine.Play());
    }

    public void Pause()
    {
        _wantPlay = false;
        if (_engine is { } engine) Call(() => engine.Pause());
    }

    private void CreateEngine(string path)
    {
        Mf.Startup();
        int generation = _generation;
        _events = new EngineEvents((ev, p1, p2) => Dispatcher.BeginInvoke(() => OnEvent(generation, ev, p1, p2)));
        Mf.Check(Mf.MFCreateAttributes(out Mf.IMFAttributes attrs, 3));
        try
        {
            attrs.SetUnknown(Mf.MF_MEDIA_ENGINE_CALLBACK, _events);
            attrs.SetUINT64(Mf.MF_MEDIA_ENGINE_PLAYBACK_HWND, (ulong)_hwnd);
            attrs.SetUINT32(Mf.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, 87); // DXGI_FORMAT_B8G8R8A8_UNORM
            var factory = (Mf.IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(Mf.CLSID_MFMediaEngineClassFactory)!)!;
            try
            {
                factory.CreateInstance(0, attrs, out Mf.IMFMediaEngine created);
                _engine = (Mf.IMFMediaEngineEx)created;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attrs);
        }
        _engine.SetVolume(_volume);
        _engine.SetMuted(_muted);
        _engine.SetSource(path);
        if (_wantPlay) _engine.Play(); // asked for before the window existed
    }

    private void OnEvent(int generation, int ev, long p1, int p2)
    {
        if (generation != _generation || _engine is not { } engine) return;
        switch (ev)
        {
            case Mf.EventLoadedMetadata:
                double d = Call(() => engine.GetDuration());
                Duration = double.IsFinite(d) && d > 0 ? d : 0;
                uint w = 0, h = 0, ax = 0, ay = 0;
                Call(() => engine.GetNativeVideoSize(out w, out h));
                Call(() => engine.GetVideoAspectRatio(out ax, out ay));
                NaturalWidth = (int)w;
                NaturalHeight = (int)h;
                _aspect = ax > 0 && ay > 0 ? (double)ax / ay : w > 0 && h > 0 ? (double)w / h : 0;
                InvalidateMeasure();
                Opened?.Invoke();
                break;
            case Mf.EventEnded:
                Ended?.Invoke();
                break;
            case Mf.EventError:
                string reason = p1 switch
                {
                    1 => "Playback was stopped.",
                    2 => "The file could not be read.",
                    3 => "The video could not be decoded.",
                    4 => "This file type is not supported.",
                    _ => "Playback failed.",
                };
                Failed?.Invoke($"{reason} (0x{p2:X8})");
                break;
        }
    }

    /// <summary>Runs an engine call; a failing call is logged, not thrown, as MediaElement never threw either.</summary>
    private static T Call<T>(Func<T> call)
    {
        try { return call(); }
        catch (Exception ex)
        {
            Log.Warn("Video: " + ex.Message);
            return default!;
        }
    }

    private static void Call(Action call)
    {
        try { call(); }
        catch (Exception ex) { Log.Warn("Video: " + ex.Message); }
    }

    // ================================================================ layout and the child window

    /// <summary>As large as fits while keeping the video's shape; nothing until a clip is open.</summary>
    protected override Size MeasureOverride(Size available)
    {
        if (_aspect <= 0 || double.IsInfinity(available.Width) || double.IsInfinity(available.Height)) return new Size(0, 0);
        double width = Math.Min(available.Width, available.Height * _aspect);
        return new Size(width, width / _aspect);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _hwnd = Mf.CreateWindowEx(0, "static", "", Mf.WS_CHILD | Mf.WS_VISIBLE | Mf.WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_pending is { } path)
        {
            _pending = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_engine != null || _hwnd == IntPtr.Zero) return;
                try { CreateEngine(path); }
                catch (Exception ex) { Failed?.Invoke(ex.Message); }
            });
        }
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Close();
        Mf.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ApplyCut();
        Redraw();
    }

    /// <summary>
    /// Has the engine draw the picture at the window's new size. While playing it does that by itself on the next frame;
    /// while paused it would otherwise keep showing the old, smaller picture blown up.
    /// </summary>
    private unsafe void Redraw()
    {
        if (_engine is not { } engine || _aspect <= 0 || !Mf.GetClientRect(_hwnd, out Mf.RECT r) || r.Right <= 0 || r.Bottom <= 0) return;
        uint black = 0xFF000000; // MFARGB: blue, green, red, alpha
        try { engine.UpdateVideoStream(IntPtr.Zero, (IntPtr)(&r), (IntPtr)(&black)); }
        catch (Exception ex) { Log.Warn("Video: " + ex.Message); }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Mf.WM_NCHITTEST:
                // The mouse goes to the WPF window underneath: clicks, double clicks and the full-screen cursor work as before.
                handled = true;
                return (IntPtr)Mf.HTTRANSPARENT;
            case Mf.WM_ERASEBKGND:
                handled = true;
                return (IntPtr)1;
            case Mf.WM_PAINT:
                // Black until the engine's first frame covers it (a "static" window would paint itself light grey).
                IntPtr dc = Mf.BeginPaint(hwnd, out Mf.PAINTSTRUCT ps);
                Mf.FillRect(dc, ref ps.rcPaint, Mf.GetStockObject(Mf.BLACK_BRUSH));
                Mf.EndPaint(hwnd, ref ps);
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void ApplyCut()
    {
        if (_hwnd == IntPtr.Zero || !Mf.GetClientRect(_hwnd, out Mf.RECT r)) return;
        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int cut = (int)Math.Round(_cutBottom * scale);
        IntPtr region = cut <= 0 ? IntPtr.Zero : Mf.CreateRectRgn(0, 0, r.Right, Math.Max(0, r.Bottom - cut));
        if (Mf.SetWindowRgn(_hwnd, region, true) == 0 && region != IntPtr.Zero) Mf.DeleteObject(region); // the window owns it otherwise
    }

    /// <summary>Receives the engine's events on its own threads; they are handed to the UI thread.</summary>
    private sealed class EngineEvents(Action<int, long, int> raise) : Mf.IMFMediaEngineNotify
    {
        public int EventNotify(int ev, IntPtr param1, int param2)
        {
            if (ev is Mf.EventLoadedMetadata or Mf.EventEnded or Mf.EventError) raise(ev, (long)param1, param2);
            return 0;
        }
    }

    /// <summary>The parts of Media Foundation, the Media Engine and Win32 the view needs.</summary>
    private static class Mf
    {
        public const int EventError = 5, EventLoadedMetadata = 10, EventEnded = 19;
        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        public const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1, BLACK_BRUSH = 4;

        public static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
        public static readonly Guid MF_MEDIA_ENGINE_PLAYBACK_HWND = new("d988879b-67c9-4d92-baa7-6eadd446039d");
        public static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new("5066893c-8cf9-42bc-8b8a-472212e52726");
        public static readonly Guid CLSID_MFMediaEngineClassFactory = new("b44392da-499b-446b-a4cb-005fead0e6d5");

        private static bool _started;

        public static void Startup()
        {
            if (_started) return;
            Check(MFStartup(0x20070, 0)); // MF_VERSION, MFSTARTUP_FULL
            _started = true;
        }

        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
        [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble();
            void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob();
            void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            void SetUINT32(in Guid key, uint value);
            void SetUINT64(in Guid key, ulong value);
            void SetDouble(); void SetGUID(); void SetString(); void SetBlob();
            void SetUnknown(in Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        }

        [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaEngineClassFactory
        {
            void CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine engine);
        }

        [ComImport, Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaEngineNotify
        {
            [PreserveSig] int EventNotify(int ev, IntPtr param1, int param2);
        }

        [ComImport, Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaEngine { }

        /// <summary>IMFMediaEngine's methods in order, then IMFMediaEngineEx's, as far as they are needed.</summary>
        [ComImport, Guid("83015ead-b1e6-40d0-a98a-37145ffe1ad1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaEngineEx
        {
            void GetError(out IntPtr error);
            void SetErrorCode(int error);
            void SetSourceElements(IntPtr elements);
            void SetSource([MarshalAs(UnmanagedType.BStr)] string url);
            void GetCurrentSource([MarshalAs(UnmanagedType.BStr)] out string url);
            [PreserveSig] ushort GetNetworkState();
            [PreserveSig] int GetPreload();
            void SetPreload(int preload);
            void GetBuffered(out IntPtr ranges);
            void Load();
            void CanPlayType(IntPtr type, out int answer);
            [PreserveSig] ushort GetReadyState();
            [PreserveSig] int IsSeeking();
            [PreserveSig] double GetCurrentTime();
            void SetCurrentTime(double seconds);
            [PreserveSig] double GetStartTime();
            [PreserveSig] double GetDuration();
            [PreserveSig] int IsPaused();
            [PreserveSig] double GetDefaultPlaybackRate();
            void SetDefaultPlaybackRate(double rate);
            [PreserveSig] double GetPlaybackRate();
            void SetPlaybackRate(double rate);
            void GetPlayed(out IntPtr ranges);
            void GetSeekable(out IntPtr ranges);
            [PreserveSig] int IsEnded();
            [PreserveSig] int GetAutoPlay();
            void SetAutoPlay([MarshalAs(UnmanagedType.Bool)] bool autoPlay);
            [PreserveSig] int GetLoop();
            void SetLoop([MarshalAs(UnmanagedType.Bool)] bool loop);
            void Play();
            void Pause();
            [PreserveSig] int GetMuted();
            void SetMuted([MarshalAs(UnmanagedType.Bool)] bool muted);
            [PreserveSig] double GetVolume();
            void SetVolume(double volume);
            [PreserveSig] int HasVideo();
            [PreserveSig] int HasAudio();
            void GetNativeVideoSize(out uint width, out uint height);
            void GetVideoAspectRatio(out uint x, out uint y);
            void Shutdown();
            void TransferVideoFrame(IntPtr surface, IntPtr src, IntPtr dst, IntPtr border);
            void OnVideoStreamTick(out long pts);
            // IMFMediaEngineEx
            void SetSourceFromByteStream(IntPtr byteStream, [MarshalAs(UnmanagedType.BStr)] string url);
            void GetStatistics(int type, IntPtr value);
            void UpdateVideoStream(IntPtr src, IntPtr dst, IntPtr border);
        }
    }
}
