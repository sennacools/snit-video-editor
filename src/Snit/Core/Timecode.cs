namespace Snit.Core;

/// <summary>
/// Turns a time in seconds into an editor timecode: hours:minutes:seconds:frames.
/// Example at 30 fps: 65.5 seconds → "00:01:05:15".
/// </summary>
public static class Timecode
{
    public static string Format(double seconds, double framesPerSecond)
    {
        if (seconds < 0) seconds = 0;
        if (framesPerSecond <= 0) framesPerSecond = 30;

        // Work in whole frames so the numbers never drift.
        long totalFrames = (long)Math.Round(seconds * framesPerSecond);
        int fps = (int)Math.Round(framesPerSecond);

        long frames = totalFrames % fps;
        long totalSeconds = totalFrames / fps;
        long secs = totalSeconds % 60;
        long mins = totalSeconds / 60 % 60;
        long hours = totalSeconds / 3600;

        return $"{hours:00}:{mins:00}:{secs:00}:{frames:00}";
    }
}
