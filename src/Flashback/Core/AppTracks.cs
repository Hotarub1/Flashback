namespace Flashback.Core;

/// <summary>
/// Turns the per-app captures into a clip's app tracks, lined up and levelled against the desktop capture.
///
/// The desktop capture is the reference: it is what the clip's mix is made from and it is in sync with the video.
/// Per-app capture runs through a different path in Windows: measured on the user's PC it arrived 12-40 ms later
/// and about 4.5 dB louder than the same sound in the desktop capture (output effects only touch the desktop path).
/// Worse, Windows mixes each app capture on a thread of its own, and when that thread is held up (a busy PC, another
/// capture opening) the capture loses some audio and from then on runs later still: 80 ms later after a 150 ms stall
/// in a test. So every app capture is lined up on every clip, piece by piece between the points where it lost audio,
/// by cross-correlation against the desktop sound, and each app's level is found by a least-squares fit.
///
/// Whatever the app tracks do not account for (an app that could not be captured, such as Steam while it runs a
/// game, Windows' own sounds, or the first moment of an app that just started) goes into an "Other sounds" track,
/// so the clip always gets its app tracks and the player still never loses a sound.
/// </summary>
public static class AppTracks
{
    private const int Rate = AudioRing.SampleRate;
    private const int MaxTracks = 8;
    /// <summary>Apps whose loudest sample stays below this (about -54 dBFS) played nothing worth a track.</summary>
    private const int SilentPeak = 64;
    private const int Decimation = 24;                 // 2 kHz for the coarse delay search
    /// <summary>How much earlier (100 ms) and later (500 ms) than the desktop sound an app capture may run.</summary>
    private const int LagBefore = Rate / 10, LagAfter = Rate / 2;
    /// <summary>
    /// When a delay is trusted: its score stands out from those of all other delays by <see cref="ClearPeak"/>
    /// robust standard deviations, or by <see cref="MinPeak"/> while the piece also correlates with the desktop
    /// sound by at least <see cref="MinRho"/>. Measured on the user's game: right delays scored 5 to 36 (correlation
    /// 0.84 to 0.97); the same audio against the wrong stretch of desktop sound scored 2 to 4.4 (correlation 0.05 or
    /// less). A piece without a trusted delay is left out of its app track, so its sound goes to "Other sounds"
    /// as it was, instead of an app track with the sound in the wrong place.
    /// </summary>
    private const double ClearPeak = 8, MinPeak = 4, MinRho = 0.2;
    /// <summary>A piece shorter than this (0.5 s) is too short to line up reliably (scraps right after a stall).</summary>
    private const int MinPieceFrames = Rate / 2;
    /// <summary>Below this share of the desktop sound explained, the apps were not lined up at all.</summary>
    private const double MinMatch = 0.05;
    /// <summary>
    /// An app whose level in the desktop sound is below this share of the loudest app's is not in the desktop sound
    /// at all (it plays on another device), so it gets no track: its track would add a sound the mix does not have.
    /// </summary>
    private const double MinRelativeLevel = 0.25;
    /// <summary>The leftover sound is judged in blocks of 10 ms, over a sliding 90 ms.</summary>
    private const int Block = Rate / 100;
    private const int SmoothBlocks = 4;
    /// <summary>Leftover sound is kept from 100 ms before to 100 ms after it is heard, so it starts and ends cleanly.</summary>
    private const int HoldBlocks = 10;
    /// <summary>Less leftover sound than this (0.2 s) is not worth a track of its own.</summary>
    private const int MinOtherBlocks = 20;
    /// <summary>Leftover sound quieter than about -50 dBFS (per stereo sample) is not counted.</summary>
    private const double AudibleSample = 100;

    public const string OtherTitle = "Other sounds";

    /// <summary>For tests: receives a line about every piece of every app capture that was lined up.</summary>
    internal static Action<string>? Trace;

