namespace Flashback.Core;

/// <summary>
/// In-memory history of one audio source as 48 kHz stereo 16-bit samples. Every captured packet keeps the
/// wall-clock time it was recorded at, so any time range can later be laid out exactly against the video.
/// Silence (no packets) simply leaves a gap.
/// </summary>
public sealed class AudioRing
{
    public const int SampleRate = 48000;
    private const int BlockFrames = 16384;          // 64 KB blocks stay off the large-object heap
    private const int ForwardTolerance = 1440;      // 30 ms: larger forward jumps are real gaps
    private const int BackwardTolerance = 4800;     // 100 ms

    private readonly struct Packet(double time, long start, int frames, bool resync, bool gap)
    {
        public readonly double Time = time;
        public readonly long Start = start;
        public readonly int Frames = frames;
        public readonly bool Resync = resync;
        /// <summary>Silence that was not stored: it only keeps the packets after it in their exact place.</summary>
        public readonly bool Gap = gap;
    }

    private readonly object _lock = new();
    private readonly List<short[]> _blocks = new();
    private readonly Stack<short[]> _pool = new();
    private readonly Queue<Packet> _packets = new();
    private long _firstBlockFrame;
    private long _writeFrame;

    /// <summary>Seconds of history to keep.</summary>
    public volatile int RetainSeconds = 50;

    public long TotalFrames { get; private set; }

    /// <summary>Wall-clock time the newest captured audio ends at; 0 before any audio arrived.</summary>
    public double LastUtc { get; private set; }
    public long TotalPackets { get; private set; }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (short[] b in _blocks) _pool.Push(b);
            _blocks.Clear();
            _packets.Clear();
            // Realign so the next write starts on a block boundary.
            _firstBlockFrame = _writeFrame = (_writeFrame / BlockFrames + 1) * BlockFrames;
        }
    }

    /// <param name="timeUtc">Wall-clock time of the first frame.</param>
    /// <param name="stereo">Interleaved left/right samples.</param>
    /// <param name="resync">Packets before this one were left out on purpose: place it by its own time even if the gap is short.</param>
    public void Append(double timeUtc, ReadOnlySpan<short> stereo, bool resync = false)
    {
        int frames = stereo.Length / 2;
        if (frames == 0) return;
        lock (_lock)
        {
            long start = _writeFrame;
            int done = 0;
            while (done < frames)
            {
                long rel = _writeFrame - _firstBlockFrame;
                int blockIndex = (int)(rel / BlockFrames);
                int offset = (int)(rel % BlockFrames);
                while (blockIndex >= _blocks.Count)
                    _blocks.Add(_pool.Count > 0 ? _pool.Pop() : new short[BlockFrames * 2]);
                int n = Math.Min(frames - done, BlockFrames - offset);
                stereo.Slice(done * 2, n * 2).CopyTo(_blocks[blockIndex].AsSpan(offset * 2));
                done += n;
                _writeFrame += n;
            }
            _packets.Enqueue(new Packet(timeUtc, start, frames, resync, false));
            TotalFrames += frames;
            LastUtc = timeUtc + frames / (double)SampleRate;
            TotalPackets++;
            Trim(timeUtc - RetainSeconds);
        }
    }

    /// <summary>
    /// Records <paramref name="frames"/> of silence without storing it. The audio after it then follows on sample for
    /// sample, exactly as if the silence had been kept, instead of being placed by its own (slightly jittery) time.
    /// </summary>
    public void AppendGap(double timeUtc, int frames, bool resync = false)
    {
        if (frames <= 0) return;
        lock (_lock)
        {
            _packets.Enqueue(new Packet(timeUtc, _writeFrame, frames, resync, true));
            Trim(timeUtc - RetainSeconds);
        }
    }

    private void Trim(double cutoff)
    {
        while (_packets.Count > 0)
        {
            Packet p = _packets.Peek();
            if (p.Time + p.Frames / (double)SampleRate >= cutoff) break;
            _packets.Dequeue();
        }
        long keepFrom = _packets.Count > 0 ? _packets.Peek().Start : _writeFrame;
        while (_blocks.Count > 1 && _firstBlockFrame + BlockFrames <= keepFrom)
        {
            if (_pool.Count < 8) _pool.Push(_blocks[0]);
            _blocks.RemoveAt(0);
            _firstBlockFrame += BlockFrames;
        }
    }

    /// <summary>
    /// Mixes this source's audio for [startUtc, endUtc) into <paramref name="dest"/> (interleaved stereo),
    /// beginning at frame <paramref name="destFrame"/>. Returns how many frames of real audio were written.
    /// </summary>
    public long Render(double startUtc, double endUtc, short[] dest, long destFrame, float gain)
    {
        long total = (long)Math.Round((endUtc - startUtc) * SampleRate);
        total = Math.Min(total, dest.Length / 2 - destFrame);
        if (total <= 0 || gain <= 0f) return 0;
        long written = 0;
        lock (_lock)
        {
            long pos = long.MinValue;
            foreach (Packet p in _packets)
            {
                if (p.Time + p.Frames / (double)SampleRate <= startUtc) continue;
                if (p.Time >= endUtc) break;
                long ideal = (long)Math.Round((p.Time - startUtc) * SampleRate);
                // Packets normally follow each other exactly. Only re-anchor on a real gap or a clock jump,
                // so tiny timestamp jitter never causes clicks.
                if (pos == long.MinValue || p.Resync || ideal - pos > ForwardTolerance || pos - ideal > BackwardTolerance) pos = ideal;

                if (p.Gap)
                {
                    pos += p.Frames;
                    continue;
                }
                long from = Math.Max(0, -pos);
                long to = Math.Min(p.Frames, total - pos);
                for (long i = from; i < to;)
                {
                    long rel = p.Start + i - _firstBlockFrame;
                    if (rel < 0) { i = Math.Min(to, i - rel); continue; }
                    int blockIndex = (int)(rel / BlockFrames);
                    if (blockIndex >= _blocks.Count) break;
                    int offset = (int)(rel % BlockFrames);
                    int n = (int)Math.Min(to - i, BlockFrames - offset);
                    short[] src = _blocks[blockIndex];
                    long d = (destFrame + pos + i) * 2;
                    int s = offset * 2;
                    for (int k = 0; k < n * 2; k++)
                    {
                        int v = dest[d + k] + (int)(src[s + k] * gain);
                        dest[d + k] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                    }
                    i += n;
                    written += n;
                }
                pos += p.Frames;
            }
        }
        return written;
    }
}
