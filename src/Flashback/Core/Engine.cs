using System.IO;
using System.Media;

namespace Flashback.Core;

public sealed record EngineStatus(
    RecorderState State, string Detail, RecorderConfig? Config,
    bool DesktopEnabled, string DesktopStatus, string DesktopDevice,
    bool MicEnabled, string MicStatus, string MicDevice, int BufferedSeconds);

/// <summary>Owns the recorder, the audio capture and clip saving, and applies settings changes to them.</summary>
public sealed class Engine : IDisposable
{
    private readonly JobObject _job = new();
    private readonly object _applyLock = new();
    private System.Threading.Timer? _restartTimer;
    private System.Threading.Timer? _encoderRecheck;
    private bool? _nvencOk;
    private bool? _nvencLogged;
    private bool _started;

    public SettingsStore Settings { get; }
    public string? FfmpegPath { get; }
    public VideoRecorder? Recorder { get; }
    public AudioSource Desktop { get; } = new(loopback: true);
    public AudioSource Mic { get; } = new(loopback: false);
    public ClipService? Clips { get; }
    public JobObject Job => _job;

    public Engine(SettingsStore settings)
    {
        Settings = settings;
        FfmpegPath = Ffmpeg.Locate();
        if (FfmpegPath == null)
        {
            Log.Error("ffmpeg.exe was not found next to the app, in tools\\ffmpeg, or on PATH");
            return;
        }
        Log.Info("Using FFmpeg at " + FfmpegPath);
        Recorder = new VideoRecorder(FfmpegPath, AppPaths.BufferDir, BuildConfig, _job)
        {
            SessionFailed = failures =>
            {
                // If capture keeps failing, re-check whether the GPU encoder is still usable.
                if (failures == 3 || failures % 40 == 0) _nvencOk = ProbeNvenc();
            },
        };
        Clips = new ClipService(FfmpegPath, Recorder, Desktop, Mic, _job);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        AppSettings s = Settings.Current;
        ApplyRetention(s);
        try
        {
            foreach (string f in Directory.GetFiles(AppPaths.TempDir)) File.Delete(f);
        }
        catch { }
        ClipLibrary.RemoveStaleParts(s.SaveFolder);
        if (s.HotkeySoundName != null) Sounds.UseCustom(true);
        Mic.SetDevice(s.MicDeviceId);
        if (s.DesktopAudio) Desktop.Start();
        if (s.Microphone) Mic.Start();
        Recorder?.Start();
        Settings.Changed += OnSettingsChanged;
        // If the GPU encoder was unavailable (a driver update, for example), go back to it once it returns.
        _encoderRecheck = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_nvencOk == false && !Settings.Current.ForceSoftwareEncoder && ProbeNvenc())
                {
                    _nvencOk = true;
                    Recorder?.RequestRestart("GPU encoder became available");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Encoder re-check failed", ex);
            }
        }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private void ApplyRetention(AppSettings s)
    {
        if (Recorder != null) Recorder.RetainSeconds = s.ClipSeconds + 15;
        Desktop.Ring.RetainSeconds = s.ClipSeconds + 20;
        Mic.Ring.RetainSeconds = s.ClipSeconds + 20;
    }

    private void OnSettingsChanged(AppSettings old, AppSettings now)
    {
        lock (_applyLock)
        {
            ApplyRetention(now);

            if (now.DesktopAudio && !Desktop.IsRunning) Desktop.Start();
            else if (!now.DesktopAudio && Desktop.IsRunning) Desktop.Stop();

            Mic.SetDevice(now.MicDeviceId);
            if (now.Microphone && !Mic.IsRunning) Mic.Start();
            else if (!now.Microphone && Mic.IsRunning) Mic.Stop();

            if (old.StartWithWindows != now.StartWithWindows && !AppPaths.IsCustom) AutoStart.Set(now.StartWithWindows);

            bool videoChanged = old.Quality != now.Quality || old.Fps != now.Fps || old.CaptureCursor != now.CaptureCursor
                                || old.ForceSoftwareEncoder != now.ForceSoftwareEncoder;
            if (videoChanged && Recorder != null)
            {
                // Debounced, so flicking through options does not restart the recorder for each one.
                _restartTimer?.Dispose();
                _restartTimer = new System.Threading.Timer(_ => Recorder.RequestRestart("settings changed"), null, 700, Timeout.Infinite);
            }
        }
    }

    /// <summary>Called when Windows reports a display change. Restarts capture only if the main monitor really changed.</summary>
    public void OnDisplayChanged()
    {
        if (Recorder == null) return;
        RecorderConfig? active = Recorder.Status.Config;
        var d = DisplayInfo.GetPrimary();
        if (active == null || active.OutputIndex != d.OutputIndex || active.Width != d.Width || active.Height != d.Height)
            Recorder.RequestRestart("display changed");
    }

    public void OnResume() => Recorder?.RequestRestart("resumed from sleep");

    /// <summary>The PC was unlocked: if capture was waiting to retry, retry right now.</summary>
    public void OnUnlocked() => Recorder?.Wake();

    private RecorderConfig? BuildConfig()
    {
        AppSettings s = Settings.Current;
        _nvencOk ??= ProbeNvenc();
        bool gpu = _nvencOk == true && !s.ForceSoftwareEncoder;
        var d = DisplayInfo.GetPrimary();
        int fps = gpu ? s.Fps : Math.Min(s.Fps, 30);
        (int cq, int kbps) = AppSettings.VideoQuality(s.Quality, d.Width, d.Height, fps);
        return new RecorderConfig
        {
            OutputIndex = d.OutputIndex,
            Width = d.Width,
            Height = d.Height,
            Fps = fps,
            Cq = cq,
            MaxKbps = kbps,
            Cursor = s.CaptureCursor,
            Encoder = gpu ? "h264_nvenc" : "libx264",
        };
    }

    private bool ProbeNvenc()
    {
        if (FfmpegPath == null) return false;
        try
        {
            var (code, err) = Ffmpeg.Run(FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30",
                 "-frames:v", "5", "-c:v", "h264_nvenc", "-f", "null", "-"], 20000, _job);
            bool ok = code == 0;
            if (_nvencLogged != ok)
            {
                _nvencLogged = ok;
                Log.Info(ok ? "NVIDIA GPU encoder is available" : "NVIDIA GPU encoder is not available, using the CPU encoder. " + Ffmpeg.LastLine(err));
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Encoder probe failed", ex);
            return false;
        }
    }

    /// <param name="silenceAfterPress">The hotkey sound is about to play: end the clip's audio at the press so the sound is not in it.</param>
    public Task<ClipResult> SaveClipAsync(bool silenceAfterPress = false)
    {
        if (Clips == null) return Task.FromResult(new ClipResult(false, null, "ffmpeg.exe is missing", 0));
        return Clips.SaveAsync(Settings.Current, silenceAfterPress);
    }

    /// <summary>Makes <paramref name="sourceFile"/> the hotkey sound. Call on the UI thread. Returns an error message, or null.</summary>
    public async Task<string?> SetHotkeySoundAsync(string sourceFile)
    {
        if (FfmpegPath == null) return "ffmpeg.exe is missing, so the file cannot be converted.";
        string ffmpeg = FfmpegPath;
        string? error = await Task.Run(() => Sounds.Import(ffmpeg, sourceFile, _job));
        if (error != null) return error;
        if (!Sounds.UseCustom(true)) return "The converted sound could not be played.";
        AppSettings s = Settings.Current;
        s.HotkeySoundName = Path.GetFileName(sourceFile);
        Settings.Save(s);
        return null;
    }

    /// <summary>Goes back to the built-in hotkey chime.</summary>
    public void ResetHotkeySound()
    {
        Sounds.UseCustom(false);
        AppSettings s = Settings.Current;
        s.HotkeySoundName = null;
        Settings.Save(s);
        try { File.Delete(Sounds.CustomFile); } catch { }
    }

    public EngineStatus GetStatus()
    {
        AppSettings s = Settings.Current;
        var (state, detail, cfg) = Recorder?.Status ?? (RecorderState.Error, "ffmpeg.exe is missing. Put it next to the app.", null);
        return new EngineStatus(state, detail, cfg,
            s.DesktopAudio, Desktop.Status, Desktop.DeviceName,
            s.Microphone, Mic.Status, Mic.DeviceName, Recorder?.BufferedSeconds ?? 0);
    }

    public void Dispose()
    {
        Settings.Changed -= OnSettingsChanged;
        _restartTimer?.Dispose();
        _encoderRecheck?.Dispose();
        Recorder?.Stop();
        Desktop.Stop();
        Mic.Stop();
        _job.Dispose();
    }
}

