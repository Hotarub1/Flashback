using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback.Core;

/// <summary>
/// Plays a clip's decoded audio tracks through the default output, each at its own volume, so the player can
/// rebalance desktop audio and the microphone while the video plays. The video player drives the clock: the window
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

    public void SetGains(float first, float second)
    {
        _provider.FirstGain = first;
        _provider.SecondGain = second;
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
        private readonly short[] _first = stems.First;
        private readonly short[]? _second = stems.Second;
        private readonly long _frames = stems.Frames;
        private readonly object _lock = new();
        private long _pos, _anchor;

        public volatile bool Playing;
        public volatile bool Muted;
        public volatile float FirstGain = 1f;
        public volatile float SecondGain = 1f;

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
            if (!playing || Muted)
            {
                output.Clear();
                return frames * 8;
            }
            float ga = FirstGain, gb = SecondGain;
            short[] a = _first;
            short[]? b = _second;
            for (int i = 0; i < frames; i++)
            {
                long f = pos + i;
                if (f < 0 || f >= _frames)
                {
                    output[i * 2] = output[i * 2 + 1] = 0f;
                    continue;
                }
                long s = f * 2;
                float l = a[s] * ga, r = a[s + 1] * ga;
                if (b != null)
                {
                    l += b[s] * gb;
                    r += b[s + 1] * gb;
                }
                output[i * 2] = Math.Clamp(l / 32768f, -1f, 1f);
                output[i * 2 + 1] = Math.Clamp(r / 32768f, -1f, 1f);
            }
            return frames * 8;
        }
    }
}
