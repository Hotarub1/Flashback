using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback.Core;

/// <summary>
/// Plays a clip's decoded audio tracks through the default output, each at its own volume, so the player can
/// rebalance the apps and the microphone while the video plays. The video player drives the clock: the window
/// compares <see cref="HeardSeconds"/> with the video position every few milliseconds and calls <see cref="Seek"/>
/// when they drift apart. The output stream runs all the time and plays silence while paused, so pausing, seeking
/// and resuming never wait on the sound card.
/// </summary>
public sealed class StemPlayer : IDisposable
{
    private const int LatencyMs = 60;
    private const long LatencyFrames = ClipAudio.SampleRate * LatencyMs / 1000;

    private readonly Provider _provider;
    private WasapiOut? _out;

    /// <summary>Raised (on the thread that created the player) when the output device stopped working.</summary>
    public event Action<string>? Failed;

    private StemPlayer(ClipStems stems) => _provider = new Provider(stems);

    /// <summary>Opens the default output device. Null, after logging, if that is not possible.</summary>
    public static StemPlayer? Create(ClipStems stems)
    {
        var player = new StemPlayer(stems);
        try
        {
            var output = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMs);
            output.Init(player._provider);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) player.Failed?.Invoke(e.Exception.Message);
            };
            output.Play();
            player._out = output;
            return player;
        }
        catch (Exception ex)
        {
            Log.Warn("Clip audio output could not be opened: " + ex.Message);
            player.Dispose();
            return null;
        }
    }

    /// <summary>Seek this far ahead of the video when starting, so the first audible sample lines up with the picture.</summary>
    public double LeadSeconds => LatencyMs / 1000.0 + 0.01;

    public bool Playing
    {
        get => _provider.Playing;
        set => _provider.Playing = value;
    }

    public bool Muted
    {
        get => _provider.Muted;
        set => _provider.Muted = value;
    }

    /// <summary>The player's overall volume (0 to 1), applied after the tracks are mixed. Only for listening; trims ignore it.</summary>
    public float Volume
    {
        get => _provider.Volume;
        set => _provider.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>One volume per track, in track order (1 = as recorded).</summary>
    public void SetGains(IReadOnlyList<float> gains)
    {
        var copy = new float[_provider.TrackCount];
        for (int i = 0; i < copy.Length; i++) copy[i] = i < gains.Count ? gains[i] : 1f;
        _provider.Gains = copy;
    }

    public void Seek(double seconds) => _provider.Seek((long)Math.Round(seconds * ClipAudio.SampleRate));

    /// <summary>The clip time the listener is hearing right now, allowing for the output buffer.</summary>
    public double HeardSeconds => _provider.HeardFrames(LatencyFrames) / (double)ClipAudio.SampleRate;

    public void Dispose()
    {
        WasapiOut? output = _out;
        _out = null;
        if (output == null) return;
        try
        {
            output.Stop();
            output.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing clip audio output: " + ex.Message);
        }
    }

    private sealed class Provider(ClipStems stems) : IWaveProvider
    {
        private readonly short[][] _tracks = stems.Tracks.Select(t => t.Pcm).ToArray();
        private readonly long _frames = stems.Frames;
        private readonly object _lock = new();
        private long _pos, _anchor;

        public volatile bool Playing;
        public volatile bool Muted;
        public volatile float Volume = 1f;
        /// <summary>Replaced as a whole, never changed in place, so the audio thread always sees one consistent set.</summary>
        public volatile float[] Gains = Enumerable.Repeat(1f, stems.Tracks.Count).ToArray();

        public int TrackCount => _tracks.Length;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(ClipAudio.SampleRate, 2);

        public void Seek(long frame)
        {
            lock (_lock)
            {
                _pos = frame;
                _anchor = frame;
            }
        }

        public long HeardFrames(long latency)
        {
            lock (_lock) return Math.Max(_anchor, _pos - latency);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 8;
            Span<float> output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 8));
            bool playing = Playing;
            long pos;
            lock (_lock)
            {
                pos = _pos;
                if (playing) _pos += frames;
            }
            float master = Volume / 32768f;
            if (!playing || Muted || master == 0f)
            {
                output.Clear();
                return frames * 8;
            }
            float[] gains = Gains;
            short[][] tracks = _tracks;
            for (int i = 0; i < frames; i++)
            {
                long f = pos + i;
                if (f < 0 || f >= _frames)
                {
                    output[i * 2] = output[i * 2 + 1] = 0f;
                    continue;
                }
                long s = f * 2;
                float l = 0, r = 0;
                for (int t = 0; t < tracks.Length; t++)
                {
                    float g = gains[t];
                    if (g == 0f) continue;
                    l += tracks[t][s] * g;
                    r += tracks[t][s + 1] * g;
                }
                output[i * 2] = Math.Clamp(l * master, -1f, 1f);
                output[i * 2 + 1] = Math.Clamp(r * master, -1f, 1f);
            }
            return frames * 8;
        }
    }
}
