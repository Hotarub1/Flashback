namespace Flashback.Core;

/// <summary>
/// Turns the per-app captures into a clip's app tracks, lined up and levelled against the desktop capture.
///
/// The desktop capture is the reference: it is what the clip's mix is made from and it is in sync with the video.
/// Per-app capture runs through a different path in Windows: measured on the user's PC it arrived 20-40 ms later
/// and about 4.5 dB louder than the same sound in the desktop capture (output effects only touch the desktop path),
/// and the delay differs by a few milliseconds from app to app. So both are measured on every clip: the delays by
/// cross-correlation (all apps together, then each app near that), the level by a least-squares fit. If the app tracks do not account for the desktop sound (an app that could not be captured,
/// for example), they are not used and the clip keeps one desktop track, so the player never loses a sound.
/// </summary>
public static class AppTracks
{
    private const int Rate = AudioRing.SampleRate;
    private const int MaxTracks = 8;
    /// <summary>Apps whose loudest sample stays below this (about -54 dBFS) played nothing worth a track.</summary>
    private const int SilentPeak = 64;
    private const int Decimation = 24;                 // 2 kHz for the coarse delay search
    private const double MaxLagSeconds = 0.15;
    /// <summary>How far (10 ms) an app's own delay may be from the delay of all apps together.</summary>
    private const int RefineRadius = Rate / 100;
    /// <summary>How well the app tracks, summed, must match the desktop sound overall (squared correlation).</summary>
    private const double MinMatch = 0.7;

    public static string OtherAppsTitle => "Other apps";

    /// <summary>
    /// Builds one track per app that played sound in the clip, or returns null (with the reason in
    /// <paramref name="note"/>) when the clip should keep a single desktop track instead.
    /// </summary>
    /// <param name="parts">The clip's uninterrupted stretches of video, in wall-clock time.</param>
    /// <param name="desktop">The desktop capture for the same stretches, as rendered for the clip.</param>
    public static List<ClipTrack>? Build(List<AppAudioRing> apps, List<(double Start, double End)> parts, long totalFrames,
        short[] desktop, out string note)
    {
        // One buffer per app (all processes of an app together), dropping apps that stayed silent.
        // Flashback's own sound takes part in lining up and checking, but never becomes a track.
        var groups = new List<(string Name, List<AudioRing> Rings, short[] Pcm, bool Own)>();
        foreach (var g in apps.GroupBy(a => a.Key))
        {
            var rings = g.Select(a => a.Ring).ToList();
            short[] pcm = new short[totalFrames * 2];
            Render(rings, parts, pcm, 0);
            if (Peak(pcm) > SilentPeak) groups.Add((g.Last().Name, rings, pcm, g.First().Own));
        }
        var shown = groups.Where(g => !g.Own).ToList();
        if (shown.Count < 2)
        {
            note = shown.Count == 0 ? "no app played sound" : $"only {shown[0].Name} played sound";
            return null;
        }

        int[] dm = MonoSum([desktop], totalFrames);
        int[] apMono = MonoSum(groups.Select(g => g.Pcm), totalFrames);
        if (!FindLag(dm, apMono, out int lag))
        {
            note = "the app sound could not be lined up with the desktop sound";
            return null;
        }

        // Each app's own delay differs by a few milliseconds (measured 29 to 36 ms in one clip), which is enough to
        // scramble speech and music against the desktop sound, so every app is lined up on its own near the common
        // delay, then taken again shifted by its delay.
        var delays = new int[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            delays[i] = RefineLag(dm, MonoSum([groups[i].Pcm], totalFrames), lag, RefineRadius);
            Array.Clear(groups[i].Pcm);
            Render(groups[i].Rings, parts, groups[i].Pcm, delays[i] / (double)Rate);
        }
        apMono = MonoSum(groups.Select(g => g.Pcm), totalFrames);

        (double gain, double match, int uncovered, int audible) = Fit(dm, apMono);
        string measured = $"delays {string.Join(" / ", delays.Select(d => (d * 1000.0 / Rate).ToString("F1")))} ms, level {gain:F2}, match {match * 100:F0}%";
        if (audible == 0)
        {
            note = "the desktop sound is too quiet to line the apps up with";
            return null;
        }
        if (match < MinMatch || (uncovered >= 10 && uncovered > audible / 20))
        {
            note = $"the app tracks do not cover the desktop sound ({measured}, {uncovered / 10.0:F1} s not covered)";
            return null;
        }

        // Match the desktop level, so the tracks at 100% add up to the clip's mix.
        foreach (var g in shown) Scale(g.Pcm, (float)gain);

        var ordered = shown.OrderByDescending(g => Energy(g.Pcm)).ToList();
        var tracks = ordered.Take(MaxTracks - 1).Select(g => new ClipTrack(g.Name, g.Pcm)).ToList();
        if (ordered.Count == MaxTracks) tracks.Add(new ClipTrack(ordered[^1].Name, ordered[^1].Pcm));
        else if (ordered.Count > MaxTracks)
        {
            short[] rest = ordered[MaxTracks - 1].Pcm;
            foreach (var g in ordered.Skip(MaxTracks)) Add(rest, g.Pcm);
            tracks.Add(new ClipTrack(OtherAppsTitle, rest));
        }
        Disambiguate(tracks);
        note = $"{string.Join(", ", groups.Select(g => g.Name))} ({measured})";
        return tracks;
    }

