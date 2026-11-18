using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snit.Views;

public partial class ProgramMonitorView : UserControl
{
    public ProgramMonitorView() => InitializeComponent();

    /// <summary>The Source scrub bar fits the whole file in its width.</summary>
    private void SourceRuler_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel main) main.Source.ScrubWidth = e.NewSize.Width;
    }

    /// <summary>A small still of what the monitor shows (without the guides), for the export dialog.</summary>
    public ImageSource? Capture()
    {
        if (Picture.ActualWidth <= 0 || Picture.ActualHeight <= 0) return null;
        // Rendered at its own size (project pixels), then shrunk to a quarter.
        var full = new RenderTargetBitmap((int)Picture.ActualWidth, (int)Picture.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        full.Render(Picture);
        var small = new TransformedBitmap(full, new ScaleTransform(0.25, 0.25));
        small.Freeze();
        return small;
    }
}
