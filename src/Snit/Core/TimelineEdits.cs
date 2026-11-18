namespace Snit.Core;

/// <summary>Basic cutting operations on a track's clips.</summary>
public static class TimelineEdits
{
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
                right.Start = end;
                right.In = other.In + (end - other.Start);
                right.Length = other.End - end;
                right.FadeInSeconds = 0;
                track.Clips.Add(right);
                other.Length = start - other.Start;
                other.FadeOutSeconds = 0;
            }
            else if (!startsInside)
            {
                // Only its tail is in the way: shorten it.
                other.Length = start - other.Start;
            }
            else
            {
                // Only its head is in the way: cut off the beginning.
                long cut = end - other.Start;
                other.Start = end;
                other.In += cut;
                other.Length -= cut;
            }
        }
    }

    /// <summary>Cuts a clip in two at a timeline frame. Returns the new right-hand piece, or null if the frame isn't inside the clip.</summary>
    public static Clip? Split(Track track, Clip clip, long frame)
    {
        if (frame <= clip.Start || frame >= clip.End)
            return null;

        long leftLength = frame - clip.Start;
        var right = clip.Copy();
        right.Id = Guid.NewGuid();
        right.Start = frame;
        right.In = clip.In + leftLength;
        right.Length = clip.Length - leftLength;

        clip.Length = leftLength;
        // The fade-in stays at the very start and the fade-out at the very end, not at the cut.
        clip.FadeOutSeconds = 0;
        right.FadeInSeconds = 0;
        track.Clips.Add(right);
        return right;
    }
}
