using System.Diagnostics;
using System.IO;

namespace Snit.Services;

/// <summary>The result of looking for FFmpeg on this computer.</summary>
public sealed record FFmpegStatus(bool IsAvailable, string? FfmpegPath, string? FfprobePath, string? Version);

/// <summary>
/// Finds ffmpeg.exe and ffprobe.exe. Snit does not ship FFmpeg itself;
/// it expects it to be installed (for example with "winget install Gyan.FFmpeg").
/// </summary>
public static class FFmpegLocator
{
    public const string InstallCommand = "winget install Gyan.FFmpeg";

    /// <summary>The last result of <see cref="CheckAsync"/>. Other parts of Snit use these paths.</summary>
    public static FFmpegStatus Current { get; private set; } = new(false, null, null, null);

    public static Task<FFmpegStatus> CheckAsync() => Task.Run(() =>
    {
        string? ffmpeg = FindOnPath("ffmpeg.exe");
        string? ffprobe = FindOnPath("ffprobe.exe");

        string? version = ffmpeg is null ? null : ReadVersion(ffmpeg);

        // Only count it as available if ffmpeg actually ran.
        bool ok = ffmpeg is not null && ffprobe is not null && version is not null;
        Current = new FFmpegStatus(ok, ffmpeg, ffprobe, version);
        return Current;
    });

    /// <summary>
    /// Looks through every folder on PATH. We read PATH fresh from Windows' settings
    /// (not only from when Snit started), so "Check again" works right after
    /// installing FFmpeg without restarting Snit.
    /// </summary>
    private static string? FindOnPath(string exeName)
    {
        var pathValues = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };

        var folders = pathValues
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A broken entry in PATH (odd characters); skip it.
            }
        }
        return null;
    }

    /// <summary>Runs "ffmpeg -version" and returns e.g. "ffmpeg version 7.1-full_build-www.gyan.dev".</summary>
    private static string? ReadVersion(string ffmpegPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath, "-hide_banner -version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;

            string firstLine = process.StandardOutput.ReadLine() ?? "";
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }

            int copyright = firstLine.IndexOf(" Copyright", StringComparison.Ordinal);
            return copyright > 0 ? firstLine[..copyright] : firstLine;
        }
        catch (Exception)
        {
            // File exists but would not start (blocked, wrong architecture, ...).
            return null;
        }
    }
}
