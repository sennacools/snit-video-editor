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
    [ObservableProperty] private double scaleX = 1;
    [ObservableProperty] private double scaleY = 1;

    /// <summary>A wipe's shape while a transition runs (an opacity mask), or null.</summary>
    [ObservableProperty] private Brush? transitionMask;

    /// <summary>0 = normal, 1 = completely white (dip to white).</summary>
    [ObservableProperty] private double lighten;

    private WriteableBitmap? maskBitmap;
    private byte[] maskPixels = [];
    [ObservableProperty] private double rotation;
    [ObservableProperty] private double offsetX;
    [ObservableProperty] private double offsetY;
    [ObservableProperty] private double opacity = 1;

    /// <summary>0 = normal, 1 = completely black (dip to black).</summary>
    [ObservableProperty] private double darken;

    public void Hide() => IsVisible = false;

    /// <summary>
    /// Puts a bundle of pictures from the player on a monitor's layers (one per place in the
    /// bundle; extra layers are hidden) and gives the picture buffers back.
    /// </summary>
    public static void ApplyBundle(IList<PreviewLayer> layers, LayerFrame?[] bundle, ProjectSettings frame,
        Func<TitleStyle, ImageSource> titlePicture)
    {
        while (layers.Count < bundle.Length) layers.Add(new PreviewLayer());
        for (int i = 0; i < layers.Count; i++)
        {
            LayerFrame? layer = i < bundle.Length ? bundle[i] : null;
            if (layer is null) layers[i].Hide();
            else if (layer.Segment.Title is TitleStyle title)
                layers[i].ShowTitle(titlePicture(title), layer.Segment, frame, layer.Visibility, layer.Brightness, layer.Look, layer.Transition, layer.White);
            else if (layer.Picture is DecodedFrame picture)
                layers[i].ShowPicture(picture, layer.Segment, frame, layer.Visibility, layer.Brightness, layer.Look, layer.Transition, layer.White);
        }
        PlaybackEngine.Release(bundle);
    }

    /// <summary>Shows decoded pixels (video or picture).</summary>
    public void ShowPicture(DecodedFrame picture, VideoSegment segment, ProjectSettings frame, double visibility = 1, double brightness = 1, ClipLook? look = null,
        TransitionState? transition = null, bool white = false)
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
        ApplyLook(look ?? segment.Look, visibility, brightness, transition, white, frame);
    }

    /// <summary>Shows a title (a full-frame picture with see-through background).</summary>
    public void ShowTitle(ImageSource picture, VideoSegment segment, ProjectSettings frame, double visibility = 1, double brightness = 1, ClipLook? look = null,
        TransitionState? transition = null, bool white = false)
    {
        Source = picture;
        Width = frame.Width;
        Height = frame.Height;
        ApplyLook(look ?? segment.Look, visibility, brightness, transition, white, frame);
    }

    private void ApplyLook(ClipLook look, double visibility, double brightness, TransitionState? transition, bool white, ProjectSettings frame)
    {
        TransitionState t = transition ?? TransitionState.None;
        double dip = Math.Clamp(1 - brightness, 0, 1);
        Darken = white ? 0 : dip;
        Lighten = white ? dip : 0;
        Scale = Math.Max(0.01, look.Scale / 100);
        ScaleX = Scale * t.Zoom * Math.Max(0.001, t.Squeeze);
        ScaleY = Scale * t.Zoom;
        Rotation = look.Rotation;
        OffsetX = look.PositionX + t.ShiftX * frame.Width;
        OffsetY = look.PositionY + t.ShiftY * frame.Height;
        Opacity = Math.Clamp(look.Opacity / 100 * visibility * t.Opacity, 0, 1);
        UpdateMask(t);
        IsVisible = true;
    }

    /// <summary>
    /// Draws the wipe shape of a running transition into a small mask picture (stretched over the
    /// clip), from the same formula the export gives to FFmpeg.
    /// </summary>
    private void UpdateMask(TransitionState t)
    {
        if (t.Mask is null || Width <= 0 || Height <= 0)
        {
            TransitionMask = null;
            return;
        }
        const int mw = 192;
        int mh = Math.Clamp((int)Math.Round(mw * Height / Width), 2, 400);
        if (maskBitmap is null || maskBitmap.PixelWidth != mw || maskBitmap.PixelHeight != mh)
        {
            maskBitmap = new WriteableBitmap(mw, mh, 96, 96, PixelFormats.Bgra32, null);
            maskPixels = new byte[mw * mh * 4];
            TransitionMask = null;
        }
        Formula.Compiled shape = Formula.Compile(t.Mask);
        for (int j = 0; j < mh; j++)
        {
            double y = (j + 0.5) / mh * Height;
            for (int i = 0; i < mw; i++)
            {
                double x = (i + 0.5) / mw * Width;
                double a = Math.Clamp(shape(x, y, Width, Height, t.MaskP, t.MaskQ, 0), 0, 1);
                int k = (j * mw + i) * 4;
                maskPixels[k] = maskPixels[k + 1] = maskPixels[k + 2] = 255;
                maskPixels[k + 3] = (byte)Math.Round(a * 255);
            }
        }
        maskBitmap.WritePixels(new Int32Rect(0, 0, mw, mh), maskPixels, mw * 4, 0);
        TransitionMask ??= new ImageBrush(maskBitmap) { Stretch = Stretch.Fill };
    }
}