    /// <summary>
    /// Builds one track per app that played sound in the clip, plus "Other sounds" for anything they leave out, or
    /// returns null (with the reason in <paramref name="note"/>) when the clip should keep a single desktop track.
    /// </summary>
    /// <param name="parts">The clip's uninterrupted stretches of video, in wall-clock time.</param>
    /// <param name="desktop">The desktop capture for the same stretches, as rendered for the clip.</param>
    public static List<ClipTrack>? Build(List<AppAudioRing> apps, List<(double Start, double End)> parts, long totalFrames,
        short[] desktop, out string note)
    {
        int[] dm = MonoSum([desktop], totalFrames);

        // One buffer per app (all processes of an app together), each process lined up on its own, dropping apps that
        // stayed silent. Flashback's own sound takes part in levelling and in the leftover, but never becomes a track.
        var groups = new List<Group>();
        var unaligned = new List<string>();
        foreach (var g in apps.GroupBy(a => a.Key))
        {
            short[] pcm = new short[totalFrames * 2];
            var lags = new List<int>();
            bool heard = false, lined = false;
            foreach (AppAudioRing a in g)
            {
                var (h, l) = Align(a.Name, a.Ring, parts, dm, pcm, lags);
                heard |= h;
                lined |= l;
            }
            if (heard && !lined && !g.First().Own) unaligned.Add(g.Last().Name);
            if (lined && Peak(pcm) > SilentPeak) groups.Add(new Group(g.Last().Name, pcm, g.First().Own, lags));
        }
        string notLined = unaligned.Count == 0 ? "" : $"; could not be lined up: {string.Join(", ", unaligned)}";
        if (!groups.Any(g => !g.Own))
        {
            note = unaligned.Count == 0 ? "no app played sound" : $"the app sound could not be lined up with the desktop sound{notLined}";
            return null;
        }

        FitLevels(dm, groups);
        double loudest = groups.Where(g => !g.Own).Max(g => g.Level);
        var elsewhere = groups.Where(g => !g.Own && (g.Level <= 0 || g.Level < MinRelativeLevel * loudest)).ToList();
        foreach (Group g in elsewhere) groups.Remove(g);
        string measured = $"delays {string.Join(" / ", groups.Select(g => string.Join("->", g.Lags.Select(l => (l * 1000.0 / Rate).ToString("F1")))))} ms, " +
                          $"levels {string.Join(" / ", groups.Select(g => g.Level.ToString("F2")))}";
        string notInDesktop = notLined + (elsewhere.Count == 0 ? "" : $"; not in the desktop sound: {string.Join(", ", elsewhere.Select(g => g.Name))}");
        if (!groups.Any(g => !g.Own))
        {
            note = $"the app sound is not in the desktop sound (another output device?){notInDesktop}";
            return null;
        }

        // Match the desktop level, so the tracks at 100% add up to the clip's mix.
        foreach (Group g in groups) Scale(g.Pcm, (float)g.Level);
        short[] other = Leftover(desktop, groups, out double match, out int otherBlocks);
        if (match < MinMatch)
        {
            note = $"the app tracks do not match the desktop sound ({measured}, match {match * 100:F0}%){notInDesktop}";
            return null;
        }

        var shown = groups.Where(g => !g.Own).OrderByDescending(g => Energy(g.Pcm)).ToList();
        bool leftover = otherBlocks >= MinOtherBlocks, hasOther = leftover;
        int room = hasOther ? MaxTracks - 1 : MaxTracks;
        string merged = "";
        if (shown.Count > room)
        {
            // Too many apps for one slider each: the quietest go together with the leftover sound.
            room = MaxTracks - 1;
            merged = $", in other sounds: {string.Join(", ", shown.Skip(room).Select(g => g.Name))}";
            foreach (Group g in shown.Skip(room)) Add(other, g.Pcm);
            shown = shown.Take(room).ToList();
            hasOther = true;
        }
        var tracks = shown.Select(g => new ClipTrack(g.Name, g.Pcm)).ToList();
        if (hasOther) tracks.Add(new ClipTrack(OtherTitle, other));
        Disambiguate(tracks);
        note = $"{string.Join(", ", tracks.Select(t => t.Title))} ({measured}, match {match * 100:F0}%" +
               (leftover ? $", sound no app track has {otherBlocks / 100.0:F1} s" : "") + merged + $"){notInDesktop}";
        return tracks;
    }

    private sealed class Group(string name, short[] pcm, bool own, List<int> lags)
    {
        public readonly string Name = name;
        public readonly short[] Pcm = pcm;
        public readonly bool Own = own;
        /// <summary>The delays the app's captures were lined up with, in order, in frames.</summary>
        public readonly List<int> Lags = lags;
        public double Level;
    }

