namespace Snit.Core;

/// <summary>
/// One clip on one video track, as the preview player needs it: where it is, which part of
/// which file it shows (with speed and reverse), and how it looks.
/// </summary>
public sealed record VideoSegment(
    long Start, long End, string Path, MediaKind Kind,
    long In, long SourceLength, double Speed, bool Reverse,
    ClipLook Look, int MediaWidth, int MediaHeight, Guid ClipId, TitleStyle? Title)
{
    /// <summary>Which source frame is shown at a timeline frame (same rule as <see cref="Clip.SourceFrameAt"/>).</summary>
    public long SourceFrameAt(long timelineFrame)
    {
        long step = (long)Math.Floor((timelineFrame - Start) * Speed + 1e-9);
        step = Math.Clamp(step, 0, SourceLength - 1);
        return Reverse ? In + SourceLength - 1 - step : In + step;
    }

    /// <summary>Same file and same colours = the same decoded pictures (a decoder can be reused).</summary>
    public string DecoderKey => $"{Path}|{Look.Exposure}|{Look.Contrast}|{Look.Saturation}|{Look.Temperature}";

    /// <summary>
    /// True when this clip hides everything below it: a normal video that fills the whole frame.
    /// Then the player doesn't need to decode the tracks underneath.
    /// </summary>
    public bool CoversFrame(int frameWidth, int frameHeight)
    {
        if (Kind != MediaKind.Video || MediaWidth <= 0 || MediaHeight <= 0) return false;
        if (Look.Scale < 100 || Look.Rotation != 0 || Look.Opacity < 100 || Look.PositionX != 0 || Look.PositionY != 0) return false;
        double shape = (double)MediaWidth / MediaHeight, frameShape = (double)frameWidth / frameHeight;
        return Math.Abs(shape - frameShape) / frameShape < 0.01 || Look.Scale > 100 * Math.Max(shape / frameShape, frameShape / shape);
    }
}

/// <summary>One audio clip that should be heard during playback, with its volume, fades and speed.</summary>
public sealed record AudioPiece(long Start, long End, string Path, long SourceStart,
    double VolumeDb = 0, double FadeInSeconds = 0, double FadeOutSeconds = 0,
    double Speed = 1, bool Reverse = false, long SourceLength = 0)
{
    public long SourceFrames => SourceLength > 0 ? SourceLength : End - Start;
}

/// <summary>
/// Turns the project into simple, read-only lists for the preview player.
/// They are copies, so the player (which runs on its own thread) never reads
/// the project while you are editing it.
/// </summary>
public static class PreviewPlan
{
    /// <summary>
    /// One list of clips per video track, bottom track (V1) first. A hidden track gives an
    /// empty list, so the numbering of the layers never changes.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<VideoSegment>> BuildLayers(Project project)
        => project.Tracks.Where(t => t.Kind == TrackKind.Video).Reverse()
            .Select(t => (IReadOnlyList<VideoSegment>)(t.IsOff ? [] : t.Clips
                .Where(c => !c.Media.IsOffline)
                .OrderBy(c => c.Start)
                .Select(c => new VideoSegment(
                    c.Start, c.End, c.Media.FilePath, c.Media.Kind,
                    c.In, c.SourceLength, c.Speed, c.Reverse,
                    c.Look, c.Media.Width, c.Media.Height, c.Id, c.Title?.Copy()))
                .ToList()))
            .ToList();

    /// <summary>All clips on audio tracks that are not muted.</summary>
    public static IReadOnlyList<AudioPiece> BuildAudio(Project project)
        => project.Tracks
            .Where(t => t.Kind == TrackKind.Audio && !t.IsOff)
            .SelectMany(t => t.Clips)
            .Where(c => !c.Media.IsOffline)
            .Select(c => new AudioPiece(c.Start, c.End, c.Media.FilePath, c.In, c.VolumeDb, c.FadeInSeconds, c.FadeOutSeconds,
                c.Speed, c.Reverse, c.SourceLength))
            .ToList();
}
