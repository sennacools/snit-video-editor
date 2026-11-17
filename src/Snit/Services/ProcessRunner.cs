using System.Diagnostics;
using System.IO;

namespace Snit.Services;

/// <summary>Runs a command-line program (ffmpeg / ffprobe) and collects what it prints.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, byte[] Output, string Errors);

    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, CancellationToken cancel = default)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList takes care of quotes and spaces in file names for us.
        foreach (string a in args) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {exe}.");
        using var output = new MemoryStream();

        // Read both streams at the same time; otherwise a full buffer can make the program hang.
        Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output, cancel);
        Task<string> readErrors = process.StandardError.ReadToEndAsync(cancel);

        try
        {
            await Task.WhenAll(copyOutput, readErrors, process.WaitForExitAsync(cancel));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        return new Result(process.ExitCode, output.ToArray(), readErrors.Result);
    }

    /// <summary>Starts a long-running program whose output we read as a stream (for playback).</summary>
    public static Process StartStreaming(string exe, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) startInfo.ArgumentList.Add(a);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {exe}.");
    }

    /// <summary>Formats a number the way FFmpeg expects (always a dot, never a comma).</summary>
    public static string Num(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
