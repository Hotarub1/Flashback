using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Flashback.Core;

/// <summary>What a clip file contains, as FFmpeg describes it.</summary>
public sealed record ClipInfo(double Seconds, int Width, int Height, double Fps, IReadOnlyList<string> AudioTitles)
{
    public int AudioStreams => AudioTitles.Count;

    /// <summary>
    /// True when the clip carries separate tracks (desktop audio or each app, and the microphone) behind the mix in
    /// stream 0, which is how Flashback saves clips with more than one source.
    /// </summary>
    public bool HasStems => AudioStreams >= 3 || (AudioStreams == 2 && AudioTitles[0] == ClipAudio.MixTitle);

    /// <summary>The audio streams the player and the trim editor work with: the separate tracks, or the one track.</summary>
    public IReadOnlyList<int> StemStreams => HasStems ? Enumerable.Range(1, AudioStreams - 1).ToList() : AudioStreams > 0 ? [0] : [];
}

/// <summary>One audio track of a clip: 48 kHz stereo 16-bit samples and the title stored with it.</summary>
public sealed record ClipTrack(string Title, short[] Pcm);

/// <summary>
/// Audio tracks of equal length for a clip, and what goes into its mix. With one track that track is the clip's only
/// audio. With several, the clip gets the mix first (what every player plays) and then each track on its own.
/// </summary>
public sealed class ClipStems
{
    private readonly IReadOnlyList<short[]> _mixParts;

    /// <param name="mixParts">What the mix is the sum of. By default the tracks themselves; separate app tracks pass
    /// the full desktop capture instead, so sounds no app track caught stay in the mix.</param>
    public ClipStems(IReadOnlyList<ClipTrack> tracks, IReadOnlyList<short[]>? mixParts = null)
    {
        if (tracks.Count == 0) throw new ArgumentException("A clip needs at least one audio track");
        int length = tracks[0].Pcm.Length;
        if (tracks.Any(t => t.Pcm.Length != length) || mixParts?.Any(m => m.Length != length) == true)
            throw new ArgumentException("Audio tracks must have the same length");
        if (tracks.Count == 1 && mixParts != null) throw new ArgumentException("A single track is its own mix");
        Tracks = tracks;
        _mixParts = mixParts ?? tracks.Select(t => t.Pcm).ToList();
    }

    public IReadOnlyList<ClipTrack> Tracks { get; }
    public bool Separate => Tracks.Count > 1;
    public long Frames => Tracks[0].Pcm.Length / 2;
    public double Seconds => Frames / (double)ClipAudio.SampleRate;

    /// <summary>Channels on the pipe to FFmpeg: the mix and then each track (two each), or the one track.</summary>
    public int PipeChannels => Separate ? 2 * (Tracks.Count + 1) : 2;

    /// <summary>
    /// Writes the audio as interleaved 16-bit samples for FFmpeg. With several tracks every frame carries the mix
    /// (the sum, clamped like the recorder always mixed it) followed by each track on its own.
    /// </summary>
    public void WriteTo(Stream stdin)
    {
        if (!Separate)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(Tracks[0].Pcm.AsSpan());
            for (int i = 0; i < bytes.Length; i += 1 << 16)
                stdin.Write(bytes.Slice(i, Math.Min(1 << 16, bytes.Length - i)));
            return;
        }
        const int chunkFrames = 4096;
        int channels = PipeChannels;
        var chunk = new short[chunkFrames * channels];
        short[][] mix = _mixParts.ToArray();
        short[][] tracks = Tracks.Select(t => t.Pcm).ToArray();
        long frames = Frames;
        for (long f = 0; f < frames; f += chunkFrames)
        {
            int n = (int)Math.Min(chunkFrames, frames - f);
            for (int i = 0; i < n; i++)
            {
                long s = (f + i) * 2;
                int o = i * channels;
                int l = 0, r = 0;
                foreach (short[] m in mix)
                {
                    l += m[s];
                    r += m[s + 1];
                }
                chunk[o] = (short)Math.Clamp(l, short.MinValue, short.MaxValue);
                chunk[o + 1] = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
                for (int t = 0; t < tracks.Length; t++)
                {
                    chunk[o + 2 + t * 2] = tracks[t][s];
                    chunk[o + 3 + t * 2] = tracks[t][s + 1];
                }
            }
            stdin.Write(MemoryMarshal.AsBytes(chunk.AsSpan(0, n * channels)));
        }
    }
}

