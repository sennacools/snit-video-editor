namespace Snit.Core;

/// <summary>The file types in the Format list. Only MP4 (H.264) works so far; the rest come in phase 6.</summary>
public enum ExportFormat { Mp4H264, Mp4H265, WebmVp9, MovProRes, Gif, Mp3 }

/// <summary>Everything chosen in the export dialog.</summary>
public sealed class ExportSettings
{
    public required string OutputPath { get; init; }
    public ExportFormat Format { get; init; } = ExportFormat.Mp4H264;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public double FrameRate { get; init; } = 30;
    public double VideoBitrateMbps { get; init; } = 16;
    public int AudioBitrateKbps { get; init; } = 320;

    /// <summary>Export only this part of the timeline (frames, end not included). Null = the whole timeline.</summary>
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }

    /// <summary>The FFmpeg video encoder, e.g. "libx264" (computer's processor) or "h264_nvenc" (NVIDIA graphics card).</summary>
    public string VideoEncoder { get; init; } = "libx264";
}
