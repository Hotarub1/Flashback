using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Flashback.Core;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Flashback;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _quitSignal;
    private SettingsStore _settings = null!;
    private Engine _engine = null!;
    private GlobalHotkey? _hotkey;
    private MainWindow? _window;
    private ToastWindow? _toast;
    private WinForms.NotifyIcon? _tray;
    private DispatcherTimer? _hotkeyRetry;
    private bool _hotkeySuspended;
    private bool _exiting;
    private Size? _renderSize;

    public bool HotkeyOk { get; private set; }
    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? dataDir = ArgValue(e.Args, "--data-dir");
        string? selfTest = ArgValue(e.Args, "--selftest");
        string? renderUi = ArgValue(e.Args, "--render-ui");
        bool minimized = e.Args.Contains("--minimized");
        bool diagnostic = selfTest != null || renderUi != null;

        // --quit asks the copy that is already running to exit cleanly.
        bool quit = e.Args.Contains("--quit");

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            _instanceMutex = new Mutex(true, "Flashback.Instance", out bool first);
            if (!first || quit)
            {
                // Already running: ask that copy to show its window (or to exit), then leave.
                if (!first)
                {
                    try { EventWaitHandle.OpenExisting(quit ? "Flashback.Quit" : "Flashback.Show").Set(); } catch { }
                }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Flashback.Show");
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Flashback.Quit");
            var waiter = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny([_showSignal, _quitSignal]);
                    if (_exiting) break;
                    if (which == 1)
                    {
                        Dispatcher.BeginInvoke(ExitApp);
                        break;
                    }
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
            }) { IsBackground = true, Name = "InstanceSignals" };
            waiter.Start();
        }
        else if (quit)
        {
            Shutdown();
            return;
        }

        AppPaths.Init(dataDir);

        Log.Info($"---- Flashback starting (args: {string.Join(' ', e.Args)}) ----");
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("Unhandled UI error", a.Exception);
            a.Handled = true; // keep recording no matter what the UI did
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Fatal error", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error("Background task error", a.Exception);
            a.SetObserved();
        };

        _settings = new SettingsStore();
        _engine = new Engine(_settings);
        _toast = new ToastWindow();
        _window = new MainWindow(_engine, this);

        if (!diagnostic)
        {
            CreateTray();
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += SaveClip;
            ApplyHotkey();
            _hotkeyRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _hotkeyRetry.Tick += (_, _) =>
            {
                // Another program had the shortcut: keep trying, it may have closed since.
                if (!HotkeyOk && !_hotkeySuspended) ApplyHotkey();
            };
            _hotkeyRetry.Start();
            _settings.Changed += (old, now) =>
            {
                if (old.HotkeyKey != now.HotkeyKey || old.HotkeyModifiers != now.HotkeyModifiers)
                    Dispatcher.BeginInvoke(ApplyHotkey);
            };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            if (_settings.Current.StartWithWindows) AutoStart.Set(true); // keeps the path current if the app was moved
        }

        // A UI render needs no recording: not starting the capture keeps it from competing with the copy the user runs.
        if (renderUi == null) _engine.Start();

        // --render-size WxH draws the window at that size, e.g. a maximized window on a large screen.
        if (ArgValue(e.Args, "--render-size")?.Split('x') is [var w, var h] && double.TryParse(w, out double width) && double.TryParse(h, out double height))
            _renderSize = new Size(width, height);
        if (renderUi != null) _ = RunRenderUi(renderUi);
        else if (selfTest != null) _ = RunSelfTest(selfTest);
        else if (!minimized) ShowMainWindow();
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---------------------------------------------------------------- hotkey

    public void SuspendHotkey()
    {
        _hotkeySuspended = true;
        _hotkey?.Unregister();
    }

    public void ApplyHotkey()
    {
        _hotkeySuspended = false;
        if (_hotkey == null) return;
        AppSettings s = _settings.Current;
        bool ok = _hotkey.Register(s.HotkeyModifiers, s.HotkeyKey);
        if (ok != HotkeyOk) Log.Info(ok ? $"Hotkey {s.HotkeyText} registered" : $"Hotkey {s.HotkeyText} is in use by another program");
        HotkeyOk = ok;
    }

    // ---------------------------------------------------------------- clips

    public async void SaveClip()
    {
        try
        {
            AppSettings s = _settings.Current;
            if (_engine.Clips?.IsBusy == true) return;
            // The save notes the press time first; the sound starts just after, so the clip's audio can end
            // before it and the sound is not recorded into the clip.
            Task<ClipResult> saving = _engine.SaveClipAsync(silenceAfterPress: s.PlaySound);
            if (s.PlaySound) Sounds.Hotkey();
            if (s.ShowToast) _toast?.ShowMessage("Saving clip…", "", false);
            ClipResult r = await saving;
            if (r.Ok)
            {
                if (s.ShowToast) _toast?.ShowMessage("Clip saved", $"{Math.Round(r.Seconds)} seconds  ·  {Path.GetFileName(r.Path)}", false);
                _window?.OnClipSaved();
            }
            else
            {
                Log.Warn("Clip not saved: " + r.Error);
                if (s.PlaySound) Sounds.Failed();
                if (s.ShowToast) _toast?.ShowMessage("Clip not saved", r.Error ?? "", true);
                else _tray?.ShowBalloonTip(4000, "Clip not saved", r.Error ?? "", WinForms.ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SaveClip", ex);
        }
    }

    // ---------------------------------------------------------------- window and tray

    public void ShowMainWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowTrayHint()
    {
        AppSettings s = _settings.Current;
        if (s.TrayHintShown || _tray == null) return;
        _tray.ShowBalloonTip(5000, "Still recording", "Flashback keeps running here in the tray. Right-click the icon to exit.", WinForms.ToolTipIcon.Info);
        s.TrayHintShown = true;
        _settings.Save(s);
    }

    private void CreateTray()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            using Stream? stream = GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))?.Stream;
            if (stream != null) icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon could not be loaded: " + ex.Message);
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Flashback", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Save clip now", null, (_, _) => SaveClip());
        menu.Items.Add("Open clips folder", null, (_, _) => Flashback.MainWindow.OpenFolder(_settings.Current.SaveFolder));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "Flashback",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _hotkeyRetry?.Stop();
            _hotkey?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _window?.Shutdown();
            _toast?.Close();
            _engine.Dispose();
            _showSignal?.Set();
        }
        catch (Exception ex)
        {
            Log.Error("Error while exiting", ex);
        }
        Shutdown();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _engine.OnSuspend();
        else if (e.Mode == PowerModes.Resume) _engine.OnResume();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _engine.OnDisplayChanged();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionLogon)
            _engine.OnUnlocked();
    }

    // ---------------------------------------------------------------- diagnostics (not part of normal use)

    /// <summary>--selftest N: record for N seconds, save one clip, write selftest.json, exit.</summary>
    private async Task RunSelfTest(string secondsText)
    {
        int code = 1;
        try
        {
            int seconds = Math.Clamp(int.TryParse(secondsText, out int v) ? v : 8, 3, 120);
            await Task.Delay(seconds * 1000);
            EngineStatus before = _engine.GetStatus();
            ClipResult r = await _engine.SaveClipAsync();

            // Also exercise the player's reading of the clip and a trimmed copy of it, as "Save trim" would make.
            ClipInfo? info = null;
            object? trim = null;
            if (r.Ok && r.Path != null && _engine.FfmpegPath is { } ffmpeg)
            {
                info = await Task.Run(() => ClipAudio.Probe(ffmpeg, r.Path, _engine.Job));
                if (info != null && info.Seconds > 2.5)
                {
                    string output = ClipLibrary.UniquePath(Path.GetDirectoryName(r.Path)!, Path.GetFileNameWithoutExtension(r.Path) + " (trimmed)");
                    float[] gains = info.StemStreams.Select((_, i) => i % 2 == 0 ? 0.5f : 1.5f).ToArray();
                    var request = new TrimRequest(r.Path, output, 1.0, info.Seconds - 1.0, gains, info, _settings.Current.Quality);
                    string? error = await Task.Run(() => ClipEditor.Export(ffmpeg, request, _engine.Job, null));
                    ClipInfo? trimmed = error == null ? await Task.Run(() => ClipAudio.Probe(ffmpeg, output, _engine.Job)) : null;
                    trim = new { Ok = error == null, Path = output, Error = error, Info = trimmed };
                }
            }
            var report = new
            {
                r.Ok, r.Path, r.Error, r.Seconds, r.StartUtc,
                RecorderState = before.State.ToString(), before.Detail, Config = before.Config?.Key, before.Config?.OutputIndex,
                Desktop = new { before.DesktopStatus, before.DesktopDevice, _engine.Desktop.Ring.TotalPackets, _engine.Desktop.Ring.TotalFrames },
                Mic = new { before.MicStatus, before.MicDevice, _engine.Mic.Ring.TotalPackets, _engine.Mic.Ring.TotalFrames },
                Apps = _engine.Apps.Snapshot().Select(a => new { a.Name, a.Key, a.Ring.TotalPackets, a.Ring.TotalFrames }).ToList(),
                AppReopens = _engine.Apps.Reopens,
                Info = info,
                Trim = trim,
            };
            File.WriteAllText(Path.Combine(AppPaths.LocalDir, "selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            code = r.Ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Log.Error("Self test failed", ex);
        }
        _exiting = true;
        _engine.Dispose();
        Shutdown(code);
    }

    /// <summary>--render-ui DIR: draw both pages to PNG files and exit, without recording anything.</summary>
    private async Task RunRenderUi(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            // Off screen and without focus, so a check never pulls the user out of a game.
            _window!.ShowActivated = false;
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (_renderSize is { } size)
            {
                _window.Width = size.Width;
                _window.Height = size.Height;
            }
            _window.Left = SystemParameters.VirtualScreenLeft - _window.Width - 400;
            _window.Top = SystemParameters.VirtualScreenTop;
            _window.Show();
            _window.PlayerSilent = true; // a diagnostic run must not play sound through the speakers
            await Task.Delay(2500);
            _window.RenderTo(Path.Combine(dir, "clips.png"));
            _window.ScrollClips(toEnd: true);
            await Task.Delay(300);
            _window.RenderTo(Path.Combine(dir, "clips-end.png"));
            _window.ScrollClips(toEnd: false);
            Log.Info("Chrome check: " + _window.ChromeCheck());
            Log.Info("Caption check: " + _window.CaptionCheck());
            Log.Info("Layout check: " + _window.LayoutCheck());
            Log.Info("Band check: " + Flashback.MainWindow.BandCheck());
            Log.Info("Title bar check: " + _window.TitleBarCheck());
            if (_window.PlayFirstClip())
            {
                await Task.Delay(3000);
                Log.Info("Player check: " + _window.PlayerDebug());
                _window.RenderTo(Path.Combine(dir, "player.png"));
                await Task.Delay(2000);
                Log.Info("Player check: " + _window.PlayerDebug());
                Log.Info("Align check: " + _window.AlignCheck());
                Log.Info("Scrub check: " + await _window.ScrubCheckAsync());
                Log.Info("Volume check: " + _window.VolumeCheck());
                _window.ClosePlayerForTest();
                Log.Info("Still check: " + await _window.StillCheckAsync());
            }
            _window.ShowSettingsTab();
            for (int section = 0; section < 4; section++)
            {
                _window.ShowSettingsSection(section);
                await Task.Delay(section == 0 ? 1000 : 400);
                if (section == 0) Log.Info("Settings check: " + _window.SettingsCheck());
                _window.RenderTo(Path.Combine(dir, $"settings-{section + 1}.png"));
            }
            _toast?.RenderPreview(Path.Combine(dir, "toast.png"));
        }
        catch (Exception ex)
        {
            Log.Error("Render failed", ex);
        }
        _exiting = true;
        _engine.Dispose();
        Shutdown();
    }
}
