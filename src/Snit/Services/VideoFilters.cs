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
    /// An FFmpeg expression that follows keyframes: "if before keyframe 2, go evenly from 1 to 2,
    /// otherwise if before keyframe 3 ..." in terms of a time variable (seconds).
    /// </summary>
    public static string Piecewise(IReadOnlyList<Keyframe> keys, double fps, string time)
    {
        string expr = N(keys[^1].Value);
        for (int i = keys.Count - 1; i >= 1; i--)
        {
            Keyframe a = keys[i - 1], b = keys[i];
            double ta = a.Frame / fps, tb = b.Frame / fps;
            expr = $"if(lt({time},{N(tb)}),{N(a.Value)}+({N(b.Value - a.Value)})*({time}-{N(ta)})/{N(Math.Max(1e-6, tb - ta))},{expr})";
        }
        return $"if(lt({time},{N(keys[0].Frame / fps)}),{N(keys[0].Value)},{expr})";
    }

    /// <summary>
    /// Size, rotation and opacity for a clip with keyframes: they change every frame. The clip is
    /// put on a fixed-size see-through canvas (big enough for its largest size and rotation),
    /// because the next step (overlay) needs the same picture size every frame. Returns the filter
    /// chain and the overlay position expressions (the position is animated in the overlay).
    /// </summary>
    public static (string Chain, string X, string Y) AnimatedTransform(
        Clip clip, int frameWidth, int frameHeight, double fit, double fps, double clipStartSeconds)
    {
        var keys = clip.Keyframes;
        ClipLook look = clip.Look;
        bool Has(string name) => keys.TryGetValue(name, out var k) && k.Count > 0;
        string Value(string name, double fallback, string time) => Has(name) ? Piecewise(keys[name], fps, time) : N(fallback);

        var parts = new List<string> { "format=rgba" };

        // Size, per frame ("eval=frame" works the expression out again for every frame).
        if (Has("Scale"))
        {
            string s = Value("Scale", look.Scale, "t");
            parts.Add($"scale=w='max(2,trunc(iw*({s})/100/2)*2)':h='max(2,trunc(ih*({s})/100/2)*2)':eval=frame");
        }
        else if (Math.Abs(look.Scale - 100) > 0.001)
        {
            double k = Math.Max(0.01, look.Scale / 100);
            parts.Add($"scale=max(2\\,trunc(iw*{N(k)}/2)*2):max(2\\,trunc(ih*{N(k)}/2)*2)");
        }

        // The fixed canvas: as big as the frame at the largest size, square if it rotates.
        double maxScale = Math.Max(look.Scale, Has("Scale") ? keys["Scale"].Max(k => k.Value) : 0) / 100;
        double cw = frameWidth * Math.Max(1, maxScale), ch = frameHeight * Math.Max(1, maxScale);
        if (Has("Rotation") || look.Rotation != 0) cw = ch = Math.Sqrt(cw * cw + ch * ch);
        int canvasW = Math.Min(4 * frameWidth, (int)Math.Ceiling(cw / 2) * 2);
        int canvasH = Math.Min(4 * frameHeight, (int)Math.Ceiling(ch / 2) * 2);
        parts.Add($"pad=w={canvasW}:h={canvasH}:x=(ow-iw)/2:y=(oh-ih)/2:color=black@0:eval=frame");

        // Rotation around the middle, per frame.
        if (Has("Rotation") || look.Rotation != 0)
            parts.Add($"rotate=a='({Value("Rotation", look.Rotation, "t")})*PI/180':ow=iw:oh=ih:c=none");

        // Opacity, per frame (geq works per pixel; slower, so only used when opacity is animated).
        if (Has("Opacity"))
            parts.Add($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*clip(({Value("Opacity", look.Opacity, "T")})/100,0,1)'");
        else if (look.Opacity < 100)
            parts.Add($"colorchannelmixer=aa={N(Math.Max(0, look.Opacity) / 100)}");

        // Position: the overlay's time "t" is the timeline time, so count from the clip's start.
        string local = $"(t-{N(clipStartSeconds)})";
        string x = $"(main_w-overlay_w)/2+({Value("PositionX", look.PositionX, local)})*{N(fit)}";
        string y = $"(main_h-overlay_h)/2+({Value("PositionY", look.PositionY, local)})*{N(fit)}";
        return ("," + string.Join(",", parts), x, y);
    }

    /// <summary>
    /// Fades at the start and end of a clip, for transitions. With alpha: from/to see-through
    /// (cross-dissolve); without: from/to black (dip to black). Times are within the clip.
    /// </summary>
    public static string Fade(long inFrames, long outFrames, long clipFrames, double fps, bool alpha)
    {
        string a = alpha ? ":alpha=1" : "";
        string result = "";
        if (inFrames > 0) result += $",fade=t=in:st=0:d={N(inFrames / fps)}{a}";
        if (outFrames > 0) result += $",fade=t=out:st={N((clipFrames - outFrames) / fps)}:d={N(outFrames / fps)}{a}";
        return result;
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
