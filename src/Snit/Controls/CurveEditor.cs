using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snit.Core;

namespace Snit.Controls;

/// <summary>
/// A curve you can bend (Lumetri-style RGB curves). Click to add a point and drag it; right-click
/// a point to remove it; double-click an empty spot to make the line straight again.
/// <see cref="Points"/> is the curve as text (see <see cref="Curves"/>).
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(string),
        typeof(CurveEditor), new FrameworkPropertyMetadata("",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((CurveEditor)d).OnPointsChanged()));

    public static readonly DependencyProperty CurveBrushProperty = DependencyProperty.Register(nameof(CurveBrush), typeof(Brush),
        typeof(CurveEditor), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Points { get => (string)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Brush CurveBrush { get => (Brush)GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }

    private const double Pad = 6, HitRadius = 8;

    /// <summary>The points being edited (kept while dragging, so points never merge under the mouse).</summary>
    private List<CurvePoint> points = [.. Curves.Straight];
    private int dragIndex = -1;
    private bool writing;

    public CurveEditor()
    {
        Cursor = Cursors.Cross;
        ToolTip = "Click to add a point and drag it. Right-click a point to remove it. Double-click to reset.";
    }

    private void OnPointsChanged()
    {
        if (writing || dragIndex >= 0) return;
        points = [.. Curves.Parse(Points)];
    }

    private Rect Area => new(Pad, Pad, Math.Max(1, ActualWidth - Pad * 2), Math.Max(1, ActualHeight - Pad * 2));

    private Point ToScreen(CurvePoint p) { Rect a = Area; return new Point(a.Left + p.X * a.Width, a.Bottom - p.Y * a.Height); }

    private CurvePoint ToCurve(Point s)
    {
        Rect a = Area;
        return new CurvePoint(Math.Clamp((s.X - a.Left) / a.Width, 0, 1), Math.Clamp((a.Bottom - s.Y) / a.Height, 0, 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        Rect a = Area;
        dc.DrawRoundedRectangle((Brush)FindResource("InputBrush"), new Pen((Brush)FindResource("BorderBrush"), 1),
            new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 4, 4);

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++)
        {
            double x = a.Left + a.Width * i / 4, y = a.Top + a.Height * i / 4;
            dc.DrawLine(grid, new Point(x, a.Top), new Point(x, a.Bottom));
            dc.DrawLine(grid, new Point(a.Left, y), new Point(a.Right, y));
        }
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash },
            a.BottomLeft, a.TopRight);

        // The curve itself.
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            int steps = Math.Max(32, (int)a.Width);
            for (int i = 0; i <= steps; i++)
            {
                double x = (double)i / steps;
                Point s = ToScreen(new CurvePoint(x, Curves.Evaluate(points, x)));
                if (i == 0) g.BeginFigure(s, false, false);
                else g.LineTo(s, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(CurveBrush, 1.6), geometry);

        foreach (CurvePoint p in points)
            dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), ToScreen(p), 3.5, 3.5);
    }

    private int PointAt(Point s)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Point p = ToScreen(points[i]);
            if (Math.Abs(p.X - s.X) <= HitRadius && Math.Abs(p.Y - s.Y) <= HitRadius) return i;
        }
        return -1;
    }

    private void Write()
    {
        writing = true;
        Points = Curves.Format(points);
        writing = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point s = e.GetPosition(this);
        int hit = PointAt(s);
        if (e.ClickCount == 2 && hit < 0)
        {
            points = [.. Curves.Straight];
            Write();
            e.Handled = true;
            return;
        }
        if (hit < 0)
        {
            CurvePoint p = ToCurve(s);
            // Not on top of an existing point's X (that would make the curve jump).
            if (points.Any(q => Math.Abs(q.X - p.X) < 0.02)) return;
            points.Add(p);
            points.Sort((u, v) => u.X.CompareTo(v.X));
            hit = points.IndexOf(p);
            Write();
        }
        dragIndex = hit;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragIndex < 0) return;
        CurvePoint p = ToCurve(e.GetPosition(this));
        // A point stays between its neighbours.
        double min = dragIndex > 0 ? points[dragIndex - 1].X + 0.01 : 0;
        double max = dragIndex < points.Count - 1 ? points[dragIndex + 1].X - 0.01 : 1;
        points[dragIndex] = new CurvePoint(Math.Clamp(p.X, min, max), p.Y);
        Write();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        dragIndex = -1;
        ReleaseMouseCapture();
        points = [.. Curves.Parse(Points)];
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragIndex = -1;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int hit = PointAt(e.GetPosition(this));
        if (hit >= 0 && points.Count > 2)
        {
            points.RemoveAt(hit);
            Write();
        }
        e.Handled = true;
    }
}
