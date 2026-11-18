namespace Snit.Core;

/// <summary>
/// One clip on one video track, as the preview player needs it: where it is, which part of
/// which file it shows (with speed and reverse), and how it looks.
/// </summary>
public sealed record VideoSegment(
    long Start, long End, string Path, MediaKind Kind,
    long In, long SourceLength, double Speed, bool Reverse,
    ClipLook Look, int MediaWidth, int MediaHeight, Guid ClipId, TitleStyle? Title,
    long FadeInFrames = 0, long FadeOutFrames = 0, long BlackInFrames = 0, long BlackOutFrames = 0, int Slot = 0,
    IReadOnlyDictionary<string, IReadOnlyList<Keyframe>>? Keyframes = null, IReadOnlyList<ClipEffect>? Effects = null)
{
    /// <summary>The look at a timeline frame, with animation applied.</summary>
    public ClipLook LookAt(long frame) => Keyframes is null ? Look : Core.Keyframes.LookAt(Look, Keyframes, frame - Start);

    /// <summary>How visible (0–1) at a frame, for transitions (same ramps as <see cref="RenderClip"/>).</summary>
    public double VisibilityAt(long frame) => Ramp(frame, FadeInFrames, FadeOutFrames);

    /// <summary>How bright (0 = black, 1 = normal) at a frame, for dip to black.</summary>
    public double BrightnessAt(long frame) => Ramp(frame, BlackInFrames, BlackOutFrames);

    private double Ramp(long frame, long rise, long fall)
    {
        double value = 1;
        if (rise > 0) value = Math.Min(value, Math.Clamp((frame - Start) / (double)rise, 0, 1));
        if (fall > 0) value = Math.Min(value, Math.Clamp((End - frame) / (double)fall, 0, 1));
        return value;
    }

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
    public bool CoversFrame(int frameWidth, int frameHeight) => CoversFrame(frameWidth, frameHeight, Look);

    public bool CoversFrame(int frameWidth, int frameHeight, ClipLook Look)
    {
        if (Kind != MediaKind.Video || MediaWidth <= 0 || MediaHeight <= 0) return false;
        // Crop and green screen make parts see-through: what is below may show.
        if (Effects?.Any(e => e.Enabled && EffectCatalog.Find(e.Kind)?.MakesTransparency == true) == true) return false;
        if (Look.Scale < 100 || Look.Rotation != 0 || Look.Opacity < 100 || Look.PositionX != 0 || Look.PositionY != 0) return false;
        double shape = (double)MediaWidth / MediaHeight, frameShape = (double)frameWidth / frameHeight;
        return Math.Abs(shape - frameShape) / frameShape < 0.01 || Look.Scale > 100 * Math.Max(shape / frameShape, frameShape / shape);
    }
}

/// <summary>One audio clip that should be heard during playback, with its volume, fades and speed.</summary>
public sealed record AudioPiece(long Start, long End, string Path, long SourceStart,
    double VolumeDb = 0, double FadeInSeconds = 0, double FadeOutSeconds = 0,
    double Speed = 1, bool Reverse = false, long SourceLength = 0,
    ClipAudio? Audio = null, IReadOnlyList<Keyframe>? VolumeKeys = null, double TrackDb = 0, double Pan = 0)
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
        => project.Tracks.Where(t => t.Kind != TrackKind.Audio).Reverse()
            .Select(t => (IReadOnlyList<VideoSegment>)(t.IsOff ? [] : TransitionPlan.Expand(t, project.Fps)
                .Where(r => !r.Clip.Media.IsOffline)
                .Select(r => new VideoSegment(
                    r.Clip.Start, r.Clip.End, r.Clip.Media.FilePath, r.Clip.Media.Kind,
                    r.Clip.In, r.Clip.SourceLength, r.Clip.Speed, r.Clip.Reverse,
                    r.Clip.Look, r.Clip.Media.Width, r.Clip.Media.Height, r.Original.Id, r.Clip.Title?.Copy(),
                    r.FadeInFrames, r.FadeOutFrames, r.BlackInFrames, r.BlackOutFrames, r.Slot, r.Clip.Keyframes, r.Clip.Effects))
                .ToList()))
            .ToList();

    /// <summary>All clips on audio tracks that are not muted.</summary>
    public static IReadOnlyList<AudioPiece> BuildAudio(Project project)
        => project.Tracks
            .Where(project.IsAudible)
            .SelectMany(t => TransitionPlan.Expand(t, project.Fps)
                .Where(r => !r.Clip.Media.IsOffline)
                .Select(r => Piece(project, t, r)))
            .ToList();

    private static AudioPiece Piece(Project project, Track track, RenderClip r)
        => new AudioPiece(r.Clip.Start, r.Clip.End, r.Clip.Media.FilePath, r.Clip.In, r.Clip.VolumeDb,
                // Transitions fade the sound as well (same rule as the export).
                Math.Max(r.Clip.FadeInSeconds, (r.FadeInFrames + r.BlackInFrames) / project.Fps),
                Math.Max(r.Clip.FadeOutSeconds, (r.FadeOutFrames + r.BlackOutFrames) / project.Fps),
                r.Clip.Speed, r.Clip.Reverse, r.Clip.SourceLength,
                r.Clip.Audio, Keyframes.Of(r.Clip.Keyframes, Keyframes.Volume), track.VolumeDb, track.Pan);
}
