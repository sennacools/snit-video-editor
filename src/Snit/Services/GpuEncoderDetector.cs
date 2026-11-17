namespace Snit.Services;

/// <summary>
/// Finds out whether this computer can encode H.264 on its graphics card
/// (NVIDIA, Intel or AMD). That is usually several times faster than the processor.
/// We test each one with a tiny encode, because FFmpeg lists encoders even when
/// the matching graphics card isn't there.
/// </summary>
public static class GpuEncoderDetector
{
    private static readonly (string Encoder, string Name)[] Candidates =
    [
        ("h264_nvenc", "NVIDIA"),
        ("h264_qsv", "Intel Quick Sync"),
        ("h264_amf", "AMD"),
    ];

    private static Task<(string Encoder, string Name)?>? detection;

    /// <summary>The first graphics-card encoder that works, or null. Only checked once per run.</summary>
    public static Task<(string Encoder, string Name)?> DetectAsync() => detection ??= DetectCoreAsync();

    private static async Task<(string Encoder, string Name)?> DetectCoreAsync()
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null) return null;

        foreach (var candidate in Candidates)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await ProcessRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30:d=0.2",
                    "-c:v", candidate.Encoder, "-f", "null", "-",
                ], timeout.Token);
                if (result.ExitCode == 0) return candidate;
            }
            catch (Exception)
            {
                // Not available on this computer; try the next one.
            }
        }
        return null;
    }
}
