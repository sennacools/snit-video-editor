using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snit.Core;

namespace Snit.Controls;

/// <summary>
/// The strip above the timeline tracks with tick marks, timecodes and the orange
/// playhead marker. Click or drag in it to move the playhead (scrubbing).
/// </summary>
public class TimeRuler : FrameworkElement
{
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FramesPerSecondProperty = DependencyProperty.Register(
        nameof(FramesPerSecond), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the timeline is scrolled, in pixels.</summary>
    public static readonly DependencyProperty ScrollXProperty = DependencyProperty.Register(
        nameof(ScrollX), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Playhead position in seconds. Dragging in the ruler changes it.</summary>
    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(
        nameof(PlayheadBrush), typeof(Brush), typeof(TimeRuler),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PixelsPerSecond { get => (double)GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double FramesPerSecond { get => (double)GetValue(FramesPerSecondProperty); set => SetValue(FramesPerSecondProperty, value); }
    public double ScrollX { get => (double)GetValue(ScrollXProperty); set => SetValue(ScrollXProperty, value); }
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }
    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush PlayheadBrush { get => (Brush)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    // Label spacing to choose from, in seconds. We pick the smallest that leaves room for the text.
    private static readonly double[] LabelSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
    private const double MinLabelSpacing = 90; // pixels
    private const int TicksPerLabel = 5;

    private static readonly Typeface LabelFont = new(new FontFamily("JetBrains Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Geometry Marker = Geometry.Parse("M-6.5,0 L6.5,0 L6.5,9 L0,15 L-6.5,9 Z");

    public TimeRuler() => Cursor = Cursors.Hand;

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

        // Only draw the ticks that are visible.
        long firstTick = (long)Math.Floor(ScrollX / (tickStep * pps));
        long lastTick = (long)Math.Ceiling((ScrollX + width) / (tickStep * pps));
        for (long i = firstTick; i <= lastTick; i++)
        {
            // +0.5 puts 1-pixel lines exactly on a pixel so they look sharp, not blurry.
            double x = Math.Round(i * tickStep * pps - ScrollX) + 0.5;
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

        // Playhead marker.
        double px = Math.Round(PlayheadSeconds * pps - ScrollX) + 0.5;
        if (px > -8 && px < width + 8)
        {
            dc.PushTransform(new TranslateTransform(px, height - 15));
            dc.DrawGeometry(PlayheadBrush, null, Marker);
            dc.Pop();
        }
    }

    // ---- Scrubbing ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        MovePlayhead(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) MovePlayhead(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void MovePlayhead(double x)
    {
        if (PixelsPerSecond <= 0) return;
        PlayheadSeconds = Math.Max(0, (x + ScrollX) / PixelsPerSecond);
    }
}