/// <summary>
/// The hotkey sound (a built-in chime, or a file the user picked) and the failure tone. The built-in ones are
/// generated in memory so there are no sound files to ship.
/// </summary>
public static class Sounds
{
    /// <summary>A picked sound is cut to this length, so a song cannot play over the game for minutes.</summary>
    public const int MaxCustomSeconds = 5;

    private static readonly SoundPlayer DefaultHotkeyPlayer = Make([(880, 70), (1320, 110)]);
    private static readonly SoundPlayer FailedPlayer = Make([(330, 110), (220, 180)]);
    private static volatile SoundPlayer? _customPlayer;

    /// <summary>The user's hotkey sound, converted to plain WAV so playing it never depends on installed codecs.</summary>
    public static string CustomFile => Path.Combine(AppPaths.ConfigDir, "hotkey-sound.wav");

    public static void Hotkey()
    {
        SoundPlayer? custom = _customPlayer;
        if (custom == null || !Play(custom)) Play(DefaultHotkeyPlayer);
    }

    public static void Failed() => Play(FailedPlayer);

    /// <summary>Switches between the user's sound in <see cref="CustomFile"/> and the built-in chime. False if the file cannot be played.</summary>
    public static bool UseCustom(bool custom)
    {
        if (!custom)
        {
            _customPlayer = null;
            return true;
        }
        try
        {
            // Read into memory: the file stays free to be replaced, and playing never touches the disk.
            byte[] wav = File.ReadAllBytes(CustomFile);
            if (!IsPcmWav(wav)) throw new InvalidDataException("not a playable WAV file");
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Load();
            _customPlayer = player;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("The custom hotkey sound could not be loaded, using the built-in one: " + ex.Message);
            _customPlayer = null;
            return false;
        }
    }

