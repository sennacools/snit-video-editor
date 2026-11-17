using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snit.Views;

/// <summary>Saves a PNG picture of a window (used for the README screenshot).</summary>
public static class ScreenshotHelper
{
    public static void SaveWindow(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        double dpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpiScale),
            (int)Math.Ceiling(content.ActualHeight * dpiScale),
            96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);

        // Paint the window background first; the content alone is transparent where nothing is drawn.
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
