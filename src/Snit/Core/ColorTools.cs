using System.Globalization;

namespace Snit.Core;

/// <summary>
/// The colour wheels (shadows, midtones, highlights). Each wheel has a "puck": pushing it towards
/// a colour tints that part of the picture with that colour. X/Y go from -1 to 1 (the edge of the
/// wheel). The colour wheel on screen and the FFmpeg recipe both use <see cref="Shift"/>, so the
/// colour you push towards is the colour you get.
/// </summary>
public static class ColorWheel
{
    /// <summary>
    /// How much red, green and blue to add for a puck position (each between about -0.5 and 0.5,
    /// together adding up to zero, so the brightness stays the same). Right = red, then (turning
    /// anticlockwise) yellow, green, cyan, blue, magenta.
    /// </summary>
    public static (double R, double G, double B) Shift(double x, double y)
    {
        double radius = Math.Min(1, Math.Sqrt(x * x + y * y));
        if (radius < 1e-6) return (0, 0, 0);
        double angle = Math.Atan2(y, x);
        const double third = 2 * Math.PI / 3;
        double k = radius * 0.5;
        return (k * Math.Cos(angle), k * Math.Cos(angle - third), k * Math.Cos(angle - 2 * third));
    }

    /// <summary>The colour shown at a spot on the wheel (for drawing it), as 0–255 values.</summary>
    public static (byte R, byte G, byte B) Display(double x, double y)
    {
        var (r, g, b) = Shift(x, y);
        static byte C(double v) => (byte)Math.Clamp(Math.Round(255 * (0.5 + v * 1.2)), 0, 255);
        return (C(r), C(g), C(b));
    }
}

/// <summary>A point on a curve: input brightness X to output brightness Y, both 0–1.</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// Curves (like Lumetri's RGB curves). A curve is saved as text, e.g. "0/0 0.5/0.6 1/1" (the
/// way FFmpeg's curves filter writes it). Empty text means a straight line: no change.
/// </summary>
public static class Curves
{
    public static readonly IReadOnlyList<CurvePoint> Straight = [new(0, 0), new(1, 1)];

    public static IReadOnlyList<CurvePoint> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Straight;
        var points = new List<CurvePoint>();
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] xy = part.Split('/');
            if (xy.Length == 2 &&
                double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                points.Add(new CurvePoint(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        }
        points = points.GroupBy(p => Math.Round(p.X, 3)).Select(g => g.Last()).OrderBy(p => p.X).ToList();
        return points.Count >= 2 ? points : Straight;
    }

    /// <summary>Text for the points; "" when the curve is a straight line.</summary>
    public static string Format(IEnumerable<CurvePoint> points)
    {
        var list = points.OrderBy(p => p.X).ToList();
        if (IsStraight(list)) return "";
        return string.Join(" ", list.Select(p =>
            $"{Math.Round(p.X, 3).ToString(CultureInfo.InvariantCulture)}/{Math.Round(p.Y, 3).ToString(CultureInfo.InvariantCulture)}"));
    }

    public static bool IsStraight(IReadOnlyList<CurvePoint> points)
        => points.All(p => Math.Abs(p.X - p.Y) < 0.0005) && points.Count > 0
           && points[0].X < 0.0005 && points[^1].X > 0.9995;

    /// <summary>
    /// The curve's output at x: a smooth line through the points (a "natural cubic spline", the
    /// same kind FFmpeg uses), flat before the first and after the last point.
    /// </summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> p, double x)
    {
        int n = p.Count;
        if (n == 0) return x;
        if (x <= p[0].X) return p[0].Y;
        if (x >= p[^1].X) return p[^1].Y;
        if (n == 2) return p[0].Y + (p[1].Y - p[0].Y) * (x - p[0].X) / Math.Max(1e-9, p[1].X - p[0].X);

        // Second derivatives (natural spline: zero at both ends), by the tridiagonal algorithm.
        var h = new double[n - 1];
        for (int i = 0; i < n - 1; i++) h[i] = Math.Max(1e-9, p[i + 1].X - p[i].X);
        var m = new double[n];
        var c = new double[n];
        var d = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            double a = h[i - 1], b = 2 * (h[i - 1] + h[i]), cc = h[i];
            double r = 6 * ((p[i + 1].Y - p[i].Y) / h[i] - (p[i].Y - p[i - 1].Y) / h[i - 1]);
            double denom = b - a * c[i - 1];
            c[i] = cc / denom;
            d[i] = (r - a * d[i - 1]) / denom;
        }
        for (int i = n - 2; i >= 1; i--) m[i] = d[i] - c[i] * m[i + 1];

        int k = 0;
        while (k < n - 2 && x > p[k + 1].X) k++;
        double t = x - p[k].X, hk = h[k];
        double y = p[k].Y
                   + ((p[k + 1].Y - p[k].Y) / hk - hk * (2 * m[k] + m[k + 1]) / 6) * t
                   + m[k] / 2 * t * t
                   + (m[k + 1] - m[k]) / (6 * hk) * t * t * t;
        return Math.Clamp(y, 0, 1);
    }
}
