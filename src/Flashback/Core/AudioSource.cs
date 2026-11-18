using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback.Core;

/// <summary>
/// Continuously captures one audio source (everything the PC plays, a microphone, or what one app plays) into an
/// <see cref="AudioRing"/>. Recovers by itself when a device is unplugged, changes, or the default device is switched.
/// </summary>
public sealed unsafe class AudioSource
{
    private static readonly AudioClientStreamFlags AutoConvert =
        unchecked((AudioClientStreamFlags)0x80000000) | unchecked((AudioClientStreamFlags)0x08000000);

    /// <summary>How often an app capture checks its own CPU use, and the share of one core it may use.</summary>
    internal static int CpuCheckMs = 5000;
    internal static double MaxCpuShare = 0.15;

    private readonly bool _loopback;
    private readonly int _processId;
    private readonly string _label;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _reinit;
    private volatile string? _deviceId;
    private volatile string _status = "Off";
    private volatile string _deviceName = "";
    private volatile float _peak;
    private string _lastError = "";

    // Conversion state for the fallback path (device format -> 48 kHz stereo 16-bit).
    private short[] _scratch = new short[9600];
    private float[] _left = [], _right = [];
    private int _srcRate = AudioRing.SampleRate, _srcChannels = 2, _srcBits = 16;
    private bool _srcFloat, _direct;
    private bool _resync;
    private int _runaways;
    private int _gapFrames;
    private double _gapTime;
    private double _resamplePos;
    private float _prevL, _prevR;

    public AudioRing Ring { get; } = new();
    public string Status => _status;
    public string DeviceName => _deviceName;
    public bool IsRunning => _thread != null;
    public bool IsOk => _status == "OK";

    public AudioSource(bool loopback)
    {
        _loopback = loopback;
        _label = loopback ? "Desktop audio" : "Microphone";
    }

    /// <summary>Captures only what <paramref name="processId"/> and the programs it started play.</summary>
    public AudioSource(int processId, string label)
    {
        _loopback = true;
        _processId = processId;
        _label = label;
    }

    public int ProcessId => _processId;

    /// <summary>How many times this capture used too much CPU and was reopened.</summary>
    public int Reopens { get; private set; }

    /// <summary>Closes the capture and opens it again, keeping what was captured so far.</summary>
    public void Reopen()
    {
        _reinit = true;
        _wake.Set();
    }

