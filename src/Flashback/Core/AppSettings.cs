using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Flashback.Core;

public sealed class AppSettings
{
    public string SaveFolder { get; set; } = AppPaths.DefaultSaveFolder;

    /// <summary>Win32 MOD_* flags: Alt=1, Ctrl=2, Shift=4, Win=8.</summary>
    public uint HotkeyModifiers { get; set; } = 0x2 | 0x4;
    /// <summary>Win32 virtual key code. Default F9.</summary>
    public uint HotkeyKey { get; set; } = 0x78;

    public int ClipSeconds { get; set; } = 30;
    /// <summary>Low, Medium, High or Ultra.</summary>
    public string Quality { get; set; } = "High";
    /// <summary>Height of saved clips. 0 keeps the monitor's native resolution.</summary>
    public int ClipHeight { get; set; }
    public int Fps { get; set; } = 60;
    public bool CaptureCursor { get; set; } = true;

    public bool DesktopAudio { get; set; } = true;
    public bool Microphone { get; set; } = true;
    /// <summary>Null means "whatever Windows has as the default microphone".</summary>
    public string? MicDeviceId { get; set; }
    public int MicVolume { get; set; } = 100;

    /// <summary>Play a sound the moment the clip hotkey is pressed.</summary>
    public bool PlaySound { get; set; } = true;
    /// <summary>Name of the file the user picked as the hotkey sound (kept converted in <see cref="Sounds.CustomFile"/>). Null: the built-in chime.</summary>
    public string? HotkeySoundName { get; set; }
    public bool ShowToast { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>Not shown in the UI. Forces the CPU encoder, for troubleshooting.</summary>
    public bool ForceSoftwareEncoder { get; set; }

    public static readonly int[] ClipLengths = [15, 30, 45, 60, 90, 120, 180, 300];
    public static readonly string[] Qualities = ["Low", "Medium", "High", "Ultra"];
    public const int MaxClipSeconds = 300;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = AppPaths.DefaultSaveFolder;
        ClipSeconds = Math.Clamp(ClipSeconds, 5, MaxClipSeconds);
        if (Array.IndexOf(Qualities, Quality) < 0) Quality = "High";
        if (Fps != 30 && Fps != 60) Fps = 60;
        if (ClipHeight is not (0 or 720 or 1080 or 1440)) ClipHeight = 0;
        MicVolume = Math.Clamp(MicVolume, 0, 300);
        HotkeyModifiers &= 0xF;
        if (string.IsNullOrWhiteSpace(MicDeviceId)) MicDeviceId = null;
        if (string.IsNullOrWhiteSpace(HotkeySoundName)) HotkeySoundName = null;
    }

    /// <summary>Constant-quality level and bitrate ceiling for a given quality preset and frame size.</summary>
    public static (int Cq, int MaxKbps) VideoQuality(string quality, int width, int height, int fps)
    {
        (int cq, double mbps) = quality switch
        {
            "Low" => (33, 8.0),
            "Medium" => (29, 14.0),
            "Ultra" => (20, 45.0),
            _ => (25, 25.0),
        };
        double scale = Math.Pow(width * (double)height / (2560.0 * 1440.0), 0.75) * Math.Pow(fps / 60.0, 0.6);
        return (cq, Math.Clamp((int)(mbps * 1000 * scale), 1500, 120000));
    }

    public string HotkeyText => DescribeHotkey(HotkeyModifiers, HotkeyKey);

    public static string DescribeHotkey(uint mods, uint vk)
    {
        if (vk == 0) return "None";
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Shift");
        if ((mods & 8) != 0) parts.Add("Win");
        Key key = KeyInterop.KeyFromVirtualKey((int)vk);
        string name;
        if (key >= Key.D0 && key <= Key.D9) name = ((int)(key - Key.D0)).ToString();
        else if (key >= Key.NumPad0 && key <= Key.NumPad9) name = "Num " + (int)(key - Key.NumPad0);
        else name = key switch
        {
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Back => "Backspace",
            Key.Enter => "Enter",
            Key.CapsLock => "CapsLock",
            Key.Scroll => "ScrollLock",
            Key.OemTilde => "`",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.Multiply => "Num *",
            Key.Add => "Num +",
            Key.Subtract => "Num -",
            Key.Divide => "Num /",
            Key.Decimal => "Num .",
            _ => key.ToString(),
        };
        parts.Add(name);
        return string.Join("+", parts);
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore() => _current = Load();

    /// <summary>A private copy. Change it, then pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    public event Action<AppSettings, AppSettings>? Changed;

    public void Save(AppSettings next)
    {
        next.Normalize();
        AppSettings old;
        lock (_gate)
        {
            old = _current;
            _current = next.Clone();
            try
            {
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_current, Json));
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save settings", ex);
            }
        }
        Changed?.Invoke(old, next.Clone());
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable, using defaults", ex);
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", true); } catch { }
        }
        var d = new AppSettings();
        d.Normalize();
        return d;
    }
}
