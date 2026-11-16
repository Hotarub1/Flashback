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
    public static (int ExitCode, string Stderr) Run(string exe, IEnumerable<string> args, int timeoutMs, JobObject? job,
        Action<Stream>? feedStdin = null, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = feedStdin != null,
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
        if (lowPriority)
        {
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        proc.BeginErrorReadLine();

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

    public static string LastLine(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
