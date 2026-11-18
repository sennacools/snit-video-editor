namespace Snit.Services;

/// <summary>Which scope the Color workspace shows.</summary>
public enum ScopeKind { Waveform, Parade, Vectorscope, Histogram }

/// <summary>A drawn scope: BGRA pixels (premultiplied, fully opaque).</summary>
public sealed record ScopeImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Scopes measure the picture, so you can grade by numbers instead of by eye:
/// <list type="bullet">
/// <item>Waveform: how bright each column of the picture is (top = white, bottom = black).</item>
/// <item>Parade: the same for red, green and blue side by side.</item>
/// <item>Vectorscope: which colours there are and how strong (centre = grey, edge = vivid).</item>
/// <item>Histogram: how many pixels there are of each brightness, per colour.</item>
/// </list>
/// Works on a small copy of the preview picture (BGRA, as WPF gives it).
/// </summary>
public static class ScopeRenderer
{
    public const int Size = 256;

    public static ScopeImage Render(ScopeKind kind, byte[] bgra, int width, int height, int stride) => kind switch
    {
        ScopeKind.Parade => Parade(bgra, width, height, stride),
        ScopeKind.Vectorscope => Vectorscope(bgra, width, height, stride),
        ScopeKind.Histogram => Histogram(bgra, width, height, stride),
        _ => Waveform(bgra, width, height, stride),
    };