/// <summary>
/// How audio is laid out in a clip, and how to read it back. A clip with more than one source carries the mix that
/// every player uses by default, then each source on its own (desktop audio or one track per app, then the
/// microphone) so the player can rebalance them afterwards. A clip with one source carries that one track only.
/// </summary>
public static partial class ClipAudio
{
    public const int SampleRate = AudioRing.SampleRate;
    public const string MixTitle = "Mix";
    public const string DesktopTitle = "Desktop";
    public const string MicTitle = "Microphone";

    /// <summary>The waveform shown in the player: one peak per this many seconds.</summary>
    public const int PeaksPerSecond = 100;

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Stream #0:(\d+).*?: (Video|Audio): ")]
    private static partial Regex StreamRegex();

    [GeneratedRegex(@"Video: .*?,\s(\d{2,5})x(\d{2,5})")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([\d.]+) (fps|tbr)")]
    private static partial Regex FpsRegex();

    // The MP4 muxer stores a track title in a "name" box, which FFmpeg reads back under that key.
    [GeneratedRegex(@"^\s+(?:title|name)\s*:\s*(.+?)\s*$")]
    private static partial Regex TitleRegex();

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// The FFmpeg input for the stems on stdin. Add before the maps; the input index is the position of this input.
    /// </summary>
    public static void AddPipeInput(List<string> args, ClipStems stems) =>
        args.AddRange(["-f", "s16le", "-ar", SampleRate.ToString(), "-ac", stems.PipeChannels.ToString(), "-i", "pipe:0"]);

    /// <summary>
    /// Maps and encodes the audio from <see cref="AddPipeInput"/> (input <paramref name="inputIndex"/>). Add right after
    /// the video map so the mix is the first audio track.
    /// </summary>
    public static void AddOutputs(List<string> args, ClipStems stems, int inputIndex, bool mixOnly = false)
    {
        if (stems.Separate && !mixOnly)
        {
            // All channels come in as one stream; pan picks each pair out again.
            int n = stems.Tracks.Count + 1;
            var graph = new StringBuilder($"[{inputIndex}:a]asplit={n}");
            for (int i = 0; i < n; i++) graph.Append($"[s{i}]");
            for (int i = 0; i < n; i++) graph.Append($";[s{i}]pan=stereo|c0=c{i * 2}|c1=c{i * 2 + 1}[o{i}]");
            args.AddRange(["-filter_complex", graph.ToString()]);
            for (int i = 0; i < n; i++) args.AddRange(["-map", $"[o{i}]"]);
            AddCodec(args, [MixTitle, .. stems.Tracks.Select(t => t.Title)]);
        }
        else if (stems.Separate)
        {
            // Fallback: only the mix, exactly as clips were saved before separate tracks existed.
            args.AddRange(["-filter_complex", $"[{inputIndex}:a]pan=stereo|c0=c0|c1=c1[mix]", "-map", "[mix]"]);
            AddCodec(args, [MixTitle]);
        }
        else
        {
            args.AddRange(["-map", $"{inputIndex}:a:0"]);
            AddCodec(args, [stems.Tracks[0].Title]);
        }
    }

    /// <summary>Adds the AAC encoder, bitrates, titles and default-track flags for <paramref name="titles"/> audio tracks.</summary>
    public static void AddCodec(List<string> args, string[] titles)
    {
        args.AddRange(["-c:a", "aac"]);
        for (int i = 0; i < titles.Length; i++)
        {
            args.AddRange([$"-b:a:{i}", i == 0 ? "192k" : titles[i] == MicTitle ? "128k" : "160k"]);
            args.AddRange([$"-metadata:s:a:{i}", "title=" + CleanTitle(titles[i])]);
            args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "0"]);
        }
    }

    /// <summary>A track title without anything that could upset FFmpeg's metadata handling.</summary>
    private static string CleanTitle(string title)
    {
        string clean = new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? "Audio" : clean.Length > 60 ? clean[..60] : clean;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Reads a clip's duration, frame size, frame rate and audio tracks. Null if FFmpeg cannot read the file.</summary>
    public static ClipInfo? Probe(string ffmpeg, string path, JobObject? job)
    {
        // With no output FFmpeg describes the input on stderr and exits with 1; that description is all we need.
        var (_, err) = Ffmpeg.Run(ffmpeg, ["-hide_banner", "-nostdin", "-i", path], 20000, job, null, lowPriority: true);
        Match d = DurationRegex().Match(err);
        if (!d.Success) return null;
        double seconds = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                         + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);

        int width = 0, height = 0;
        double fps = 0;
        var titles = new List<string>();
        int? audioIndex = null;
        foreach (string line in err.Split('\n'))
        {
            Match s = StreamRegex().Match(line);
            if (s.Success)
            {
                if (s.Groups[2].Value == "Video" && width == 0)
                {
                    Match size = SizeRegex().Match(line);
                    if (size.Success)
                    {
                        width = int.Parse(size.Groups[1].Value);
                        height = int.Parse(size.Groups[2].Value);
                    }
                    foreach (Match f in FpsRegex().Matches(line))
                    {
                        if (fps == 0 || f.Groups[2].Value == "fps")
                            fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (f.Groups[2].Value == "fps") break;
                    }
                    audioIndex = null;
                }
                else if (s.Groups[2].Value == "Audio")
                {
                    titles.Add("");
                    audioIndex = titles.Count - 1;
                }
                else audioIndex = null;
                continue;
            }
            if (audioIndex is int ai && titles[ai].Length == 0)
            {
                Match t = TitleRegex().Match(line);
                if (t.Success) titles[ai] = t.Groups[1].Value;
            }
        }
        if (fps <= 0 || fps > 480) fps = 60;
        return new ClipInfo(seconds, width, height, fps, titles);
    }

    /// <summary>
    /// Decodes the clip's audio for the player: its separate tracks when it has them, otherwise its one track.
    /// Bounded by <paramref name="maxSeconds"/> so a stray long video cannot eat all memory.
    /// </summary>
    public static ClipStems Decode(string ffmpeg, string path, ClipInfo info, int maxSeconds, JobObject? job)
    {
        int seconds = (int)Math.Ceiling(Math.Min(info.Seconds, maxSeconds)) + 1;
        long expected = (long)seconds * SampleRate * 4;
        int timeout = 30000 + seconds * 1000;

        short[] Load(int stream)
        {
            var (code, bytes, err) = Ffmpeg.RunCapture(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", $"0:a:{stream}", "-t", seconds.ToString(),
                 "-f", "s16le", "-ar", SampleRate.ToString(), "-ac", "2", "pipe:1"],
                timeout, job, expected + (1 << 20), expected);
            if (code != 0) throw new InvalidDataException($"Audio track {stream} could not be decoded ({code}): {Ffmpeg.LastLine(err)}");
            var pcm = new short[bytes.Length / 4 * 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            return pcm;
        }

        IReadOnlyList<int> streams = info.StemStreams;
        var pcms = new short[streams.Count][];
        Parallel.For(0, streams.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => pcms[i] = Load(streams[i]));
        // AAC padding can differ by a few samples; line the tracks up on the longest.
        int n = pcms.Max(p => p.Length);
        for (int i = 0; i < pcms.Length; i++)
        {
            if (pcms[i].Length != n) Array.Resize(ref pcms[i], n);
        }
        return new ClipStems(streams.Select((stream, i) => new ClipTrack(info.AudioTitles[stream], pcms[i])).ToList());
    }

    /// <summary>The loudest sample (0..1) in each <see cref="PeaksPerSecond"/>th of a second, for drawing a waveform.</summary>
    public static float[] Peaks(short[] stereo)
    {
        int bucket = SampleRate / PeaksPerSecond;
        long frames = stereo.Length / 2;
        var peaks = new float[(frames + bucket - 1) / bucket];
        for (int p = 0; p < peaks.Length; p++)
        {
            long from = (long)p * bucket * 2, to = Math.Min(stereo.Length, from + bucket * 2);
            int max = 0;
            for (long i = from; i < to; i++)
            {
                int v = stereo[i];
                if (v < 0) v = -v;
                if (v > max) max = v;
            }
            peaks[p] = max / 32768f;
        }
        return peaks;
    }
}
