using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Flashback.Core;

internal static class Win32
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    public static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>CPU time the calling thread has used so far, in 100 ns units.</summary>
    public static long CurrentThreadCpu() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? kernel + user : 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// Every FFmpeg process is put in this job, so Windows kills them if this app dies for any reason.
/// Without it a crashed app could leave a recorder writing to disk forever.
/// </summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    public JobObject()
    {
        _handle = Win32.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) return;
        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!Win32.SetInformationJobObject(_handle, 9, ref info, Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            Log.Warn("Could not configure the job object: " + Marshal.GetLastWin32Error());
    }

    public void Add(Process process)
    {
        try
        {
            if (_handle != IntPtr.Zero && !Win32.AssignProcessToJobObject(_handle, process.Handle))
                Log.Warn("Could not add a process to the job object: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Warn("Job assignment failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Win32.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Finds the main (primary) monitor: its handle for Windows Graphics Capture, and its number the way FFmpeg's
/// ddagrab counts outputs.
/// </summary>
public static unsafe class DisplayInfo
{
    public readonly record struct PrimaryDisplay(int OutputIndex, int Width, int Height, long Monitor);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)p)[2])(p);
    }

    public static PrimaryDisplay GetPrimary()
    {
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            if (Win32.CreateDXGIFactory1(IidFactory1, out factory) >= 0 && factory != IntPtr.Zero)
            {
                // IDXGIFactory::EnumAdapters, IDXGIAdapter::EnumOutputs and IDXGIOutput::GetDesc are all vtable slot 7.
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[7])(factory, 0, &adapter);
                if (hr >= 0 && adapter != IntPtr.Zero)
                {
                    for (uint i = 0; i < 16; i++)
                    {
                        IntPtr output = IntPtr.Zero;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)adapter)[7])(adapter, i, &output);
                        if (hr < 0 || output == IntPtr.Zero) break;
                        DXGI_OUTPUT_DESC desc = default;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)(*(void***)output)[7])(output, &desc);
                        Release(output);
                        // The primary monitor is always the one whose top-left corner is the desktop origin.
                        if (hr >= 0 && desc.AttachedToDesktop != 0 && desc.Left == 0 && desc.Top == 0)
                        {
                            int w = desc.Right - desc.Left, h = desc.Bottom - desc.Top;
                            if (w > 0 && h > 0) return new PrimaryDisplay((int)i, w, h, desc.Monitor != IntPtr.Zero ? (long)desc.Monitor : PrimaryMonitor());
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Display lookup failed: " + ex.Message);
        }
        finally
        {
            Release(adapter);
            Release(factory);
        }
        return new PrimaryDisplay(0, Math.Max(2, Win32.GetSystemMetrics(0)), Math.Max(2, Win32.GetSystemMetrics(1)), PrimaryMonitor());
    }

    /// <summary>The primary monitor is the one at the desktop origin (MONITOR_DEFAULTTOPRIMARY).</summary>
    private static long PrimaryMonitor() => (long)Win32.MonitorFromPoint(default, 1);
}

/// <summary>System-wide hotkey. Works while a game has focus.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int Id = 0x0C11;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        var p = new HwndSourceParameters("FlashbackHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // message-only window
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        if (virtualKey == 0) return false;
        _registered = Win32.RegisterHotKey(_source.Handle, Id, modifiers | ModNoRepeat, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Flashback";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else
                key.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the start-with-Windows setting", ex);
        }
    }
}
