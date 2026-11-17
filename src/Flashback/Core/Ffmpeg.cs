using System.Diagnostics;
using System.IO;
using System.Text;

namespace Flashback.Core;

public static class Ffmpeg
{
    /// <summary>Looks next to the app first, then in a tools\ffmpeg folder above it, then on PATH.</summary>
    public static string? Locate()
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
        };
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "tools", "ffmpeg", "ffmpeg.exe"));
        foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(p.Trim(), "ffmpeg.exe")); } catch { }
        }
        foreach (string c in candidates)
        {
            try
            {
                if (File.Exists(c)) return Path.GetFullPath(c);
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Runs FFmpeg to completion. Always bounded: the process is killed once <paramref name="timeoutMs"/> passes.
    /// </summary>
    /// <param name="onStdoutLine">Receives each line FFmpeg writes to stdout (for example "-progress pipe:1"), on a worker thread.</param>
    public static (int ExitCode, string Stderr) Run(string exe, IEnumerable<string> args, int timeoutMs, JobObject? job,
        Action<Stream>? feedStdin = null, bool lowPriority = false, Action<string>? onStdoutLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = feedStdin != null,
            RedirectStandardOutput = onStdoutLine != null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var err = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err)
            {
                if (err.Length < 64000) err.AppendLine(e.Data);
            }
        };
        if (onStdoutLine != null)
        {
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                try { onStdoutLine(e.Data); }
                catch { }
            };
        }
        proc.Start();
        job?.Add(proc);
        if (lowPriority)
        {
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        proc.BeginErrorReadLine();
        if (onStdoutLine != null) proc.BeginOutputReadLine();

        bool timedOut = false;
        using var killer = new System.Threading.Timer(_ =>
        {
            timedOut = true;
            try { proc.Kill(true); } catch { }
        }, null, timeoutMs, Timeout.Infinite);

        if (feedStdin != null)
        {
            try
            {
                using Stream stdin = proc.StandardInput.BaseStream;
                feedStdin(stdin);
                stdin.Flush();
            }
            catch (IOException)
            {
                // FFmpeg closed its end early; its exit code and stderr say why.
            }
            catch (ObjectDisposedException) { }
        }

        proc.WaitForExit();
        string text;
        lock (err) text = err.ToString();
        return timedOut ? (-1, "Timed out. " + text) : (proc.ExitCode, text);
    }

    /// <summary>
    /// Runs FFmpeg and collects everything it writes to stdout (for example decoded audio sent to "pipe:1").
    /// Bounded in time by <paramref name="timeoutMs"/> and in size by <paramref name="maxBytes"/>: past either,
    /// the process is killed and the exit code is negative.
    /// </summary>
    public static (int ExitCode, byte[] Stdout, string Stderr) RunCapture(string exe, IEnumerable<string> args, int timeoutMs,
        JobObject? job, long maxBytes, long expectedBytes = 0)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var err = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err)
            {
                if (err.Length < 64000) err.AppendLine(e.Data);
            }
        };
        proc.Start();
        job?.Add(proc);
        proc.BeginErrorReadLine();

        bool timedOut = false, tooBig = false;
        using var killer = new System.Threading.Timer(_ =>
        {
            timedOut = true;
            try { proc.Kill(true); } catch { }
        }, null, timeoutMs, Timeout.Infinite);

        var output = new MemoryStream((int)Math.Clamp(expectedBytes, 1 << 16, Math.Min(maxBytes, int.MaxValue - 1024)));
        try
        {
            using Stream stdout = proc.StandardOutput.BaseStream;
            var buf = new byte[1 << 16];
            int n;
            while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
            {
                if (output.Length + n > maxBytes)
                {
                    tooBig = true;
                    try { proc.Kill(true); } catch { }
                    break;
                }
                output.Write(buf, 0, n);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        proc.WaitForExit();
        string text;
        lock (err) text = err.ToString();
        if (timedOut) return (-1, [], "Timed out. " + text);
        if (tooBig) return (-2, [], "Output larger than allowed. " + text);
        return (proc.ExitCode, output.ToArray(), text);
    }

    public static string LastLine(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
