namespace Snit.Core;

public enum TransitionKind { CrossDissolve, DipToBlack }

/// <summary>A transition at the start or end of a clip, <see cref="Frames"/> long.</summary>
public sealed record Transition(TransitionKind Kind, long Frames);

/// <summary>
/// A clip as it is really shown and heard once transitions are taken into account: maybe a bit
/// longer (a cross-dissolve uses footage just beyond the cut), with fades. <see cref="Clip"/> is
/// an adjusted copy; the original on the timeline is left alone.
/// </summary>
public sealed record RenderClip(
    Clip Clip, Clip Original,
    long FadeInFrames, long FadeOutFrames,   // from see-through (or silent) to visible, and back
    long BlackInFrames, long BlackOutFrames, // from black to visible, and back (dip to black)
    int Slot)                                // 0 or 1, alternating per clip on a track
{
    /// <summary>How visible the clip is at a timeline frame: 0 = see-through, 1 = solid.</summary>
    public double VisibilityAt(long frame) => Ramp(frame, FadeInFrames, FadeOutFrames);

    /// <summary>How bright the clip is at a timeline frame: 0 = black, 1 = normal.</summary>
    public double BrightnessAt(long frame) => Ramp(frame, BlackInFrames, BlackOutFrames);

    private double Ramp(long frame, long rise, long fall)
    {
        double value = 1;
        if (rise > 0) value = Math.Min(value, Math.Clamp((frame - Clip.Start) / (double)rise, 0, 1));
        if (fall > 0) value = Math.Min(value, Math.Clamp((Clip.End - frame) / (double)fall, 0, 1));
        return value;
    }
}

/// <summary>
/// Works out what transitions do to the clips of one track. The export, the preview picture
/// and the preview sound all use this, so they always agree.
/// </summary>
public static class TransitionPlan
{
    public const long DefaultFrames = 30; // 1 second at 30 fps

    public static List<RenderClip> Expand(Track track, double fps)
    {
        var clips = track.Clips.OrderBy(c => c.Start).ToList();
        int n = clips.Count;
        var copies = clips.Select(c => c.Copy()).ToList();
        var fadeIn = new long[n];
        var fadeOut = new long[n];
        var blackIn = new long[n];
        var blackOut = new long[n];
        bool isAudio = track.Kind == TrackKind.Audio;

        for (int i = 0; i < n; i++)
        {
            Clip c = clips[i];
            int prev = i > 0 && clips[i - 1].End == c.Start ? i - 1 : -1;
            int next = i + 1 < n && clips[i + 1].Start == c.End ? i + 1 : -1;

            if (c.TransitionIn is Transition t)
            {
                long d = Math.Clamp(t.Frames, 1, c.Length);
                if (t.Kind == TransitionKind.CrossDissolve)
                {
                    // Centred on the cut: the next clip starts half a transition early and the
                    // previous one keeps going half a transition longer, as far as their files allow.
                    long half = prev < 0 ? 0 : Math.Min(d / 2, Math.Min(
                        TimelineEdits.RoomToGrow(c, fps).BeforeStart,
                        TimelineEdits.RoomToGrow(clips[prev], fps).AfterEnd));
                    if (half > 0)
                    {
                        TimelineEdits.MoveEdges(copies[i], -half, 0);
                        TimelineEdits.MoveEdges(copies[prev], 0, half);
                        fadeIn[i] = Math.Max(fadeIn[i], half * 2);
                        if (isAudio) fadeOut[prev] = Math.Max(fadeOut[prev], half * 2); // sound: cross-fade
                    }
                    else
                    {
                        fadeIn[i] = Math.Max(fadeIn[i], d); // no spare footage: just fade in
                    }
                }
                else if (prev >= 0)
                {
                    // Dip to black at a cut: half to black, half back.
                    blackOut[prev] = Math.Max(blackOut[prev], Math.Min(d / 2, clips[prev].Length));
                    blackIn[i] = Math.Max(blackIn[i], Math.Max(1, d / 2));
                }
                else
                {
                    blackIn[i] = Math.Max(blackIn[i], d);
                }
            }

            // A transition at the end only counts when the next clip doesn't have one at its start.
            if (c.TransitionOut is Transition o && !(next >= 0 && clips[next].TransitionIn is not null))
            {
                long d = Math.Clamp(o.Frames, 1, c.Length);
                if (o.Kind == TransitionKind.CrossDissolve) fadeOut[i] = Math.Max(fadeOut[i], d);
                else blackOut[i] = Math.Max(blackOut[i], d);
            }
        }

        return Enumerable.Range(0, n)
            .Select(i => new RenderClip(copies[i], clips[i], fadeIn[i], fadeOut[i], blackIn[i], blackOut[i], i % 2))
            .ToList();
    }
}
