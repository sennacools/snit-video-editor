using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Snit.Controls;

/// <summary>
/// The stereo level meter next to the timeline: two bars (left, right) and a dB scale.
/// Give it the loudest sample (0–1) of each channel; it shows them in decibels, lets the
/// bars fall back smoothly, and keeps a small peak marker for a moment.
/// </summary>
public class StereoMeter : FrameworkElement
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(
        nameof(Left), typeof(double), typeof(StereoMeter), new PropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty RightProperty = DependencyProperty.Register(
        nameof(Right), typeof(double), typeof(StereoMeter), new PropertyMetadata(0.0, OnLevelChanged));

    /// <summary>Loudest sample of the left channel, 0 (silence) to 1 (maximum).</summary>
    public double Left { get => (double)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }

    /// <summary>Loudest sample of the right channel, 0 (silence) to 1 (maximum).</summary>
    public double Right { get => (double)GetValue(RightProperty); set => SetValue(RightProperty, value); }

    private const double FloorDb = -60;          // bottom of the meter
    private const double YellowFromDb = -6;      // above this the bar is yellow
    private const double FallDbPerSecond = 24;   // how fast the bars drop back
    private const double PeakHoldSeconds = 1.2;  // how long the peak marker waits before falling
    private const double BarWidth = 8, BarGap = 3;
    private static readonly double[] ScaleMarks = [0, -6, -12, -24, -48];

    // What is drawn right now, per channel (in dB).
    private readonly double[] shownDb = [FloorDb, FloorDb];
    private readonly double[] peakDb = [FloorDb, FloorDb];
    private readonly DateTime[] peakTime = [DateTime.MinValue, DateTime.MinValue];
    private readonly DispatcherTimer timer;
    private DateTime lastTick = DateTime.Now;

    private static readonly Typeface ScaleFont = new(new FontFamily("JetBrains Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public StereoMeter()
    {
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => Animate();
        Unloaded += (_, _) => timer.Stop();
    }

    private static double ToDb(double level) => level <= 0.000001 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(level));

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (StereoMeter)d;
        int channel = e.Property == LeftProperty ? 0 : 1;
        double db = ToDb((double)e.NewValue);

        // Rise immediately; falling happens smoothly in Animate().
        if (db > meter.shownDb[channel]) meter.shownDb[channel] = db;
        if (db >= meter.peakDb[channel])
        {
            meter.peakDb[channel] = db;
            meter.peakTime[channel] = DateTime.Now;
        }
        if (!meter.timer.IsEnabled)
        {
            meter.lastTick = DateTime.Now;
            meter.timer.Start();
        }
        meter.InvalidateVisual();
    }

    private void Animate()
    {
        DateTime now = DateTime.Now;
        double seconds = (now - lastTick).TotalSeconds;
        lastTick = now;

        double[] targets = [ToDb(Left), ToDb(Right)];
        bool moving = false;
        for (int c = 0; c < 2; c++)
        {
            shownDb[c] = Math.Max(targets[c], shownDb[c] - FallDbPerSecond * seconds);
            if ((now - peakTime[c]).TotalSeconds > PeakHoldSeconds)
                peakDb[c] = Math.Max(shownDb[c], peakDb[c] - FallDbPerSecond * seconds);
            moving |= shownDb[c] > FloorDb || peakDb[c] > FloorDb;
        }
        if (!moving) timer.Stop(); // everything is silent: no need to keep redrawing
        InvalidateVisual();
    }

    /// <summary>Height position of a dB value (0 dB at the top, the floor at the bottom).</summary>
    private double YOf(double db, double top, double height) => top + height * (db / FloorDb);

    protected override void OnRender(DrawingContext dc)
    {
        double top = 4, height = Math.Max(10, ActualHeight - 8);
        double barsLeft = Math.Max(0, (ActualWidth - (BarWidth * 2 + BarGap) - 24) / 2);

        var green = (Brush)Application.Current.FindResource("MeterGreenBrush");
        var yellow = (Brush)Application.Current.FindResource("MeterYellowBrush");
        var muted = (Brush)Application.Current.FindResource("MutedTextBrush");
        var back = (Brush)Application.Current.FindResource("MonitorBrush");
        double yellowY = YOf(YellowFromDb, top, height);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int c = 0; c < 2; c++)
        {
            double x = barsLeft + c * (BarWidth + BarGap);
            var bar = new Rect(x, top, BarWidth, height);
            dc.DrawRoundedRectangle(back, null, bar, 2, 2);

            // Faint colours so you can see the scale while it's quiet.
            dc.PushOpacity(0.15);
            dc.DrawRectangle(yellow, null, new Rect(x, top, BarWidth, yellowY - top));
            dc.DrawRectangle(green, null, new Rect(x, yellowY, BarWidth, top + height - yellowY));
            dc.Pop();

            // The lit part.
            double levelY = YOf(shownDb[c], top, height);
            if (levelY < top + height)
            {
                double greenTop = Math.Max(levelY, yellowY);
                dc.DrawRectangle(green, null, new Rect(x, greenTop, BarWidth, top + height - greenTop));
                if (levelY < yellowY)
                    dc.DrawRectangle(yellow, null, new Rect(x, levelY, BarWidth, yellowY - levelY));
            }

            // Peak marker.
            if (peakDb[c] > FloorDb + 1)
            {
                double py = YOf(peakDb[c], top, height);
                dc.DrawRectangle(peakDb[c] > YellowFromDb ? yellow : green, null, new Rect(x, py, BarWidth, 2));
            }
        }

        // The dB scale on the right.
        double textX = barsLeft + BarWidth * 2 + BarGap + 5;
        foreach (double mark in ScaleMarks)
        {
            var text = new FormattedText(mark.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, ScaleFont, 9, muted, dpi);
            double y = YOf(mark, top, height) - text.Height / 2;
            dc.DrawText(text, new Point(textX, Math.Clamp(y, 0, ActualHeight - text.Height)));
        }
    }
}