    /// <summary>Brightness (Rec. 709), 0–255.</summary>
    public static int Luma(int r, int g, int b) => Math.Clamp((int)Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b), 0, 255);

    private static ScopeImage Waveform(byte[] px, int w, int h, int stride)
    {
        var counts = new int[Size * Size];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            int col = x * Size / w, row = Size - 1 - Luma(px[i + 2], px[i + 1], px[i]);
            counts[row * Size + col]++;
        }
        var image = new ScopeImage(Size, Size, new byte[Size * Size * 4]);
        Fill(image, counts, Size, 0, (180, 255, 190));
        Levels(image);
        return image;
    }

    private static ScopeImage Parade(byte[] px, int w, int h, int stride)
    {
        const int part = Size / 2;  // each colour gets 128 columns
        int width = part * 3;
        var counts = new int[3][];
        for (int c = 0; c < 3; c++) counts[c] = new int[part * Size];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            int col = x * part / w;
            counts[0][(Size - 1 - px[i + 2]) * part + col]++; // red
            counts[1][(Size - 1 - px[i + 1]) * part + col]++; // green
            counts[2][(Size - 1 - px[i]) * part + col]++;     // blue
        }
        var image = new ScopeImage(width, Size, new byte[width * Size * 4]);
        Fill(image, counts[0], part, 0, (255, 90, 90));
        Fill(image, counts[1], part, part, (90, 255, 110), add: true);
        Fill(image, counts[2], part, part * 2, (100, 150, 255), add: true);
        Levels(image);
        return image;
    }

    /// <summary>Where a colour lands on the vectorscope (Rec. 709 Cb/Cr; 128,128 = grey).</summary>
    public static (int X, int Y) VectorPosition(int r, int g, int b)
    {
        double cb = -0.1146 * r - 0.3854 * g + 0.5 * b;
        double cr = 0.5 * r - 0.4542 * g - 0.0458 * b;
        return (Math.Clamp((int)Math.Round(128 + cb), 0, Size - 1), Math.Clamp((int)Math.Round(128 - cr), 0, Size - 1));
    }

    private static ScopeImage Vectorscope(byte[] px, int w, int h, int stride)
    {
        var counts = new int[Size * Size];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            var (vx, vy) = VectorPosition(px[i + 2], px[i + 1], px[i]);
            counts[vy * Size + vx]++;
        }
        var image = new ScopeImage(Size, Size, new byte[Size * Size * 4]);
        Clear(image);

        // Guides: the outer circle, a cross, the six colour targets and the skin tone line.
        for (int a = 0; a < 720; a++)
        {
            double t = a * Math.PI / 360;
            Plot(image, (int)(128 + 120 * Math.Cos(t)), (int)(128 + 120 * Math.Sin(t)), (55, 58, 64));
        }
        for (int k = 8; k < Size - 8; k++) { Plot(image, k, 128, (40, 42, 47)); Plot(image, 128, k, (40, 42, 47)); }
        foreach (var (r, g, b) in new[] { (191, 0, 0), (191, 191, 0), (0, 191, 0), (0, 191, 191), (0, 0, 191), (191, 0, 191) })
        {
            var (tx, ty) = VectorPosition(r, g, b);
            for (int d = -4; d <= 4; d++)
            {
                Plot(image, tx + d, ty - 4, (r / 2 + 50, g / 2 + 50, b / 2 + 50)); Plot(image, tx + d, ty + 4, (r / 2 + 50, g / 2 + 50, b / 2 + 50));
                Plot(image, tx - 4, ty + d, (r / 2 + 50, g / 2 + 50, b / 2 + 50)); Plot(image, tx + 4, ty + d, (r / 2 + 50, g / 2 + 50, b / 2 + 50));
            }
        }
        var (sx, sy) = VectorPosition(224, 172, 140); // a typical skin tone: faces sit on this line
        double angle = Math.Atan2(sy - 128, sx - 128);
        for (int k = 4; k < 120; k++)
            Plot(image, (int)(128 + k * Math.Cos(angle)), (int)(128 + k * Math.Sin(angle)), (90, 70, 55));

        Fill(image, counts, Size, 0, (235, 240, 245), add: true);
        return image;
    }

    private static ScopeImage Histogram(byte[] px, int w, int h, int stride)
    {
        const int height = 160;
        var bins = new int[3][];
        for (int c = 0; c < 3; c++) bins[c] = new int[Size];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            bins[0][px[i + 2]]++;
            bins[1][px[i + 1]]++;
            bins[2][px[i]]++;
        }
        // Scale to the tallest bar, ignoring pure black/white (a black frame would flatten the rest).
        int max = 1;
        for (int c = 0; c < 3; c++)
            for (int v = 1; v < Size - 1; v++) max = Math.Max(max, bins[c][v]);

        var image = new ScopeImage(Size, height, new byte[Size * height * 4]);
        Clear(image);
        (int, int, int)[] colours = [(200, 60, 60), (60, 200, 80), (70, 110, 220)];
        for (int c = 0; c < 3; c++)
        for (int v = 0; v < Size; v++)
        {
            int bar = (int)Math.Min(height, Math.Round((double)bins[c][v] / max * (height - 4)));
            for (int y = height - bar; y < height; y++) Add(image, v, y, colours[c]);
        }
        return image;
    }

    // ---- Drawing helpers ----

    private static void Clear(ScopeImage image)
    {
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = 20; image.Pixels[i + 1] = 18; image.Pixels[i + 2] = 17; image.Pixels[i + 3] = 255;
        }
    }

    /// <summary>Paints counts as brightness (log scale, so a few pixels still show).</summary>
    private static void Fill(ScopeImage image, int[] counts, int columns, int left, (int R, int G, int B) colour, bool add = false)
    {
        if (!add) Clear(image);
        int max = 1;
        foreach (int c in counts) max = Math.Max(max, c);
        double scale = 1 / Math.Log(1 + max);
        int rows = counts.Length / columns;
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < columns; col++)
        {
            int n = counts[row * columns + col];
            if (n == 0) continue;
            double level = 0.25 + 0.75 * Math.Log(1 + n) * scale;
            int i = (row * image.Width + left + col) * 4;
            image.Pixels[i] = (byte)Math.Min(255, image.Pixels[i] + colour.B * level);
            image.Pixels[i + 1] = (byte)Math.Min(255, image.Pixels[i + 1] + colour.G * level);
            image.Pixels[i + 2] = (byte)Math.Min(255, image.Pixels[i + 2] + colour.R * level);
            image.Pixels[i + 3] = 255;
        }
    }

    /// <summary>Faint lines at 0, 25, 50, 75 and 100% brightness.</summary>
    private static void Levels(ScopeImage image)
    {
        foreach (int level in new[] { 0, 64, 128, 191, 255 })
        {
            int row = Size - 1 - level;
            for (int x = 0; x < image.Width; x += 2) Add(image, x, row, (45, 45, 45));
        }
    }

    private static void Plot(ScopeImage image, int x, int y, (int R, int G, int B) colour)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = (byte)colour.B; image.Pixels[i + 1] = (byte)colour.G; image.Pixels[i + 2] = (byte)colour.R; image.Pixels[i + 3] = 255;
    }

    private static void Add(ScopeImage image, int x, int y, (int R, int G, int B) colour)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = (byte)Math.Min(255, image.Pixels[i] + colour.B);
        image.Pixels[i + 1] = (byte)Math.Min(255, image.Pixels[i + 1] + colour.G);
        image.Pixels[i + 2] = (byte)Math.Min(255, image.Pixels[i + 2] + colour.R);
        image.Pixels[i + 3] = 255;
    }
}
