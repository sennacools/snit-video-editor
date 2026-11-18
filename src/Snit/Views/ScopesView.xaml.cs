using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Views;

/// <summary>
/// Measures the program picture a few times a second (only while it is on screen) and draws the
/// chosen scope.
/// </summary>
public partial class ScopesView : UserControl
{
    private const int SampleWidth = 320;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    private ScopeKind kind = ScopeKind.Waveform;
    private WriteableBitmap? bitmap;
    private byte[] frame = [];

    private static readonly Dictionary<ScopeKind, string> Hints = new()
    {
        [ScopeKind.Waveform] = "Left to right is the picture's left to right; up is brighter. Keep it between the bottom and top lines so nothing is crushed to black or clipped to white.",
        [ScopeKind.Parade] = "Red, green and blue next to each other. Where the three line up, that part of the picture is neutral grey.",
        [ScopeKind.Vectorscope] = "The further from the centre, the stronger the colour. Faces sit on the orange line; the boxes are the pure colours.",
        [ScopeKind.Histogram] = "Dark to bright, left to right. A pile at an edge means detail is lost there.",
    };

    public ScopesView()
    {
        InitializeComponent();
        Hint.Text = Hints[kind];
        timer.Tick += (_, _) => Draw();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { timer.Start(); Draw(); }
            else timer.Stop();
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse(name, out ScopeKind chosen))
        {
            kind = chosen;
            if (Hint is not null) Hint.Text = Hints[kind];
            if (IsLoaded) Draw();
        }
    }

    private void Draw()
    {
        if (DataContext is not MainViewModel main || main.CaptureFrame?.Invoke(SampleWidth) is not BitmapSource picture) return;
        int w = picture.PixelWidth, h = picture.PixelHeight, stride = w * 4;
        if (frame.Length != stride * h) frame = new byte[stride * h];
        picture.CopyPixels(frame, stride, 0);

        ScopeImage scope = ScopeRenderer.Render(kind, frame, w, h, stride);
        if (bitmap is null || bitmap.PixelWidth != scope.Width || bitmap.PixelHeight != scope.Height)
        {
            bitmap = new WriteableBitmap(scope.Width, scope.Height, 96, 96, PixelFormats.Pbgra32, null);
            ScopePicture.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, scope.Width, scope.Height), scope.Pixels, scope.Width * 4, 0);
    }
}
