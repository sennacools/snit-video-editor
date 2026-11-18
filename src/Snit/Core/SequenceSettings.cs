namespace Snit.Core;

/// <summary>A common video size, for the sequence settings dropdown.</summary>
public sealed record FramePreset(string Name, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>
/// Changing a sequence's size and frame rate. Clips keep their place in time: at a new frame rate
/// every frame number (positions, lengths, keyframes, transitions, markers, in/out) is converted,
/// and at a new size the position settings move along so pictures stay where they were.
/// </summary>
public static class SequenceSettings
{
    public static readonly IReadOnlyList<FramePreset> Presets =
    [
        new("HD 1080p (1920 × 1080)", 1920, 1080),
        new("4K UHD (3840 × 2160)", 3840, 2160),
        new("HD 720p (1280 × 720)", 1280, 720),
        new("Vertical (1080 × 1920)", 1080, 1920),
        new("Square (1080 × 1080)", 1080, 1080),
        new("Portrait 4:5 (1080 × 1350)", 1080, 1350),
    ];

    public static readonly IReadOnlyList<double> FrameRates = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60];

    /// <summary>A size FFmpeg's H.264 encoder accepts: even, between 16 and 8192.</summary>
    public static int CleanSize(int pixels) => Math.Clamp(pixels / 2 * 2, 16, 8192);

    public static void Apply(Project project, int width, int height, double fps)
    {
        width = CleanSize(width);
        height = CleanSize(height);
        fps = Math.Clamp(fps, 1, 240);

        double k = fps / project.Settings.FrameRate;
        double sx = (double)width / project.Settings.Width, sy = (double)height / project.Settings.Height;
        long F(long frame) => (long)Math.Round(frame * k, MidpointRounding.AwayFromZero);

        if (Math.Abs(k - 1) > 1e-9 || sx != 1 || sy != 1)
        {
            foreach (Clip clip in project.AllClips)
            {
                long start = F(clip.Start), end = F(clip.End);
                clip.Start = start;
                clip.Length = Math.Max(1, end - start);
                clip.In = F(clip.In);
                if (clip.TransitionIn is { } tin) clip.TransitionIn = tin with { Frames = Math.Max(1, F(tin.Frames)) };
                if (clip.TransitionOut is { } tout) clip.TransitionOut = tout with { Frames = Math.Max(1, F(tout.Frames)) };
                clip.Keyframes = clip.Keyframes.ToDictionary(
                    p => p.Key,
                    p => (IReadOnlyList<Keyframe>)p.Value
                        .Select(key => key with
                        {
                            Frame = F(key.Frame),
                            Value = p.Key == "PositionX" ? key.Value * sx : p.Key == "PositionY" ? key.Value * sy : key.Value,
                        })
                        .GroupBy(key => key.Frame).Select(g => g.Last()).ToList());
                clip.Look = clip.Look with { PositionX = clip.Look.PositionX * sx, PositionY = clip.Look.PositionY * sy };
            }
            project.Markers = project.Markers.Select(m => m with { Frame = F(m.Frame) }).ToList();
            if (project.InPoint is long i) project.InPoint = F(i);
            if (project.OutPoint is long o) project.OutPoint = F(o);
        }

        project.Settings.Width = width;
        project.Settings.Height = height;
        project.Settings.FrameRate = fps;
    }
}
