using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Draws a title as a picture the size of the video, with a see-through background.
/// The preview shows this picture on top of the video, and the export saves it as a PNG
/// for FFmpeg to lay over the video. Both use the same drawing, so they look identical.
/// </summary>
public static class TitleRenderer
{
    public static BitmapSource Render(TitleStyle style, int width, int height)
        => OnWpfThread(() => RenderCore(style, width, height));

    /// <summary>Saves the title as a PNG file (used for export).</summary>
    public static void SavePng(TitleStyle style, int width, int height, string path)
        => OnWpfThread(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderCore(style, width, height)));
            using var file = File.Create(path);
            encoder.Save(file);
            return true;
        });

    private static BitmapSource RenderCore(TitleStyle style, int width, int height)
    {
        double scale = height / 1080.0; // sizes are measured in a 1080-pixel-high picture
        var font = new FontFamily($"{style.FontFamily}, Segoe UI");
        var typeface = new Typeface(font, FontStyles.Normal, style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

        FormattedText MakeText(Brush brush) => new(
            style.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            Math.Max(1, style.FontSize * scale), brush, 1.0)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = width * 0.95, // wrap long lines inside the picture
        };

        var text = MakeText(new SolidColorBrush(ParseColor(style.Color)));
        // With centred text, x is the middle of the text; y is its top, so move up by half its height.
        var origin = new Point(style.X * width - text.MaxTextWidth / 2, style.Y * height - text.Height / 2);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (style.Shadow)
            {
                // A soft shadow: a few faint copies of the text slightly down and to the right.
                double offset = Math.Max(1, 3 * scale);
                var shadow = MakeText(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)));
                for (int i = 1; i <= 3; i++)
                    dc.DrawText(shadow, new Point(origin.X + offset * i / 2, origin.Y + offset * i / 2));
            }
            dc.DrawText(text, origin);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>True for colour codes like "#FFCC00" (also "#FC0" and "#80FFCC00" with see-through).</summary>
    public static bool IsColor(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    /// <summary>"#FFCC00" → a colour. Anything unreadable becomes white.</summary>
    public static Color ParseColor(string text)
    {
        try { return (Color)ColorConverter.ConvertFromString(text); }
        catch (FormatException) { return Colors.White; }
        catch (NullReferenceException) { return Colors.White; }
    }

    /// <summary>WPF drawing must happen on a "single-threaded apartment" thread; make one if needed.</summary>
    private static T OnWpfThread<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return work();

        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }
}