    /// <summary>
    /// Converts any file FFmpeg can read (WAV, MP3, OGG, FLAC, M4A, even a video) into <see cref="CustomFile"/>,
    /// keeping the first <see cref="MaxCustomSeconds"/> seconds. The current sound is only replaced once the new
    /// one is known to play. Returns an error message for the user, or null on success.
    /// </summary>
    public static string? Import(string ffmpeg, string source, JobObject job)
    {
        string tmp = CustomFile + ".new.wav";
        try
        {
            var (code, err) = Ffmpeg.Run(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-map", "0:a:0", "-t", MaxCustomSeconds.ToString(),
                 "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-map_metadata", "-1", "-fflags", "+bitexact", "-f", "wav", tmp],
                30000, job);
            if (code != 0)
            {
                Log.Warn($"Hotkey sound import failed ({code}): {Ffmpeg.LastLine(err)}");
                return err.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                    ? "That file has no sound in it."
                    : "That file could not be read as audio.";
            }
            // At least 20 ms of audio after the 44-byte header.
            if (new FileInfo(tmp).Length < 44 + 48000 * 4 / 50) return "That sound is too short.";
            if (!IsPcmWav(File.ReadAllBytes(tmp))) return "That file could not be converted to a playable sound.";
            File.Move(tmp, CustomFile, true);
            Log.Info($"Hotkey sound set from {source}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey sound import failed", ex);
            return ex.Message;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static bool Play(SoundPlayer p)
    {
        try
        {
            p.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Sound failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks that <paramref name="b"/> is a PCM WAV file with sound in it. SoundPlayer only finds out when it is
    /// asked to play, and then plays nothing.
    /// </summary>
    private static bool IsPcmWav(byte[] b)
    {
        if (b.Length < 44 || !b.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !b.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return false;
        bool pcm = false;
        long pos = 12;
        while (pos + 8 <= b.Length)
        {
            ReadOnlySpan<byte> id = b.AsSpan((int)pos, 4);
            long size = BitConverter.ToUInt32(b, (int)pos + 4);
            if (pos + 8 + size > b.Length) return false;
            if (id.SequenceEqual("fmt "u8)) pcm = size >= 16 && BitConverter.ToUInt16(b, (int)pos + 8) == 1;
            else if (id.SequenceEqual("data"u8)) return pcm && size > 0;
            pos += 8 + size + (size & 1);
        }
        return false;
    }

    private static SoundPlayer Make((int Hz, int Ms)[] notes)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (hz, ms) in notes)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i / (rate * 0.005), (n - i) / (rate * 0.03)));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * 0.22 * short.MaxValue));
            }
        }
        var ms2 = new MemoryStream();
        using (var w = new BinaryWriter(ms2, System.Text.Encoding.ASCII, true))
        {
            int dataBytes = samples.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (short s in samples) w.Write(s);
        }
        ms2.Position = 0;
        var player = new SoundPlayer(ms2);
        try { player.Load(); } catch { }
        return player;
    }
}
