namespace Snit.Services;

/// <summary>
/// The FFmpeg recipe for a clip's sound settings (volume and fades). Export and preview
/// both use this, so what you hear while editing is what ends up in the video.
/// </summary>
public static class AudioFilters
{
    /// <summary>
    /// Returns e.g. ",volume=-6dB,afade=t=in:st=0:d=1" (starting with a comma, ready to append),
    /// or "" when the clip has no sound settings.
    /// </summary>
    public static string ClipEffects(double volumeDb, double fadeInSeconds, double fadeOutSeconds, double clipSeconds)
    {
        var parts = new List<string>();
        if (Math.Abs(volumeDb) > 0.01)
            parts.Add($"volume={ProcessRunner.Num(volumeDb)}dB");

        double fadeIn = Math.Min(fadeInSeconds, clipSeconds);
        if (fadeIn > 0.001)
            parts.Add($"afade=t=in:st=0:d={ProcessRunner.Num(fadeIn)}");

        double fadeOut = Math.Min(fadeOutSeconds, clipSeconds);
        if (fadeOut > 0.001)
            parts.Add($"afade=t=out:st={ProcessRunner.Num(clipSeconds - fadeOut)}:d={ProcessRunner.Num(fadeOut)}");

        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }
}
