using System.Diagnostics;
using System.IO;

namespace Snit.Services;

/// <summary>How far an export is.</summary>
public sealed record ExportProgress(long Frame, long TotalFrames, TimeSpan Elapsed)
{
    public double Fraction => TotalFrames > 0 ? Math.Clamp((double)Frame / TotalFrames, 0, 1) : 0;

    /// <summary>A guess of the time left, based on how fast it went so far. Null right at the start.</summary>
    public TimeSpan? Remaining => Frame > 0 && Elapsed.TotalSeconds > 1
        ? TimeSpan.FromSeconds(Elapsed.TotalSeconds / Frame * Math.Max(0, TotalFrames - Frame))
        : null;
}

/// <summary>Thrown when ffmpeg stops with an error. The message holds ffmpeg's last lines.</summary>
public sealed class ExportFailedException(string message) : Exception(message);

/// <summary>
/// Runs an export: starts ffmpeg with the plan's arguments, reads its progress
/// ("frame=123" lines) for the progress bar, and stops it when you cancel.
/// </summary>
public sealed class Exporter
{
    public async Task RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress, CancellationToken cancel)
    {
        string ffmpeg = FFmpegLocator.Current.FfmpegPath ?? throw new InvalidOperationException("FFmpeg isn't installed.");
        string output = plan.Arguments[^1];

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in plan.Arguments) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var clock = Stopwatch.StartNew();

        // Keep ffmpeg's last messages, to explain what went wrong if it fails.
        var errorLines = new Queue<string>();
        Task readErrors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                lock (errorLines)
                {
                    errorLines.Enqueue(line);
                    if (errorLines.Count > 15) errorLines.Dequeue();
                }
        });

        // "-progress pipe:1" makes ffmpeg print lines like "frame=120" a few times a second.
        Task readProgress = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                if (line.StartsWith("frame=") && long.TryParse(line.AsSpan(6), out long frame))
                    progress?.Report(new ExportProgress(Math.Min(frame, plan.TotalFrames), plan.TotalFrames, clock.Elapsed));
            }
        });

        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already stopped */ }
            await process.WaitForExitAsync(CancellationToken.None);
            TryDelete(output); // don't leave a broken half-written video behind
            throw;
        }

        await Task.WhenAll(readErrors, readProgress);

        if (process.ExitCode != 0)
        {
            TryDelete(output);
            string details;
            lock (errorLines) details = string.Join("\n", errorLines);
            throw new ExportFailedException(string.IsNullOrWhiteSpace(details) ? $"FFmpeg stopped with code {process.ExitCode}." : details);
        }

        progress?.Report(new ExportProgress(plan.TotalFrames, plan.TotalFrames, clock.Elapsed));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
