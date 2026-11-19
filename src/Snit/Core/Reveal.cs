using System.Globalization;

namespace Snit.Core;

/// <summary>
/// Wipe shapes. Each shape gives every pixel a number from 0 to 1: the moment it appears (or,
/// for wipe effects, disappears) as the wipe goes from 0 to 1. A left-to-right wipe is simply
/// "X / W". The shapes are written as FFmpeg expressions (X, Y = the pixel, W, H = the size),
/// so the export can use them directly, and <see cref="Formula"/> works out the very same
/// expression for the preview.
/// </summary>
public static class Reveal
{
    private static string N(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A soft-edged mask: 1 where the pixel has appeared at <paramref name="progress"/> (0..1).</summary>
    public static string Appeared(string shape, string progress, double feather)
    {
        double f = Math.Max(0.002, feather);
        return $"clip((({progress})*(1+{N(f)})-({shape}))/{N(f)},0,1)";
    }

    /// <summary>Random-looking but repeatable number 0..1 for a block (the same in FFmpeg and the preview).</summary>
    public static string Random(string a, string b) => $"mod(abs(sin(({a})*12.9898+({b})*78.233))*43758.5453,1)";

    /// <summary>A straight wipe at an angle (0° = from the left, 90° = from the top).</summary>
    public static string Linear(double angleDegrees)
    {
        double a = angleDegrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double reach = 0.5 * (Math.Abs(c) + Math.Abs(s));
        return $"((X/W-0.5)*{N(c)}+(Y/H-0.5)*{N(s)})/{N(Math.Max(1e-6, reach))}*0.5+0.5";
    }

    /// <summary>Like a clock hand, clockwise from <paramref name="startDegrees"/> (0 = twelve o'clock).</summary>
    public static string Clock(double startDegrees = 0)
        => $"mod(atan2(X/W-0.5,0.5-Y/H)/(2*PI)+1-{N(startDegrees / 360)},1)";

    /// <summary>Stripes: each of <paramref name="bands"/> stripes wipes on its own.</summary>
    public static string Blinds(int bands, bool upright) => upright ? $"mod(X/W*{bands},1)" : $"mod(Y/H*{bands},1)";

    /// <summary>Blocks appearing in random order.</summary>
    public static string Blocks(string blockWidth, string blockHeight) => Random($"floor(X/({blockWidth}))", $"floor(Y/({blockHeight}))");
}