    /// <summary>
    /// Lines one app capture up with the desktop sound and adds it to <paramref name="dest"/>, piece by piece between
    /// the points where the capture lost audio. Returns whether it had any sound in the clip, and whether any of it
    /// could be lined up. Pieces that cannot be lined up are left out, so their sound ends up in "Other sounds".
    /// The delays used are added to <paramref name="lags"/>.
    /// </summary>
    private static (bool Heard, bool Lined) Align(string name, AudioRing ring, List<(double Start, double End)> parts, int[] desktop,
        short[] dest, List<int> lags)
    {
        bool heard = false, lined = false;
        long offset = 0;
        foreach (var (start, end) in parts)
        {
            long n = (long)Math.Round((end - start) * Rate);
            // The capture from LagBefore before the stretch to LagAfter after it: raw[j] lines up with
            // desktop[offset + j - LagBefore - lag].
            long rawFrames = n + LagBefore + LagAfter;
            var raw = new short[rawFrames * 2];
            var anchors = new List<long>();
            ring.Render(start - LagBefore / (double)Rate, end + LagAfter / (double)Rate, raw, 0, 1f, anchors);
            if (n <= 0 || Peak(raw) <= SilentPeak)
            {
                offset += n;
                continue;
            }
            heard = true;
            int[] mono = MonoSum([raw], rawFrames);
            var desk = new DeskSpan(desktop, offset, n);

            List<long> bounds = [0, .. anchors.Where(a => a > 0 && a < rawFrames).Distinct().Order(), rawFrames];
            var pieces = new List<(long From, long To, int Lag, bool Sure)>();
            var fits = new List<PieceFit>();
            for (int k = 0; k + 1 < bounds.Count; k++)
            {
                PieceFit f = PieceLag(desk, mono, bounds[k], bounds[k + 1]);
                fits.Add(f);
                pieces.Add((bounds[k], bounds[k + 1], f.Lag, f.Sure));
            }
            Trace?.Invoke($"{name}: " + string.Join(", ", pieces.Select((p, k) =>
                $"{(p.From - LagBefore) / (double)Rate:F2}-{(p.To - LagBefore) / (double)Rate:F2}s {(p.Sure ? "" : "unsure ")}{p.Lag * 1000.0 / Rate:F1} ms (peak {fits[k].Peak:F1}, r {fits[k].Rho:F2})")));
            if (!pieces.Any(p => p.Sure))
            {
                offset += n;
                continue;
            }
            lined = true;

            foreach (var p in pieces.Where(p => p.Sure))
            {
                if (lags.Count == 0 || lags[^1] != p.Lag) lags.Add(p.Lag);
                for (long j = p.From; j < p.To; j++)
                {
                    long i = j - LagBefore - p.Lag;
                    if (i < 0 || i >= n) continue;
                    long d = (offset + i) * 2;
                    dest[d] = (short)Math.Clamp(dest[d] + raw[j * 2], short.MinValue, short.MaxValue);
                    dest[d + 1] = (short)Math.Clamp(dest[d + 1] + raw[j * 2 + 1], short.MinValue, short.MaxValue);
                }
            }
            offset += n;
        }
        return (heard, lined);
    }

    /// <summary>One stretch of the clip's desktop sound (mono).</summary>
    private readonly record struct DeskSpan(int[] Mono, long Offset, long Frames)
    {
        public int this[long i] => Mono[Offset + i];
    }

    /// <summary>How well one piece of an app capture lined up with the desktop sound.</summary>
    /// <param name="Peak">How far the best delay's score stands out from the scores of all other delays, in robust
    /// standard deviations (median absolute deviation).</param>
    /// <param name="Rho">Correlation of the piece with the desktop sound at that delay.</param>
    private readonly record struct PieceFit(int Lag, bool Sure, double Peak, double Rho);

