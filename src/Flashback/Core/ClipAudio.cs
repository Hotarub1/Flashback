using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Flashback.Core;

/// <summary>What a clip file contains, as FFmpeg describes it.</summary>
public sealed record ClipInfo(double Seconds, int Width, int Height, double Fps, IReadOnlyList<string> AudioTitles)
{
    public int AudioStreams => AudioTitles.Count;

    /// <summary>
    /// True when the clip carries desktop audio and the microphone as separate tracks (streams 1 and 2) behind the
    /// mix in stream 0, which is how Flashback saves clips that have both.
    /// </summary>
    public bool HasStems => AudioStreams >= 3;
}

/// <summary>Up to two 48 kHz stereo 16-bit audio tracks of equal length: the desktop and the microphone, or one track alone.</summary>
public sealed class ClipStems
{
    public ClipStems(short[] first, short[]? second)
    {
        if (second != null && second.Length != first.Length) throw new ArgumentException("Stems must have the same length");
        First = first;
        Second = second;
    }

    public short[] First { get; }
    public short[]? Second { get; }
    public bool Both => Second != null;
    public long Frames => First.Length / 2;
    public double Seconds => Frames / (double)ClipAudio.SampleRate;

    /// <summary>Channels on the pipe to FFmpeg: mix, first, second (six) or the one track (two).</summary>
    public int PipeChannels => Both ? 6 : 2;

    /// <summary>
    /// Writes the audio as interleaved 16-bit samples for FFmpeg. With two stems every frame carries the mix
    /// (the sum, clamped like the recorder always mixed it) followed by each stem on its own.
    /// </summary>
    public void WriteTo(Stream stdin)
    {
        if (!Both)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(First.AsSpan());
            for (int i = 0; i < bytes.Length; i += 1 << 16)
                stdin.Write(bytes.Slice(i, Math.Min(1 << 16, bytes.Length - i)));
            return;
        }
        const int chunkFrames = 4096;
        var chunk = new short[chunkFrames * 6];
        short[] a = First, b = Second!;
        long frames = Frames;
        for (long f = 0; f < frames; f += chunkFrames)
        {
            int n = (int)Math.Min(chunkFrames, frames - f);
            for (int i = 0; i < n; i++)
            {
                long s = (f + i) * 2;
                int o = i * 6;
                chunk[o] = (short)Math.Clamp(a[s] + b[s], short.MinValue, short.MaxValue);
                chunk[o + 1] = (short)Math.Clamp(a[s + 1] + b[s + 1], short.MinValue, short.MaxValue);
                chunk[o + 2] = a[s];
                chunk[o + 3] = a[s + 1];
                chunk[o + 4] = b[s];
                chunk[o + 5] = b[s + 1];
            }
            stdin.Write(MemoryMarshal.AsBytes(chunk.AsSpan(0, n * 6)));
        }
    }
}

/// <summary>
/// How audio is laid out in a clip, and how to read it back. A clip with both desktop audio and the microphone
/// carries three AAC tracks: the mix that every player uses by default, then the desktop and the microphone on their
/// own so the player can rebalance them afterwards. A clip with one source carries that one track only.
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
    public static void AddOutputs(List<string> args, ClipStems stems, int inputIndex, string? singleTitle, bool mixOnly = false)
    {
        if (stems.Both && !mixOnly)
        {
            // Six channels come in as one stream; pan picks each pair out again.
            args.AddRange(["-filter_complex",
                $"[{inputIndex}:a]asplit=3[x][y][z];[x]pan=stereo|c0=c0|c1=c1[mix];[y]pan=stereo|c0=c2|c1=c3[one];[z]pan=stereo|c0=c4|c1=c5[two]",
                "-map", "[mix]", "-map", "[one]", "-map", "[two]"]);
            AddCodec(args, [MixTitle, DesktopTitle, MicTitle]);
        }
        else if (stems.Both)
        {
            // Fallback: only the mix, exactly as clips were saved before stems existed.
            args.AddRange(["-filter_complex", $"[{inputIndex}:a]pan=stereo|c0=c0|c1=c1[mix]", "-map", "[mix]"]);
            AddCodec(args, [MixTitle]);
        }
        else
        {
            args.AddRange(["-map", $"{inputIndex}:a:0"]);
            AddCodec(args, [singleTitle ?? MixTitle]);
        }
    }

    /// <summary>Adds the AAC encoder, bitrates, titles and default-track flags for <paramref name="titles"/> audio tracks.</summary>
    public static void AddCodec(List<string> args, string[] titles)
    {
        args.AddRange(["-c:a", "aac"]);
        for (int i = 0; i < titles.Length; i++)
        {
            args.AddRange([$"-b:a:{i}", i == 0 ? "192k" : titles[i] == MicTitle ? "128k" : "160k"]);
            args.AddRange([$"-metadata:s:a:{i}", "title=" + titles[i]]);
            args.AddRange([$"-disposition:a:{i}", i == 0 ? "default" : "0"]);
        }
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
    /// Decodes the clip's audio for the player: the desktop and microphone stems when the clip has them, otherwise
    /// its one track. Bounded by <paramref name="maxSeconds"/> so a stray long video cannot eat all memory.
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

        if (!info.HasStems) return new ClipStems(Load(0), null);
        short[] a = Load(1), b = Load(2);
        if (a.Length != b.Length)
        {
            // AAC padding can differ by a few samples; line the stems up on the longer one.
            int n = Math.Max(a.Length, b.Length);
            Array.Resize(ref a, n);
            Array.Resize(ref b, n);
        }
        return new ClipStems(a, b);
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