    /// <summary>Highest level since the last call, 0..1.</summary>
    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return p;
    }

    /// <summary>Null selects the Windows default device.</summary>
    public void SetDevice(string? id)
    {
        if (_deviceId == id) return;
        _deviceId = id;
        _reinit = true;
        _wake.Set();
    }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = _label };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <param name="keepAudio">Keep what was captured so far, so a clip can still include it.</param>
    public void Stop(bool keepAudio = false)
    {
        if (_thread == null) return;
        _stop = true;
        _wake.Set();
        _thread.Join(3000);
        _thread = null;
        if (!keepAudio) Ring.Clear();
    }

    private void Run()
    {
        _status = "Starting";
        while (!_stop)
        {
            try
            {
                _reinit = false;
                if (_processId > 0) CaptureProcess();
                else CaptureSession();
                _lastError = "";
            }
            catch (Exception ex)
            {
                uint hr = (uint)ex.HResult;
                string status = ex is UnauthorizedAccessException || hr == 0x80070005
                    ? "Blocked by Windows privacy settings"
                    : hr == 0x80070490 || hr == 0x88890004 ? "No device" : "Error";
                _status = status;
                _deviceName = "";
                string msg = $"{_label}: {status} ({ex.GetType().Name} 0x{hr:X8}: {ex.Message})";
                if (msg != _lastError)
                {
                    _lastError = msg;
                    Log.Warn(msg);
                }
            }
            if (_stop) break;
            // A capture that used too much CPU is reopened at once; if it keeps doing so, less and less often
            // (after 5 s, 10 s, ... up to a minute), so it can never keep a core busy.
            int waitMs = _reinit ? 50 : _runaways switch
            {
                0 => 1500,
                1 => 50,
                _ => Math.Min(60_000, 5_000 << Math.Min(_runaways - 2, 4)),
            };
            _wake.WaitOne(waitMs);
        }
        _status = "Off";
        _deviceName = "";
    }

    private MMDevice OpenDevice(MMDeviceEnumerator enumerator, out bool isDefault)
    {
        isDefault = true;
        if (_loopback) return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string? id = _deviceId;
        if (id != null)
        {
            try
            {
                MMDevice d = enumerator.GetDevice(id);
                if (d.State == DeviceState.Active)
                {
                    isDefault = false;
                    return d;
                }
                d.Dispose();
            }
            catch
            {
                // The chosen microphone is gone: fall back to the default one rather than recording nothing.
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private void CaptureSession()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = OpenDevice(enumerator, out bool isDefault);
        string deviceId = device.ID;
        string? wantedId = _deviceId;

        AudioClient client = Initialize(device);
        try
        {
            AudioCaptureClient capture = client.AudioCaptureClient;
            client.Start();
            _status = "OK";
            _deviceName = device.FriendlyName;
            Log.Info($"{_label}: capturing \"{_deviceName}\" ({(_direct ? "48 kHz direct" : $"{_srcRate} Hz {_srcChannels}ch converted")})");

            long lastCheck = Environment.TickCount64;
            while (!_stop && !_reinit)
            {
                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpc);
                    try
                    {
                        if (frames > 0 && (flags & AudioClientBufferFlags.Silent) == 0) Handle(buffer, frames, qpc);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
                Thread.Sleep(8);

                long now = Environment.TickCount64;
                if (now - lastCheck < 2000) continue;
                lastCheck = now;
                if (_deviceId != wantedId) break;
                if (isDefault)
                {
                    // Follow the Windows default device when the user switches headset/speakers/mic.
                    using MMDevice current = _loopback
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    if (current.ID != deviceId) break;
                }
                else if (device.State != DeviceState.Active) break;
            }
            try { client.Stop(); } catch { }
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Captures one app through process loopback. Windows converts to 48 kHz stereo 16-bit for us and delivers packets
    /// all the time, near-silent ones while the app plays nothing; those become gaps (see <see cref="AddGap"/>).
    /// </summary>
    private void CaptureProcess()
    {
        AudioClient client = ProcessLoopback.Activate(_processId);
        _resync = true;
        _gapFrames = 0;
        using var ready = new AutoResetEvent(false);
        try
        {
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AutoConvert,
                2_000_000, 0, new WaveFormat(AudioRing.SampleRate, 16, 2), Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            _direct = true;
            _srcRate = AudioRing.SampleRate;
            _srcChannels = 2;
            _srcBits = 16;
            _srcFloat = false;
            AudioCaptureClient capture = client.AudioCaptureClient;
            client.Start();
            _status = "OK";
            _deviceName = _label;
            if (_lastError.Length > 0) Log.Info($"App audio \"{_label}\" (process {_processId}): capturing");
            long windowStart = Environment.TickCount64;
            long cpuAtStart = Win32.CurrentThreadCpu();
            while (!_stop && !_reinit)
            {
                ready.WaitOne(40);
                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpc);
                    try
                    {
                        if (frames > 0 && (flags & AudioClientBufferFlags.Silent) == 0) Handle(buffer, frames, qpc);
                        else if (frames > 0) AddGap(PacketTime(frames, qpc), frames);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }

                // Windows' per-app capture can get stuck copying memory flat out inside GetBuffer: after the speakers
                // were unplugged, three of them each used a whole CPU core, with no end, while still delivering normal
                // audio. A fresh capture of the same app is fine, so a capture that uses far more CPU than the
                // fraction of a percent it normally needs is closed and opened again.
                long now = Environment.TickCount64;
                if (now - windowStart < CpuCheckMs) continue;
                long cpu = Win32.CurrentThreadCpu();
                double share = (cpu - cpuAtStart) / 10_000.0 / (now - windowStart);
                windowStart = now;
                cpuAtStart = cpu;
                if (share <= MaxCpuShare)
                {
                    _runaways = 0;
                    continue;
                }
                _runaways++;
                Reopens++;
                Log.Warn($"App audio \"{_label}\" (process {_processId}): Windows' capture was using {share * 100:F0}% of a CPU core, reopening it" +
                         (_runaways > 1 ? $" (again, {_runaways} times in a row)" : ""));
                break;
            }
            try { client.Stop(); } catch { }
        }
        finally
        {
            client.Dispose();
        }
    }

    private AudioClient Initialize(MMDevice device)
    {
        AudioClientStreamFlags baseFlags = _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
        const long bufferDuration = 2_000_000; // 200 ms, in 100 ns units

        // Preferred: let Windows hand us 48 kHz 16-bit directly.
        AudioClient client = device.AudioClient;
        try
        {
            int channels = _loopback ? 2 : 1;
            client.Initialize(AudioClientShareMode.Shared, baseFlags | AutoConvert, bufferDuration, 0,
                new WaveFormat(AudioRing.SampleRate, 16, channels), Guid.Empty);
            _direct = true;
            _srcRate = AudioRing.SampleRate;
            _srcChannels = channels;
            _srcBits = 16;
            _srcFloat = false;
            return client;
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException && (uint)ex.HResult != 0x80070005)
        {
            client.Dispose();
            Log.Info($"{_label}: direct format refused (0x{(uint)ex.HResult:X8}), converting from the device format instead");
        }

        // Fallback: take the device's own format and convert it here.
        client = device.AudioClient;
        try
        {
            WaveFormat mix = client.MixFormat;
            bool isFloat = mix.Encoding == WaveFormatEncoding.IeeeFloat
                           || (mix is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            if (!(isFloat && mix.BitsPerSample == 32) && mix.BitsPerSample != 16)
                throw new NotSupportedException($"Unsupported device format: {mix}");
            client.Initialize(AudioClientShareMode.Shared, baseFlags, bufferDuration, 0, mix, Guid.Empty);
            _direct = false;
            _srcRate = mix.SampleRate;
            _srcChannels = mix.Channels;
            _srcBits = mix.BitsPerSample;
            _srcFloat = isFloat;
            _resamplePos = 0;
            _prevL = _prevR = 0;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Wall-clock time of a packet's first frame. WASAPI reports it on the performance-counter clock.</summary>
    private double PacketTime(int frames, long qpc100ns)
    {
        double now = Clock.UtcNow();
        double age = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - qpc100ns / 1e7;
        return qpc100ns > 0 && age > -0.005 && age < 2.0
            ? now - Math.Max(age, 0)
            : now - frames / (double)_srcRate;
    }

    /// <summary>
    /// App capture: silence is not stored but counted, and handed to the ring as a gap (at least once a second so
    /// old audio can still be trimmed), so the next sound lands sample-exactly where it was played.
    /// </summary>
    private void AddGap(double time, int frames)
    {
        if (_gapFrames == 0) _gapTime = time;
        _gapFrames += frames;
        if (_gapFrames >= AudioRing.SampleRate) FlushGap();
    }

    private void FlushGap()
    {
        if (_gapFrames == 0) return;
        Ring.AppendGap(_gapTime, _gapFrames, _resync);
        _resync = false;
        _gapFrames = 0;
    }

    private void Handle(IntPtr buffer, int frames, long qpc100ns)
    {
        double time = PacketTime(frames, qpc100ns);

        int outFrames = Convert(buffer, frames);
        if (outFrames <= 0) return;
        Span<short> samples = _scratch.AsSpan(0, outFrames * 2);

        int max = 0;
        foreach (short s in samples)
        {
            int a = s < 0 ? -s : s;
            if (a > max) max = a;
        }
        float peak = max / 32768f;
        if (peak > _peak) _peak = peak;

        if (_processId > 0)
        {
            // An app that plays nothing still delivers packets of +-1 noise. Keeping them would cost about 20 MB per
            // idle app, so they are only counted. Placing the next sound by its own timestamp instead shifted it by a
            // few milliseconds each time (enough to scramble speech between words in Discord).
            if (max <= 2)
            {
                AddGap(time, outFrames);
                return;
            }
            FlushGap();
            Ring.Append(time, samples, _resync);
            _resync = false;
            return;
        }
        Ring.Append(time, samples);
    }

    private void EnsureScratch(int frames)
    {
        if (_scratch.Length < frames * 2) _scratch = new short[frames * 2 + 1024];
    }

    /// <summary>Converts a device buffer to 48 kHz stereo 16-bit in <see cref="_scratch"/>; returns the frame count.</summary>
    private int Convert(IntPtr buffer, int frames)
    {
        if (_direct)
        {
            EnsureScratch(frames);
            var src = new ReadOnlySpan<short>((void*)buffer, frames * _srcChannels);
            if (_srcChannels == 2)
            {
                src.CopyTo(_scratch);
            }
            else
            {
                for (int i = 0; i < frames; i++) _scratch[i * 2] = _scratch[i * 2 + 1] = src[i];
            }
            return frames;
        }

        // Pull the first two channels out as floats.
        if (_left.Length < frames)
        {
            _left = new float[frames + 512];
            _right = new float[frames + 512];
        }
        int ch = _srcChannels;
        if (_srcFloat)
        {
            var src = new ReadOnlySpan<float>((void*)buffer, frames * ch);
            for (int i = 0; i < frames; i++)
            {
                _left[i] = src[i * ch];
                _right[i] = ch < 2 ? src[i * ch] : src[i * ch + 1];
            }
        }
        else
        {
            var src = new ReadOnlySpan<short>((void*)buffer, frames * ch);
            for (int i = 0; i < frames; i++)
            {
                _left[i] = src[i * ch] / 32768f;
                _right[i] = (ch < 2 ? src[i * ch] : src[i * ch + 1]) / 32768f;
            }
        }

        // Linear resampling; the position carries over between buffers so there are no seams.
        double step = _srcRate / (double)AudioRing.SampleRate;
        EnsureScratch((int)(frames / step) + 8);
        int n = 0;
        double t = _resamplePos;
        while (t < frames - 1)
        {
            int i = (int)Math.Floor(t);
            float frac = (float)(t - i);
            float l0 = i < 0 ? _prevL : _left[i], r0 = i < 0 ? _prevR : _right[i];
            float l = l0 + (_left[i + 1] - l0) * frac, r = r0 + (_right[i + 1] - r0) * frac;
            _scratch[n * 2] = (short)Math.Clamp((int)(l * 32767f), short.MinValue, short.MaxValue);
            _scratch[n * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), short.MinValue, short.MaxValue);
            n++;
            t += step;
        }
        _resamplePos = t - frames;
        _prevL = _left[frames - 1];
        _prevR = _right[frames - 1];
        return n;
    }

    /// <summary>Active microphones, for the settings page.</summary>
    public static List<(string Id, string Name)> ListMicrophones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add((d.ID, d.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list microphones: " + ex.Message);
        }
        return list;
    }
}
