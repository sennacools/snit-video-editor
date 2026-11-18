namespace Snit.Core;

/// <summary>
/// A stretch of the timeline where the same picture source is on top.
/// SourceStart is the source frame shown at <see cref="Start"/>.
/// </summary>
public sealed record VideoSegment(long Start, long End, string Path, MediaKind Kind, long SourceStart)
{
    public long SourceFrameAt(long timelineFrame) => SourceStart + (timelineFrame - Start);
}

/// <summary>One audio clip that should be heard during playback, with its volume and fades.</summary>
public sealed record AudioPiece(long Start, long End, string Path, long SourceStart,
    double VolumeDb = 0, double FadeInSeconds = 0, double FadeOutSeconds = 0);

/// <summary>
/// Turns the project into simple, read-only lists for the preview player.
/// They are copies, so the player (which runs on its own thread) never reads
/// the project while you are editing it.
/// </summary>
public static class PreviewPlan
{
    /// <summary>
    /// For every moment, which clip is visible? The highest visible video track wins
    /// (V2 covers V1). Gaps have no segment, and show black.
    /// </summary>
    public static IReadOnlyList<VideoSegment> BuildVideo(Project project)
    {
        // Tracks are listed top to bottom, so the first one that has a clip wins.
        var tracks = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsOff).ToList();
        // Titles are see-through and drawn on top separately, so they don't count here.
        var clips = tracks.SelectMany(t => t.Clips).Where(c => !c.Media.IsOffline && !c.IsTitle).ToList();

        var edges = clips.SelectMany(c => new[] { c.Start, c.End }).Distinct().Order().ToList();
        var segments = new List<VideoSegment>();

        for (int i = 0; i + 1 < edges.Count; i++)
        {
            long from = edges[i], to = edges[i + 1];
            Clip? top = tracks
                .Select(t => t.Clips.FirstOrDefault(c => c.Start <= from && c.End > from && !c.Media.IsOffline && !c.IsTitle))
                .FirstOrDefault(c => c is not null);
            if (top is null) continue;

            var segment = new VideoSegment(from, to, top.Media.FilePath, top.Media.Kind, top.In + (from - top.Start));

            // If this simply continues the previous piece of the same file, merge them,
            // so the player can keep reading the file without restarting.
            if (segments.Count > 0)
            {
                var last = segments[^1];
                if (last.Path == segment.Path && last.End == segment.Start &&
                    last.SourceFrameAt(segment.Start) == segment.SourceStart)
                {
                    segments[^1] = last with { End = segment.End };
                    continue;
                }
            }
            segments.Add(segment);
        }
        return segments;
    }

    /// <summary>The titles visible at a frame, bottom track first (so later ones are drawn on top).</summary>
    public static IReadOnlyList<Clip> TitlesAt(Project project, long frame)
        => project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsOff).Reverse()
            .SelectMany(t => t.Clips.Where(c => c.IsTitle && c.Start <= frame && frame < c.End))
            .ToList();

    /// <summary>All clips on audio tracks that are not muted.</summary>
    public static IReadOnlyList<AudioPiece> BuildAudio(Project project)
        => project.Tracks
            .Where(t => t.Kind == TrackKind.Audio && !t.IsOff)
            .SelectMany(t => t.Clips)
            .Where(c => !c.Media.IsOffline)
            .Select(c => new AudioPiece(c.Start, c.End, c.Media.FilePath, c.In, c.VolumeDb, c.FadeInSeconds, c.FadeOutSeconds))
            .ToList();
}
