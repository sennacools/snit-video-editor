using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Snit.Core;

namespace Snit.Controls;

/// <summary>
/// The strip above the timeline tracks with tick marks and timecodes.
/// It draws itself, and redraws when the zoom (PixelsPerSecond) changes.
/// </summary>
public class TimeRuler : FrameworkElement
{
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FramesPerSecondProperty = DependencyProperty.Register(
        nameof(FramesPerSecond), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double FramesPerSecond { get => (double)GetValue(FramesPerSecondProperty); set => SetValue(FramesPerSecondProperty, value); }
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    // Label spacing to choose from, in seconds. We pick the smallest that leaves room for the text.
    private static readonly double[] LabelSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
    private const double MinLabelSpacing = 90; // pixels
    private const int TicksPerLabel = 5;

    private static readonly Typeface LabelFont = new(new FontFamily("JetBrains Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        double pps = PixelsPerSecond;
        if (pps <= 0 || width <= 0) return;

        double labelStep = LabelSteps.FirstOrDefault(s => s * pps >= MinLabelSpacing, LabelSteps[^1]);
        double tickStep = labelStep / TicksPerLabel;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var pen = new Pen(TickBrush, 1);
        pen.Freeze();

        int tickCount = (int)(width / (tickStep * pps)) + 1;
        for (int i = 0; i <= tickCount; i++)
        {
            // +0.5 puts 1-pixel lines exactly on a pixel so they look sharp, not blurry.
            double x = Math.Round(i * tickStep * pps) + 0.5;
            bool isLabel = i % TicksPerLabel == 0;
            double tickHeight = isLabel ? 10 : 5;
            dc.DrawLine(pen, new Point(x, height - tickHeight), new Point(x, height));

            if (isLabel)
            {
                var text = new FormattedText(
                    Timecode.Format(i * tickStep, FramesPerSecond),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelFont, 10, TextBrush, pixelsPerDip);
                dc.DrawText(text, new Point(x + 4, 3));
            }
        }

        // Thin line along the bottom edge.
        dc.DrawLine(pen, new Point(0, height - 0.5), new Point(width, height - 0.5));
    }
}
