using Snit.Core;

namespace Snit.Services;

/// <summary>
/// The FFmpeg recipe for a clip's sound settings (noise reduction, equaliser, volume, fades and
/// the track's mixer settings). Export and preview both use this, so what you hear while editing
/// is what ends up in the video.
/// </summary>
public static class AudioFilters
{
    /// <summary>
    /// Returns e.g. ",volume=-6dB,afade=t=in:st=0:d=1" (starting with a comma, ready to append),
    /// or "" when the clip has no sound settings.
    /// </summary>
    /// <param name="volumeKeys">Volume keyframes in dB (frames from the clip's start); they replace <paramref name="volumeDb"/>.</param>
    /// <param name="fps">Frames per second, to turn keyframe frames into seconds.</param>
    /// <param name="trackDb">The track's volume in the mixer.</param>
    /// <param name="pan">The track's balance: -1 left … 1 right.</param>
    public static string ClipEffects(double volumeDb, double fadeInSeconds, double fadeOutSeconds, double clipSeconds,
        ClipAudio? audio = null, IReadOnlyList<Keyframe>? volumeKeys = null, double fps = 30,
        double trackDb = 0, double pan = 0)
    {
        var parts = new List<string>();
        audio ??= ClipAudio.Default;

        // Clean up first, then shape the sound, then set how loud it is.
        if (audio.NoiseReduction > 0.5)
            parts.Add($"afftdn=nr={ProcessRunner.Num(Math.Round(audio.NoiseReduction * 0.3, 2))}:nf=-40");
        if (Math.Abs(audio.Low) > 0.05)
            parts.Add($"bass=g={ProcessRunner.Num(audio.Low)}:f=100");
        if (Math.Abs(audio.Mid) > 0.05)
            parts.Add($"equalizer=f=1000:t=q:w=1:g={ProcessRunner.Num(audio.Mid)}");
        if (Math.Abs(audio.High) > 0.05)
            parts.Add($"treble=g={ProcessRunner.Num(audio.High)}:f=8000");

        if (volumeKeys is { Count: > 0 })
        {
            // Changes over time: worked out again for every bit of sound ("eval=frame").
            string db = VideoFilters.Piecewise(volumeKeys, fps, "t");
            parts.Add($"volume='pow(10,({db})/20)':eval=frame");
        }
        else if (Math.Abs(volumeDb) > 0.01)
            parts.Add($"volume={ProcessRunner.Num(volumeDb)}dB");

        double fadeIn = Math.Min(fadeInSeconds, clipSeconds);
        if (fadeIn > 0.001)
            parts.Add($"afade=t=in:st=0:d={ProcessRunner.Num(fadeIn)}");

        double fadeOut = Math.Min(fadeOutSeconds, clipSeconds);
        if (fadeOut > 0.001)
            parts.Add($"afade=t=out:st={ProcessRunner.Num(clipSeconds - fadeOut)}:d={ProcessRunner.Num(fadeOut)}");

        if (Math.Abs(trackDb) > 0.01)
            parts.Add($"volume={ProcessRunner.Num(trackDb)}dB");
        if (Math.Abs(pan) > 0.005)
        {
            // Balance: turning right makes the left side quieter, and the other way round.
            double left = pan > 0 ? 1 - pan : 1, right = pan < 0 ? 1 + pan : 1;
            parts.Add($"pan=stereo|c0={ProcessRunner.Num(Math.Round(left, 3))}*c0|c1={ProcessRunner.Num(Math.Round(right, 3))}*c1");
        }

        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    /// <summary>The volume keyframes of a clip, or null.</summary>
    public static IReadOnlyList<Keyframe>? VolumeKeys(Clip clip)
        => Keyframes.Of(clip.Keyframes, Keyframes.Volume);
}
