using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>
/// One track's picture in the program monitor. The monitor stacks these (V1 at the bottom)
/// and shows each with its clip's size, rotation, position and opacity. Sizes are measured in
/// project pixels (e.g. 1920×1080), the same as the clip's position.
/// </summary>
public partial class PreviewLayer : ObservableObject
{
    private WriteableBitmap? bitmap;

    [ObservableProperty] private ImageSource? source;
    [ObservableProperty] private bool isVisible;
    [ObservableProperty] private double width;
    [ObservableProperty] private double height;
    [ObservableProperty] private double scale = 1;
    [ObservableProperty] private double rotation;
    [ObservableProperty] private double offsetX;
    [ObservableProperty] private double offsetY;
    [ObservableProperty] private double opacity = 1;

    /// <summary>0 = normal, 1 = completely black (dip to black).</summary>
    [ObservableProperty] private double darken;

    public void Hide() => IsVisible = false;

    /// <summary>Shows decoded pixels (video or picture).</summary>
    public void ShowPicture(DecodedFrame picture, VideoSegment segment, ProjectSettings frame, double visibility = 1, double brightness = 1, ClipLook? look = null)
    {
        FramePool pool = picture.Pool;
        if (bitmap is null || bitmap.PixelWidth != pool.Width || bitmap.PixelHeight != pool.Height)
        {
            // Bgra32: the pixels may have see-through parts (picture edges, PNGs).
            bitmap = new WriteableBitmap(pool.Width, pool.Height, 96, 96, PixelFormats.Bgra32, null);
            Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, pool.Width, pool.Height), picture.Pixels, pool.Stride, 0);
        if (Source != bitmap) Source = bitmap;

        // The clip's own shape, fitted inside the project frame.
        double fit = segment.MediaWidth > 0 && segment.MediaHeight > 0
            ? Math.Min((double)frame.Width / segment.MediaWidth, (double)frame.Height / segment.MediaHeight)
            : 0;
        Width = fit > 0 ? segment.MediaWidth * fit : frame.Width;
        Height = fit > 0 ? segment.MediaHeight * fit : frame.Height;
        ApplyLook(look ?? segment.Look, visibility, brightness);
    }

    /// <summary>Shows a title (a full-frame picture with see-through background).</summary>
    public void ShowTitle(ImageSource picture, VideoSegment segment, ProjectSettings frame, double visibility = 1, double brightness = 1, ClipLook? look = null)
    {
        Source = picture;
        Width = frame.Width;
        Height = frame.Height;
        ApplyLook(look ?? segment.Look, visibility, brightness);
    }

    private void ApplyLook(ClipLook look, double visibility, double brightness)
    {
        Darken = Math.Clamp(1 - brightness, 0, 1);
        Scale = Math.Max(0.01, look.Scale / 100);
        Rotation = look.Rotation;
        OffsetX = look.PositionX;
        OffsetY = look.PositionY;
        Opacity = Math.Clamp(look.Opacity / 100 * visibility, 0, 1);
        IsVisible = true;
    }
}