    private static void Render(List<AudioRing> rings, List<(double Start, double End)> parts, short[] dest, double shift)
    {
        long offset = 0;
        foreach (var (start, end) in parts)
        {
            foreach (AudioRing ring in rings) ring.Render(start + shift, end + shift, dest, offset, 1f);
            offset += (long)Math.Round((end - start) * Rate);
        }
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

    /// <summary>
    /// The delay (in frames) of the app sound behind the desktop sound: apps[i + lag] lines up with desktop[i].
    /// A coarse search at 2 kHz, then a fine one at full rate around the best coarse match.
    /// </summary>
    private static bool FindLag(int[] desktop, int[] apps, out int lag)
    {
        lag = 0;
        long frames = apps.Length;
        int maxLag = (int)(MaxLagSeconds * Rate);
        if (frames < maxLag * 4) return false;

        int n = (int)(frames / Decimation);
        var d = new double[n];
        var a = new double[n];
        for (int j = 0; j < n; j++)
        {
            double sd = 0, sa = 0;
            for (int k = 0; k < Decimation; k++)
            {
                long i = (long)j * Decimation + k;
                sd += desktop[i];
                sa += apps[i];
            }
            d[j] = sd;
            a[j] = sa;
        }
        int coarseMax = maxLag / Decimation;
        (int best, double bestScore) = BestLag(-coarseMax, coarseMax, l =>
        {
            double c = 0;
            int from = Math.Max(0, -l), to = Math.Min(n, n - l);
            for (int j = from; j < to; j++) c += d[j] * a[j + l];
            return c;
        });
        if (bestScore <= 0) return false;

        lag = RefineLag(desktop, apps, best * Decimation, Decimation * 2);
        return Math.Abs(lag) <= maxLag;
    }

    /// <summary>The best delay within <paramref name="radius"/> frames of <paramref name="centre"/>: every 8th frame first, then each frame around the best.</summary>
    private static int RefineLag(int[] desktop, int[] app, int centre, int radius)
    {
        long frames = app.Length;
        double Score(int l, int stride)
        {
            double c = 0;
            long from = Math.Max(0, -l), to = Math.Min(frames, frames - l);
            for (long i = from; i < to; i += stride) c += (double)desktop[i] * app[i + l];
            return c;
        }
        const int step = 8;
        (int coarse, _) = BestLag(0, 2 * radius / step, k => Score(centre - radius + k * step, 6));
        int around = centre - radius + coarse * step;
        (int fine, _) = BestLag(around - step, around + step, l => Score(l, 3));
        return fine;
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
    /// The level that makes the summed apps match the desktop sound, how well they match (squared correlation), and how
    /// many tenth-of-a-second blocks have audible desktop sound (<paramref name="audible"/>) that the apps do not
    /// explain (<paramref name="uncovered"/>).
    /// </summary>
    private static (double Gain, double Match, int Uncovered, int Audible) Fit(int[] desktop, int[] apps)
    {
        double da = 0, aa = 0, dd = 0;
        for (long i = 0; i < apps.Length; i++)
        {
            double d = desktop[i];
            da += d * apps[i];
            aa += (double)apps[i] * apps[i];
            dd += d * d;
        }
        if (aa <= 0 || dd <= 0 || da <= 0) return (0, 0, 0, 0);
        double gain = Math.Clamp(da / aa, 0.05, 8);
        double match = da * da / (aa * dd);

        const int block = Rate / 10;
        // Audible: a block whose desktop sound is above about -50 dBFS (the mono sum doubles the scale).
        const double audibleEnergy = 200.0 * 200.0 * block;
        int uncovered = 0, audibleBlocks = 0;
        for (long b = 0; b + block <= apps.Length; b += block)
        {
            double e = 0, r = 0;
            for (long i = b; i < b + block; i++)
            {
                double d = desktop[i];
                double diff = d - gain * apps[i];
                e += d * d;
                r += diff * diff;
            }
            if (e < audibleEnergy) continue;
            audibleBlocks++;
            if (r > 0.5 * e) uncovered++;
        }
        return (gain, match, uncovered, audibleBlocks);
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
