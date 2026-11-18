namespace Snit.Core;

/// <summary>Basic cutting operations on a track's clips.</summary>
public static class TimelineEdits
{
    /// <summary>
    /// Moves a clip's start and/or end edge on the timeline (positive = to the right), and keeps
    /// the source in step: cutting off the start of a normal clip skips the first part of the file,
    /// but for a reversed clip the start shows the END of the file, so that part goes instead.
    /// Speed is included: at 200%, one timeline frame is two source frames.
    /// </summary>
    public static void MoveEdges(Clip clip, long startDelta, long endDelta)
    {
        double speed = clip.Speed;
        long sourceStart = clip.In;
        long sourceEnd = clip.In + clip.SourceLength;
        long startChange = (long)Math.Round(startDelta * speed);
        long endChange = (long)Math.Round(endDelta * speed);

        if (!clip.Reverse)
        {
            sourceStart += startChange; // the clip's start shows the start of the source
            sourceEnd += endChange;
        }
        else
        {
            sourceEnd -= startChange;   // the clip's start shows the end of the source
            sourceStart -= endChange;
        }

        clip.Start += startDelta;
        clip.Length += endDelta - startDelta;
        clip.In = Math.Max(0, sourceStart);
    }

    /// <summary>
    /// How many timeline frames a clip's edges can be dragged outwards before running out of
    /// source file. Pictures and titles can be stretched as far as you like.
    /// </summary>
    public static (long BeforeStart, long AfterEnd) RoomToGrow(Clip clip, double fps)
    {
        const long unlimited = long.MaxValue / 4;
        if (clip.Media.Kind is MediaKind.Image or MediaKind.Text) return (unlimited, unlimited);

        long mediaLength = clip.Media.LengthInFrames(fps);
        long unusedAtFileStart = clip.In;
        long unusedAtFileEnd = Math.Max(0, mediaLength - (clip.In + clip.SourceLength));
        // A reversed clip's start shows the end of the file, and its end the start of the file.
        long before = clip.Reverse ? unusedAtFileEnd : unusedAtFileStart;
        long after = clip.Reverse ? unusedAtFileStart : unusedAtFileEnd;
        return ((long)Math.Floor(before / clip.Speed), (long)Math.Floor(after / clip.Speed));
    }

    /// <summary>
    /// "Overwrite" edit: makes room for the range [start, end) on a track by cutting away
    /// whatever is there. Clips in <paramref name="keep"/> are left alone (the ones being placed).
    /// </summary>
    public static void ClearRange(Track track, long start, long end, ICollection<Clip> keep)
    {
        foreach (Clip other in track.Clips.ToList())
        {
            if (keep.Contains(other) || other.End <= start || other.Start >= end)
                continue; // not in the way

            bool startsInside = other.Start >= start;
            bool endsInside = other.End <= end;

            if (startsInside && endsInside)
            {
                // Completely covered: remove it.
                track.Clips.Remove(other);
            }
            else if (!startsInside && !endsInside)
            {
                // The range is in the middle of this clip: keep the left part and add a right part.
                var right = other.Copy();
                right.Id = Guid.NewGuid();
                right.LinkId = null; // the right piece has no partner of its own
                MoveEdges(right, end - right.Start, 0);
                right.FadeInSeconds = 0;
                right.TransitionIn = null;
                track.Clips.Add(right);

                MoveEdges(other, 0, start - other.End);
                other.FadeOutSeconds = 0;
                other.TransitionOut = null;
            }
            else if (!startsInside)
            {
                // Only its tail is in the way: shorten it.
                MoveEdges(other, 0, start - other.End);
            }
            else
            {
                // Only its head is in the way: cut off the beginning.
                MoveEdges(other, end - other.Start, 0);
            }
        }
    }

    /// <summary>Cuts a clip in two at a timeline frame. Returns the new right-hand piece, or null if the frame isn't inside the clip.</summary>
    public static Clip? Split(Track track, Clip clip, long frame)
    {
        if (frame <= clip.Start || frame >= clip.End)
            return null;

        var right = clip.Copy();
        right.Id = Guid.NewGuid();
        MoveEdges(right, frame - right.Start, 0); // right piece: everything after the cut
        MoveEdges(clip, 0, frame - clip.End);     // left piece: everything before it

        // The fade-in (and start transition) stays at the very start, the fade-out (and end
        // transition) at the very end, not at the cut.
        clip.FadeOutSeconds = 0;
        clip.TransitionOut = null;
        right.FadeInSeconds = 0;
        right.TransitionIn = null;
        track.Clips.Add(right);
        return right;
    }
}
