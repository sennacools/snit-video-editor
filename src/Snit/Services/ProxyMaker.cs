using System.IO;
using System.Security.Cryptography;
using System.Text;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Proxies: small, easy-to-decode copies of big video files (like 4K), used only for the
/// preview so it plays smoothly. The export always uses the original files.
///
/// A proxy is 540 pixels high H.264 with a keyframe every 10 frames (so jumping around is
/// quick). Proxies live in %LOCALAPPDATA%\Snit\Proxies, named after the original file's path,
/// size and date, so a changed file gets a new proxy and projects find their proxies again.
/// </summary>
public static class ProxyMaker
{
    public const int ProxyHeight = 540;

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snit", "Proxies");

    /// <summary>Only video files get proxies.</summary>
    public static bool CanHaveProxy(MediaItem item) => item.Kind == MediaKind.Video && !item.IsOffline;

    /// <summary>Where the proxy of a file is (or would be).</summary>
    public static string PathFor(string file)
    {
        var info = new FileInfo(file);
        string key = $"{Path.GetFullPath(file).ToLowerInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(file)}-{hash}.mp4");
    }

    /// <summary>The proxy that was made earlier for this file, or null.</summary>
    public static string? Existing(MediaItem item)
        => CanHaveProxy(item) && File.Exists(PathFor(item.FilePath)) ? PathFor(item.FilePath) : null;

    /// <summary>Makes the proxy (takes a while for long files). Returns its path.</summary>
    public static async Task<string> CreateAsync(MediaItem item, CancellationToken cancel = default)
    {
        string ffmpeg = FFmpegLocator.Current.FfmpegPath ?? throw new InvalidOperationException("FFmpeg was not found.");
        string target = PathFor(item.FilePath);
        Directory.CreateDirectory(Folder);
        string temp = target + ".part.mp4";

        int height = Math.Max(2, Math.Min(ProxyHeight, item.Height > 0 ? item.Height : ProxyHeight) / 2 * 2);
        var result = await ProcessRunner.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", item.FilePath, "-map", "0:v:0",
            "-vf", $"scale=-2:{height}",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-g", "10", "-bf", "0", "-pix_fmt", "yuv420p",
            "-an", "-movflags", "+faststart", temp,
        ], cancel);

        if (result.ExitCode != 0 || !File.Exists(temp))
        {
            TryDelete(temp);
            throw new IOException("FFmpeg could not make the proxy: " + result.Errors.Trim());
        }
        File.Move(temp, target, overwrite: true);
        return target;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
