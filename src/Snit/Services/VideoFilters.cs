using Snit.Core;

namespace Snit.Services;

/// <summary>
/// FFmpeg recipes for a clip's colour, speed and reverse. The colour recipe is used both by the
/// export and by the preview decoder, so the preview shows exactly the colours you will export.
/// Every recipe starts with a comma (ready to append to a filter chain) or is empty.
/// </summary>
public static class VideoFilters
{
    private static string N(double v) => ProcessRunner.Num(Math.Round(v, 4));

    /// <summary>Exposure and temperature (channel gains), contrast and saturation (eq).</summary>
    public static string Color(ClipLook look)
    {
        var parts = new List<string>();

        if (look.Exposure != 0 || look.Temperature != 0)
        {
            // Exposure: +1 stop doubles the light. Temperature: warmer = more red, less blue.
            double gain = Math.Pow(2, look.Exposure);
            double warm = look.Temperature / 100 * 0.15;
            parts.Add($"colorchannelmixer=rr={N(gain * (1 + warm))}:gg={N(gain * (1 + warm * 0.15))}:bb={N(gain * (1 - warm))}");
        }

        if (look.Contrast != 0 || look.Saturation != 100)
        {
            // eq: contrast 1 = unchanged (we use 0.2–1.8), saturation 1 = unchanged (0 = grey).
            double contrast = 1 + look.Contrast / 100 * 0.8;
            double saturation = look.Saturation / 100;
            parts.Add($"eq=contrast={N(contrast)}:saturation={N(saturation)}");
        }

        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    /// <summary>Video speed and reverse. Reverse comes first (it plays the clip's frames backwards).</summary>
    public static string VideoSpeed(double speed, bool reverse)
    {
        string result = reverse ? ",reverse" : "";
        if (Math.Abs(speed - 1) > 0.0001) result += $",setpts=(PTS-STARTPTS)/{N(speed)}";
        return result;
    }

    /// <summary>
    /// Sound speed and reverse. "atempo" changes speed without making voices high or low;
    /// it sounds best between 0.5× and 2×, so bigger changes are done in steps.
    /// </summary>
    public static string AudioSpeed(double speed, bool reverse)
    {
        var parts = new List<string>();
        if (reverse) parts.Add("areverse");
        double rest = speed;
        while (rest > 2.0001) { parts.Add("atempo=2"); rest /= 2; }
        while (rest < 0.4999) { parts.Add("atempo=0.5"); rest /= 0.5; }
        if (Math.Abs(rest - 1) > 0.0001) parts.Add($"atempo={N(rest)}");
        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    /// <summary>
    /// Size, rotation and opacity, after the clip has been fitted into the frame.
    /// Rotation and opacity need see-through edges, so the picture becomes RGBA first.
    /// </summary>
    public static string Transform(ClipLook look, bool keepTransparency)
    {
        var parts = new List<string>();
        if (Math.Abs(look.Scale - 100) > 0.001)
        {
            double k = Math.Max(0.01, look.Scale / 100);
            parts.Add($"scale=max(2\\,trunc(iw*{N(k)}/2)*2):max(2\\,trunc(ih*{N(k)}/2)*2)");
        }

        bool needsAlpha = keepTransparency || look.Rotation != 0 || look.Opacity < 100;
        parts.Add(needsAlpha ? "format=rgba" : "format=yuv420p");

        if (look.Rotation != 0)
        {
            string a = N(look.Rotation * Math.PI / 180); // ffmpeg wants radians, clockwise
            parts.Add($"rotate={a}:ow=rotw({a}):oh=roth({a}):c=none");
        }
        if (look.Opacity < 100)
            parts.Add($"colorchannelmixer=aa={N(Math.Max(0, look.Opacity) / 100)}");

        return "," + string.Join(",", parts);
    }
}
