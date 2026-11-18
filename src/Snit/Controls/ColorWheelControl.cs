using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snit.Core;

namespace Snit.Controls;

/// <summary>
/// A colour wheel with a puck (Lumetri-style). Drag the puck towards a colour to tint that part
/// of the picture; double-click to put it back in the middle. <see cref="X"/> and <see cref="Y"/>
/// go from -1 to 1 (Y up).
/// </summary>
public sealed class ColorWheelControl : FrameworkElement
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double),
        typeof(ColorWheelControl), new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YProperty = DependencyProperty.Register(nameof(Y), typeof(double),
        typeof(ColorWheelControl), new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }

    /// <summary>The colour disc is the same for every wheel, so it is drawn once.</summary>
    private static readonly BitmapSource Disc = MakeDisc(192);

    private bool dragging;

    public ColorWheelControl()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Drag towards a colour to tint. Double-click to reset.";
    }

    private static BitmapSource MakeDisc(int size)
    {
        var pixels = new byte[size * size * 4];
        double r = size / 2.0;
        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            double x = (px + 0.5 - r) / r, y = (r - py - 0.5) / r;
            double d = Math.Sqrt(x * x + y * y);
            if (d > 1) continue;
            var (cr, cg, cb) = ColorWheel.Display(x, y);
            // Darker towards the middle, so the puck stands out; soft edge.
            double shade = 0.35 + 0.45 * d;
            double alpha = Math.Clamp((1 - d) * r, 0, 1);
            int i = (py * size + px) * 4;
            pixels[i] = (byte)(cb * shade * alpha);
            pixels[i + 1] = (byte)(cg * shade * alpha);
            pixels[i + 2] = (byte)(cr * shade * alpha);
            pixels[i + 3] = (byte)(255 * alpha);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private (Point Centre, double Radius) Geometry()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        return (new Point(ActualWidth / 2, ActualHeight / 2), Math.Max(1, size / 2 - 1));
    }

    protected override Size MeasureOverride(Size available)
    {
        double size = double.IsInfinity(available.Width) ? 96 : Math.Min(available.Width, double.IsInfinity(available.Height) ? available.Width : available.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (c, r) = Geometry();
        dc.DrawImage(Disc, new Rect(c.X - r, c.Y - r, r * 2, r * 2));
        var line = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
        dc.DrawLine(line, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
        dc.DrawLine(line, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), c, r, r);

        var puck = new Point(c.X + X * r, c.Y - Y * r);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), puck, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), puck, 5, 5);
    }

    private void MoveTo(Point p)
    {
        var (c, r) = Geometry();
        double x = (p.X - c.X) / r, y = (c.Y - p.Y) / r;
        double d = Math.Sqrt(x * x + y * y);
        if (d > 1) { x /= d; y /= d; }
        // Slow near the middle is fine: small moves are what colour grading needs.
        double nx = Math.Round(x, 3), ny = Math.Round(y, 3);
        X = nx;
        Y = ny;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            X = 0;
            Y = 0;
            e.Handled = true;
            return;
        }
        dragging = CaptureMouse();
        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) MoveTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = false;
    }
}