    /// <summary>
    /// The delay of raw[from, to) behind the desktop sound, and whether it is clear: a coarse search at 2 kHz over
    /// the whole range, then a fine one at full rate around the best coarse match.
    /// </summary>
    private static PieceFit PieceLag(DeskSpan desk, int[] raw, long from, long to)
    {
        const int d = Decimation;
        long nd = desk.Frames / d;
        long jFrom = (from + d - 1) / d, jTo = to / d;
        if (to - from < MinPieceFrames || nd < MinPieceFrames / d) return new PieceFit(0, false, 0, 0);
        // Block sums, then their differences: game sound is mostly bass, whose slow swings make every delay score
        // high; the differences leave a sharp peak at the right delay.
        double[] Whiten(Func<long, double> block, long count)
        {
            var x = new double[count];
            double prev = block(0);
            for (long k = 1; k < count; k++)
            {
                double v = block(k);
                x[k] = v - prev;
                prev = v;
            }
            return x;
        }
        double[] dd = Whiten(k => { double t = 0; for (int q = 0; q < d; q++) t += desk[k * d + q]; return t; }, nd);
        double[] rd = Whiten(k => { double t = 0; for (int q = 0; q < d; q++) t += raw[(jFrom + k) * d + q]; return t; }, jTo - jFrom);
        // raw block k lines up with desktop block k - LagBefore/d - lagBlocks.
        int lo = -LagBefore / d, hi = LagAfter / d;
        var scores = new double[hi - lo + 1];
        Parallel.For(lo, hi + 1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, l =>
        {
            double c = 0;
            long shift = LagBefore / d + l;
            long kFrom = Math.Max(jFrom + 1, shift + 1), kTo = Math.Min(jTo, nd + shift);
            for (long k = kFrom; k < kTo; k++) c += rd[k - jFrom] * dd[k - shift];
            scores[l - lo] = c;
        });
        int best = 0;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[best]) best = i;
        }
        var rest = scores.Where((_, i) => Math.Abs(i - best) > 4).Order().ToArray();
        double median = rest[rest.Length / 2];
        double mad = rest.Select(v => Math.Abs(v - median)).Order().ElementAt(rest.Length / 2) * 1.4826;
        double peak = mad > 0 ? (scores[best] - median) / mad : 0;

        int centre = (best + lo) * d;
        double Score(int lag, int stride)
        {
            double c = 0;
            long shift = LagBefore + lag;
            long jStart = Math.Max(from, shift), jEnd = Math.Min(to, desk.Frames + shift);
            for (long j = jStart; j < jEnd; j += stride) c += (double)raw[j] * desk[j - shift];
            return c;
        }
        const int step = 8, radius = 2 * d;
        (int coarse, _) = BestLag(0, 2 * radius / step, k => Score(centre - radius + k * step, 6));
        int around = centre - radius + coarse * step;
        (int fine, _) = BestLag(around - step, around + step, l => Score(l, 3));

        double cc = 0, ea = 0, ed = 0;
        long s0 = LagBefore + fine;
        for (long j = Math.Max(from, s0), e = Math.Min(to, desk.Frames + s0); j < e; j += 3)
        {
            double a = raw[j], v = desk[j - s0];
            cc += a * v;
            ea += a * a;
            ed += v * v;
        }
        double rho = ea > 0 && ed > 0 ? cc / Math.Sqrt(ea * ed) : 0;
        bool sure = scores[best] > 0 && (peak >= ClearPeak || (peak >= MinPeak && rho >= MinRho));
        return new PieceFit(fine, sure, peak, rho);
    }

    private static int[] MonoSum(IEnumerable<short[]> tracks, long frames)
    {
        var mono = new int[frames];
        foreach (short[] t in tracks)
        {
            for (long i = 0; i < frames; i++) mono[i] += t[i * 2] + t[i * 2 + 1];
        }
        return mono;
    }

    /// <summary>The lag in [from, to] with the highest score, scoring lags on several cores at once.</summary>
    private static (int Lag, double Score) BestLag(int from, int to, Func<int, double> score)
    {
        var scores = new double[to - from + 1];
        Parallel.For(from, to + 1, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            l => scores[l - from] = score(l));
        int best = 0;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[best]) best = i;
        }
        return (best + from, scores[best]);
    }

    /// <summary>
    /// Each app's level in the desktop sound, by least squares over all apps together (they play different sounds,
    /// so this is close to fitting each on its own, but stays right when two apps play the same thing). An app that
    /// is not in the desktop sound at all comes out near zero.
    /// </summary>
    private static void FitLevels(int[] desktop, List<Group> groups)
    {
        int n = groups.Count;
        long frames = desktop.Length;
        var pcms = groups.Select(g => g.Pcm).ToArray();
        // Sums of products over chunks of the clip, on several cores, added up at the end.
        const int chunk = 1 << 16;
        int chunks = (int)((frames + chunk - 1) / chunk);
        var aa = new double[chunks][];
        var da = new double[chunks][];
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, c =>
        {
            var m = new double[n * n];
            var v = new double[n];
            var a = new double[n];
            long end = Math.Min(frames, (long)(c + 1) * chunk);
            for (long i = (long)c * chunk; i < end; i++)
            {
                for (int k = 0; k < n; k++) a[k] = pcms[k][i * 2] + pcms[k][i * 2 + 1];
                double d = desktop[i];
                for (int k = 0; k < n; k++)
                {
                    if (a[k] == 0) continue;
                    v[k] += d * a[k];
                    for (int j = k; j < n; j++) m[k * n + j] += a[k] * a[j];
                }
            }
            aa[c] = m;
            da[c] = v;
        });
        var g = new double[n, n];
        var b = new double[n];
        for (int c = 0; c < chunks; c++)
        {
            for (int k = 0; k < n; k++)
            {
                b[k] += da[c][k];
                for (int j = k; j < n; j++) g[k, j] += aa[c][k * n + j];
            }
        }
        for (int k = 0; k < n; k++)
        {
            for (int j = 0; j < k; j++) g[k, j] = g[j, k];
        }
        double[] levels = Solve(g, b);
        for (int k = 0; k < n; k++) groups[k].Level = double.IsFinite(levels[k]) ? Math.Clamp(levels[k], 0, 8) : 0;
    }

    /// <summary>Solves g x = b by Gaussian elimination, with a touch of damping so a silent or duplicate app cannot blow it up.</summary>
    private static double[] Solve(double[,] g, double[] b)
    {
        int n = b.Length;
        var m = (double[,])g.Clone();
        var x = (double[])b.Clone();
        for (int k = 0; k < n; k++) m[k, k] += 1e-6 * m[k, k] + 1e-9;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int r = k + 1; r < n; r++)
            {
                if (Math.Abs(m[r, k]) > Math.Abs(m[p, k])) p = r;
            }
            if (p != k)
            {
                for (int c = 0; c < n; c++) (m[k, c], m[p, c]) = (m[p, c], m[k, c]);
                (x[k], x[p]) = (x[p], x[k]);
            }
            if (m[k, k] == 0) continue;
            for (int r = k + 1; r < n; r++)
            {
                double f = m[r, k] / m[k, k];
                if (f == 0) continue;
                for (int c = k; c < n; c++) m[r, c] -= f * m[k, c];
                x[r] -= f * x[k];
            }
        }
        for (int k = n - 1; k >= 0; k--)
        {
            double s = x[k];
            for (int c = k + 1; c < n; c++) s -= m[k, c] * x[c];
            x[k] = m[k, k] == 0 ? 0 : s / m[k, k];
        }
        return x;
    }

    /// <summary>
    /// The desktop sound the app tracks leave out, for the "Other sounds" track. Subtracting the apps never cancels
    /// them perfectly (the desktop path adds effects), so what is left of them is a faint echo that would stay
    /// behind when an app is turned down. The leftover is therefore only kept where it is clearly louder than that
    /// echo: where something else was playing. <paramref name="match"/> is the share of the desktop sound the
    /// apps explain; <paramref name="otherBlocks"/> counts the 10 ms blocks with other sound.
    /// </summary>
    private static short[] Leftover(short[] desktop, List<Group> groups, out double match, out int otherBlocks)
    {
        long frames = desktop.Length / 2;
        var rest = new int[desktop.Length];
        for (long i = 0; i < desktop.Length; i++) rest[i] = desktop[i];
        foreach (Group g in groups)
        {
            short[] p = g.Pcm;
            for (long i = 0; i < rest.Length; i++) rest[i] -= p[i];
        }

        // Per block: desktop energy, and the products needed to fit the apps' level locally. The speaker driver's
        // effects change the level of loud moments a little (a limiter, it seems), which a fixed level cannot follow;
        // fitted per window, such a change still counts as the apps' own sound, while an unrelated sound does not.
        int blocks = (int)((frames + Block - 1) / Block);
        var e = new double[blocks + 1];
        var da = new double[blocks + 1];
        var aa = new double[blocks + 1];
        double totalE = 0, totalR = 0;
        for (int b = 0; b < blocks; b++)
        {
            double se = 0, sda = 0, saa = 0, sr = 0;
            long end = Math.Min(frames, (long)(b + 1) * Block) * 2;
            for (long i = (long)b * Block * 2; i < end; i++)
            {
                double d = desktop[i], a = desktop[i] - rest[i];
                se += d * d;
                sda += d * a;
                saa += a * a;
                sr += (double)rest[i] * rest[i];
            }
            // Prefix sums, for the sliding window below.
            e[b + 1] = e[b] + se;
            da[b + 1] = da[b] + sda;
            aa[b + 1] = aa[b] + saa;
            totalE += se;
            totalR += sr;
        }
        match = totalE > 0 ? Math.Max(0, 1 - totalR / totalE) : 0;

        // How loud the echo of the apps is, relative to the desktop sound, where the desktop sound is audible: most of
        // the time nothing else plays, so the median is the echo. Other sound has to be clearly above it.
        var window = new (double E, double R)[blocks];
        var scale = new float[blocks];
        double audible = AudibleSample * AudibleSample * Block * 2 * (2 * SmoothBlocks + 1);
        var ratios = new List<double>();
        for (int b = 0; b < blocks; b++)
        {
            int from = Math.Max(0, b - SmoothBlocks), to = Math.Min(blocks, b + SmoothBlocks + 1);
            double we = e[to] - e[from], wda = da[to] - da[from], waa = aa[to] - aa[from];
            double s = waa > 0 ? Math.Clamp(wda / waa, 0.5, 1.5) : 1;
            scale[b] = (float)s;
            window[b] = (we, Math.Max(0, we - 2 * s * wda + s * s * waa));
            if (window[b].E > audible) ratios.Add(window[b].R / window[b].E);
        }
        ratios.Sort();
        double echo = ratios.Count > 0 ? ratios[ratios.Count / 2] : 0;
        double threshold = Math.Clamp(4 * echo, 0.15, 0.5);

        var heard = new bool[blocks];
        otherBlocks = 0;
        for (int b = 0; b < blocks; b++)
        {
            heard[b] = window[b].R > audible && window[b].R > threshold * window[b].E;
            if (heard[b]) otherBlocks++;
        }
        if (otherBlocks < MinOtherBlocks) return new short[desktop.Length];

        // Keep it a little before and after, and fade in and out over a block so nothing clicks.
        var keep = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            if (!heard[b]) continue;
            for (int k = Math.Max(0, b - HoldBlocks); k <= Math.Min(blocks - 1, b + HoldBlocks); k++) keep[k] = 1;
        }
        var other = new short[desktop.Length];
        for (long f = 0; f < frames; f++)
        {
            // Linear between block centres.
            double pos = (f - Block / 2.0) / Block;
            int b0 = (int)Math.Floor(pos);
            float t = (float)(pos - b0);
            int i0 = Math.Clamp(b0, 0, blocks - 1), i1 = Math.Clamp(b0 + 1, 0, blocks - 1);
            float k = keep[i0] + (keep[i1] - keep[i0]) * t;
            if (k <= 0) continue;
            float s = scale[i0] + (scale[i1] - scale[i0]) * t;
            for (long c = f * 2; c < f * 2 + 2; c++)
            {
                float apps = desktop[c] - rest[c];
                other[c] = (short)Math.Clamp((int)MathF.Round((desktop[c] - s * apps) * k), short.MinValue, short.MaxValue);
            }
        }
        return other;
    }

    private static int Peak(short[] pcm)
    {
        int max = 0;
        foreach (short s in pcm)
        {
            int v = s < 0 ? -s : s;
            if (v > max) max = v;
        }
        return max;
    }

    private static double Energy(short[] pcm)
    {
        double e = 0;
        foreach (short s in pcm) e += (double)s * s;
        return e;
    }

    private static void Scale(short[] pcm, float gain)
    {
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)MathF.Round(pcm[i] * gain), short.MinValue, short.MaxValue);
    }

    private static void Add(short[] into, short[] from)
    {
        for (int i = 0; i < into.Length; i++) into[i] = (short)Math.Clamp(into[i] + from[i], short.MinValue, short.MaxValue);
    }

    /// <summary>Two different programs can share a name; number them so each slider says which is which.</summary>
    private static void Disambiguate(List<ClipTrack> tracks)
    {
        foreach (var dup in tracks.Select((t, i) => (t, i)).GroupBy(x => x.t.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            int n = 1;
            foreach (var (t, i) in dup) tracks[i] = t with { Title = $"{t.Title} {n++}" };
        }
    }
}
