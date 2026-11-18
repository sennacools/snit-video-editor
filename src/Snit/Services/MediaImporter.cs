using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Reads information about a media file with ffprobe (length, size, frame rate)
/// and makes a thumbnail with ffmpeg.
/// </summary>
public static class MediaImporter
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff" };

    public const string DialogFilter =
        "Media files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.mts;*.m2ts;*.mpg;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;" +
        "*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|All files|*.*";

    /// <summary>Default length of a picture when you drop it on the timeline.</summary>
    public const double DefaultImageSeconds = 5;

    public static async Task<MediaItem> ProbeAsync(string path)
    {
        string ffprobe = FFmpegLocator.Current.FfprobePath
                         ?? throw new InvalidOperationException("FFmpeg isn't installed.");

        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path]);
        if (result.ExitCode != 0)
            throw new InvalidDataException(FirstLine(result.Errors) ?? "This file isn't a video, audio file or picture Snit can read.");

        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];

        // Album art inside an MP3 shows up as a "video" stream; ignore it.
        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPicture(x));
        bool hasVideo = video.ValueKind != JsonValueKind.Undefined;
        bool hasAudio = streams.Any(x => Str(x, "codec_type") == "audio");

        bool isImage = ImageExtensions.Contains(Path.GetExtension(path));
        MediaKind kind = isImage ? MediaKind.Image
            : hasVideo ? MediaKind.Video
            : hasAudio ? MediaKind.Audio
            : throw new InvalidDataException("No video or sound found in this file.");

        if (isImage && !hasVideo)
            throw new InvalidDataException("This picture can't be read.");

        double duration = 0;
        if (kind != MediaKind.Image)
        {
            duration = Dbl(root.TryGetProperty("format", out var format) ? Str(format, "duration") : null)
                       ?? Dbl(hasVideo ? Str(video, "duration") : null)
                       ?? 0;
            if (duration <= 0)
                throw new InvalidDataException("Snit couldn't work out how long this file is.");
        }

        return new MediaItem
        {
            FilePath = Path.GetFullPath(path),
            Kind = kind,
            DurationSeconds = duration,
            Width = hasVideo ? Int(video, "width") : 0,
            Height = hasVideo ? Int(video, "height") : 0,
            FrameRate = hasVideo ? (Fraction(Str(video, "avg_frame_rate")) ?? Fraction(Str(video, "r_frame_rate")) ?? 0) : 0,
            HasAudio = kind != MediaKind.Image && hasAudio,
        };
    }

    /// <summary>Makes a small picture of the file (a frame from about 10% in). Audio files get none.</summary>
    public static async Task<ImageSource?> MakeThumbnailAsync(MediaItem item)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null || item.Kind == MediaKind.Audio || item.IsOffline) return null;

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (item.Kind == MediaKind.Video)
            args.AddRange(["-ss", ProcessRunner.Num(Math.Min(1.0, item.DurationSeconds * 0.1))]);
        args.AddRange(["-i", item.FilePath, "-frames:v", "1", "-vf", "scale=320:-2",
                       "-f", "image2pipe", "-c:v", "png", "pipe:1"]);

        var result = await ProcessRunner.RunAsync(ffmpeg, args);
        if (result.ExitCode != 0 || result.Output.Length == 0) return null;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(result.Output);
        bitmap.EndInit();
        bitmap.Freeze(); // lets other threads (and the timeline drawing) use it safely
        return bitmap;
    }

    /// <summary>
    /// Reads the whole sound once (as mono, 4000 samples a second, which is plenty for a picture)
    /// and keeps the loudest sample of every 1/100 second. Returns null for files without sound.
    /// </summary>
    public static async Task<float[]?> MakeWaveformAsync(MediaItem item)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null || !item.HasAudio || item.IsOffline) return null;

        const int sampleRate = 4000;
        var result = await ProcessRunner.RunAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", item.FilePath,
            "-vn", "-ac", "1", "-ar", sampleRate.ToString(), "-f", "s16le", "pipe:1",
        ]);
        if (result.ExitCode != 0 || result.Output.Length < 2) return null;

        return await Task.Run(() =>
        {
            int samplesPerPoint = sampleRate / MediaItem.WaveformPointsPerSecond;
            int sampleCount = result.Output.Length / 2;
            var points = new float[(sampleCount + samplesPerPoint - 1) / samplesPerPoint];

            for (int i = 0; i < sampleCount; i++)
            {
                // Each sample is a 16-bit number from -32768 to 32767.
                short sample = BitConverter.ToInt16(result.Output, i * 2);
                float loudness = Math.Abs(sample / 32768f);
                int point = i / samplesPerPoint;
                if (loudness > points[point]) points[point] = loudness;
            }
            return points;
        });
    }

    // ---- Small JSON helpers ----

    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ToString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

    private static double? Dbl(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    /// <summary>"30000/1001" → 29.97</summary>
    private static double? Fraction(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split('/');
        if (parts.Length == 2 && Dbl(parts[0]) is double n && Dbl(parts[1]) is double d && d > 0 && n > 0)
            return n / d;
        return null;
    }

    private static string? FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
