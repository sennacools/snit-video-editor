using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snit.Core;
using Snit.ViewModels;

namespace Snit.Controls;

/// <summary>
/// The track area of the timeline. It draws the tracks, clips and playhead itself
/// (fast, even with many clips) and turns mouse actions into edits on the
/// <see cref="TimelineViewModel"/>: select, move, trim, razor, pan, zoom and
/// dropping media from the bin.
/// </summary>
public class TimelineCanvas : FrameworkElement
{
    public const double TrackHeight = 56;
    private const double ClipInset = 4;     // gap above and below a clip
    private const double EdgeGrabWidth = 6; // how close to an edge counts as "grab the edge"
    public const string MediaDragFormat = "Snit.MediaItem";

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline), typeof(TimelineViewModel), typeof(TimelineCanvas),
        new PropertyMetadata(null, OnTimelineChanged));

    public TimelineViewModel? Timeline
    {
        get => (TimelineViewModel?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public TimelineCanvas()
    {
        Focusable = true;
        AllowDrop = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (Timeline is not null) Timeline.ViewportWidth = ActualWidth; };
    }

    private static void OnTimelineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (TimelineCanvas)d;
        if (e.OldValue is TimelineViewModel old)
        {
            old.RedrawNeeded -= canvas.InvalidateVisual;
            old.PropertyChanged -= canvas.OnTimelinePropertyChanged;
        }
        if (e.NewValue is TimelineViewModel tl)
        {
            tl.RedrawNeeded += canvas.InvalidateVisual;
            tl.PropertyChanged += canvas.OnTimelinePropertyChanged;
            tl.ViewportWidth = canvas.ActualWidth;
        }
        canvas.InvalidateVisual();
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.ActiveTool)) UpdateCursor(Mouse.GetPosition(this));
        InvalidateVisual();
    }

    // ===================== Drawing =====================

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static readonly Typeface ClipFont = new(new FontFamily("Inter, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        var tl = Timeline;
        dc.DrawRectangle(Res("TimelineBrush"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (tl is null) return;

        var dividerPen = new Pen(Res("RowDividerBrush"), 1);
        var groupPen = new Pen(Res("BorderBrush"), 2);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < tl.Tracks.Count; i++)
        {
            Track track = tl.Tracks[i];
            double top = i * TrackHeight;

            if (track.IsLocked) // faint stripes on locked tracks
                dc.DrawRectangle(LockedPattern, null, new Rect(0, top, ActualWidth, TrackHeight));

            // Thicker line between the last video track and the first audio track.
            bool lastVideo = track.Kind == TrackKind.Video && i + 1 < tl.Tracks.Count && tl.Tracks[i + 1].Kind == TrackKind.Audio;
            double lineY = Math.Round(top + TrackHeight) - (lastVideo ? 1 : 0.5);
            dc.DrawLine(lastVideo ? groupPen : dividerPen, new Point(0, lineY), new Point(ActualWidth, lineY));

            if (track.IsOff) dc.PushOpacity(0.35);
            foreach (Clip clip in track.Clips)
                DrawClip(dc, tl, clip, track, top, dpi);
            if (track.IsOff) dc.Pop();
        }

        if (tl.Tracks.All(t => t.Clips.Count == 0) && dropGhost is null)
        {
            var hint = Text("Drag clips here from the media bin", Res("MutedTextBrush"), 12, dpi, FontWeights.Normal);
            dc.DrawText(hint, new Point(16, TrackHeight * 1 + (TrackHeight - hint.Height) / 2));
        }

        DrawDropGhost(dc, tl, dpi);
        DrawRazorLine(dc, tl);

        if (tl.SnapLineFrame is long snap)
            DrawVerticalLine(dc, tl.FrameToX(snap), Res("AccentBrush"), 1);

        DrawVerticalLine(dc, tl.FrameToX(tl.PlayheadFrame), Res("PlayheadBrush"), 1);
    }

    private void DrawClip(DrawingContext dc, TimelineViewModel tl, Clip clip, Track track, double top, double dpi)
    {
        double x1 = tl.FrameToX(clip.Start), x2 = tl.FrameToX(clip.End);
        if (x2 < 0 || x1 > ActualWidth) return; // off screen

        var rect = new Rect(x1, top + ClipInset, Math.Max(2, x2 - x1), TrackHeight - ClipInset * 2);
        bool isAudio = track.Kind == TrackKind.Audio;
        bool selected = tl.Selection.Contains(clip);

        string kind = isAudio ? "AudioClip" : clip.IsTitle ? "TextClip" : "VideoClip"; // green, purple or blue
        Brush fill = Res(kind + "Brush");
        Brush border = selected ? Res("SelectedClipBorderBrush") : Res(kind + "BorderBrush");
        var pen = new Pen(border, selected ? 2 : 1);

        dc.DrawRoundedRectangle(fill, null, rect, 5, 5);
        dc.PushClip(new RectangleGeometry(rect, 5, 5));

        double textLeft = rect.Left + 8;
        if (!isAudio && clip.Media.Thumbnail is ImageSource thumb && rect.Width > 48)
        {
            // Small picture at the start of video clips.
            double h = rect.Height, w = h * 16 / 9;
            dc.DrawImage(thumb, new Rect(rect.Left, rect.Top, Math.Min(w, rect.Width), h));
            textLeft = rect.Left + Math.Min(w, rect.Width) + 6;
        }
        if (isAudio)
            DrawWaveform(dc, tl, clip, rect);

        string name = clip.Media.IsOffline ? clip.Name + "  (file missing)" : clip.Name;
        var label = Text(name, Res("TextBrush"), 11, dpi, FontWeights.SemiBold);
        dc.DrawText(label, new Point(textLeft, rect.Top + 5));

        dc.Pop();
        dc.DrawRoundedRectangle(null, pen, Inflate(rect, selected ? -1 : -0.5), 5, 5);
    }

    /// <summary>
    /// Draws the sound as vertical lines: one per pixel, as tall as the loudest moment in that
    /// pixel. The clip's volume and fades are included, so you can see a fade.
    /// </summary>
    private void DrawWaveform(DrawingContext dc, TimelineViewModel tl, Clip clip, Rect rect)
    {
        float[]? points = clip.Media.Waveform;
        double mid = rect.Top + rect.Height / 2 + 4; // a little lower, below the clip name
        double maxHeight = rect.Height / 2 - 6;

        if (points is null || points.Length == 0)
        {
            // Still being read (or no sound): a thin centre line.
            dc.DrawLine(new Pen(Res("WaveformBrush"), 1) { DashStyle = DashStyles.Dot },
                new Point(rect.Left, Math.Round(mid) + 0.5), new Point(rect.Right, Math.Round(mid) + 0.5));
            return;
        }

        double fps = tl.Fps, pps = tl.PixelsPerSecond;
        double clipSeconds = clip.Length / fps;
        double gain = Math.Pow(10, clip.VolumeDb / 20); // decibels → multiplier
        double perPoint = 1.0 / MediaItem.WaveformPointsPerSecond;

        double left = Math.Max(rect.Left, 0), right = Math.Min(rect.Right, ActualWidth);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (double x = Math.Floor(left); x < right; x++)
            {
                double secondsIntoClip = (x - rect.Left) / pps;
                double sourceSeconds = clip.In / fps + secondsIntoClip;

                // Loudest point within this pixel.
                int from = (int)(sourceSeconds / perPoint);
                int to = Math.Max(from + 1, (int)((sourceSeconds + 1 / pps) / perPoint));
                float peak = 0;
                for (int i = Math.Max(0, from); i < to && i < points.Length; i++)
                    if (points[i] > peak) peak = points[i];

                double envelope = gain;
                if (clip.FadeInSeconds > 0) envelope *= Math.Clamp(secondsIntoClip / clip.FadeInSeconds, 0, 1);
                if (clip.FadeOutSeconds > 0) envelope *= Math.Clamp((clipSeconds - secondsIntoClip) / clip.FadeOutSeconds, 0, 1);

                // Square root: quiet sound still shows clearly (closer to how loud it sounds).
                double h = Math.Max(0.5, Math.Sqrt(Math.Min(1, peak * envelope)) * maxHeight);
                g.BeginFigure(new Point(x + 0.5, mid - h), false, false);
                g.LineTo(new Point(x + 0.5, mid + h), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Res("WaveformBrush"), 1), geometry);
    }

    private void DrawDropGhost(DrawingContext dc, TimelineViewModel tl, double dpi)
    {
        if (dropGhost is not { } g) return;
        int index = tl.Tracks.ToList().IndexOf(g.Track);
        double x1 = tl.FrameToX(g.Start), x2 = tl.FrameToX(g.Start + g.Length);
        var rect = new Rect(x1, index * TrackHeight + ClipInset, Math.Max(2, x2 - x1), TrackHeight - ClipInset * 2);
        var pen = new Pen(Res("AccentBrush"), 1.5) { DashStyle = DashStyles.Dash };
        dc.DrawRoundedRectangle(Res("AccentTintBrush"), pen, rect, 5, 5);
        dc.DrawText(Text(g.Media.Name, Res("TextBrush"), 11, dpi, FontWeights.SemiBold), new Point(rect.Left + 8, rect.Top + 5));
    }

    private void DrawRazorLine(DrawingContext dc, TimelineViewModel tl)
    {
        if (tl.ActiveTool != "Razor" || razorHover is not { } hover) return;
        var pen = new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash };
        double x = Math.Round(tl.FrameToX(hover.Frame)) + 0.5;
        foreach (Clip c in tl.Project.LinkedGroup(hover.Clip))
        {
            int i = tl.Tracks.ToList().IndexOf(tl.Project.TrackOf(c));
            dc.DrawLine(pen, new Point(x, i * TrackHeight + ClipInset), new Point(x, (i + 1) * TrackHeight - ClipInset));
        }
    }

    private void DrawVerticalLine(DrawingContext dc, double x, Brush brush, double thickness)
    {
        if (x < -1 || x > ActualWidth + 1) return;
        x = Math.Round(x) + 0.5;
        dc.DrawLine(new Pen(brush, thickness), new Point(x, 0), new Point(x, ActualHeight));
    }

    private FormattedText Text(string text, Brush brush, double size, double dpi, FontWeight weight)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface(ClipFont.FontFamily, FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi);

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r.Width > 0 ? r : new Rect(r.X, r.Y, 1, r.Height); }

    private static readonly Brush LockedPattern = CreateLockedPattern();

    private static Brush CreateLockedPattern()
    {
        var brush = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 2),
            Geometry.Parse("M0,10 L10,0")))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    // ===================== Finding things under the mouse =====================

    private Track? TrackAt(double y)
    {
        var tl = Timeline;
        int i = (int)Math.Floor(y / TrackHeight);
        return tl is not null && y >= 0 && i < tl.Tracks.Count ? tl.Tracks[i] : null;
    }

    private Clip? ClipAt(Point p)
    {
        var tl = Timeline;
        Track? track = TrackAt(p.Y);
        if (tl is null || track is null) return null;
        double frame = tl.ExactFrameAt(p.X);
        // Last one wins, so a clip drawn on top is the one you click.
        return track.Clips.LastOrDefault(c => c.Start <= frame && frame < c.End);
    }

    /// <summary>-1 = near the left edge, +1 = near the right edge, 0 = in the middle.</summary>
    private int EdgeAt(Clip clip, double x)
    {
        var tl = Timeline!;
        double x1 = tl.FrameToX(clip.Start), x2 = tl.FrameToX(clip.End);
        double grab = Math.Min(EdgeGrabWidth, (x2 - x1) / 4); // narrow clips: smaller edges, so you can still move them
        if (x - x1 <= grab) return -1;
        if (x2 - x <= grab) return 1;
        return 0;
    }

    // ===================== Mouse =====================

    private enum DragMode { None, Move, TrimLeft, TrimRight, Pan }

    private DragMode mode = DragMode.None;
    private bool dragStarted;
    private Point dragStartPoint;
    private double panStartScroll;
    private Clip? pressedClip;
    private (Clip Clip, long Frame)? razorHover;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var tl = Timeline;
        if (tl is null) return;
        Focus();

        Point p = e.GetPosition(this);
        Clip? clip = ClipAt(p);
        bool clipUsable = clip is not null && !tl.IsLocked(clip);
        dragStartPoint = p;
        dragStarted = false;

        switch (tl.ActiveTool)
        {
            case "Hand":
                mode = DragMode.Pan;
                panStartScroll = tl.ScrollX;
                CaptureMouse();
                return;

            case "Zoom":
                bool zoomOut = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                tl.ZoomAt(p.X, zoomOut ? 1 / 1.5 : 1.5);
                return;

            case "Text":
                // Click on a track to put a new title there (titles go on video tracks).
                Track? textTrack = TrackAt(p.Y);
                if (textTrack is not null)
                {
                    tl.AddMedia(MediaItem.Title, textTrack, tl.Snap(tl.FrameAt(p.X), [], out _));
                    tl.ActiveTool = "Select"; // ready to move or trim the new title
                }
                return;

            case "Razor":
                if (clipUsable)
                {
                    long frame = tl.Snap(tl.FrameAt(p.X), [], out _);
                    tl.SplitAt(clip!, frame);
                }
                return;

            default: // Select and Ripple
                if (!clipUsable)
                {
                    tl.ClearSelection();
                    return;
                }
                bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                tl.Select(clip!, add);
                if (add) return;

                pressedClip = clip;
                int edge = EdgeAt(clip!, p.X);
                mode = edge < 0 ? DragMode.TrimLeft : edge > 0 ? DragMode.TrimRight : DragMode.Move;
                CaptureMouse();
                return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tl = Timeline;
        if (tl is null) return;
        Point p = e.GetPosition(this);

        if (mode == DragMode.None)
        {
            UpdateCursor(p);
            if (tl.ActiveTool == "Razor") UpdateRazorHover(p);
            return;
        }

        if (!dragStarted)
        {
            // Only start dragging after the mouse moved a few pixels (so a click stays a click).
            if (Math.Abs(p.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            dragStarted = true;

            switch (mode)
            {
                case DragMode.Move: tl.BeginMove(pressedClip!, tl.FrameAt(dragStartPoint.X)); break;
                case DragMode.TrimLeft: tl.BeginTrim(pressedClip!, leftEdge: true, ripple: tl.ActiveTool == "Ripple"); break;
                case DragMode.TrimRight: tl.BeginTrim(pressedClip!, leftEdge: false, ripple: tl.ActiveTool == "Ripple"); break;
            }
        }

        switch (mode)
        {
            case DragMode.Pan: tl.ScrollX = Math.Clamp(panStartScroll - (p.X - dragStartPoint.X), 0, tl.ScrollMaximum); break;
            case DragMode.Move: tl.UpdateMove(tl.FrameAt(p.X), TrackAt(p.Y)); break;
            case DragMode.TrimLeft:
            case DragMode.TrimRight: tl.UpdateTrim(tl.FrameAt(p.X)); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        FinishDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag();
    }

    private void FinishDrag()
    {
        var tl = Timeline;
        DragMode finished = mode;
        bool started = dragStarted;
        mode = DragMode.None;
        dragStarted = false;
        pressedClip = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (tl is null || !started) return;

        if (finished == DragMode.Move) tl.EndMove();
        else if (finished is DragMode.TrimLeft or DragMode.TrimRight) tl.EndTrim();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (razorHover is not null) { razorHover = null; InvalidateVisual(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var tl = Timeline;
        if (tl is null) return;
        // Ctrl + wheel zooms around the mouse; the wheel alone scrolls left and right.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            tl.ZoomAt(e.GetPosition(this).X, e.Delta > 0 ? 1.25 : 0.8);
        else
            tl.ScrollBy(-e.Delta);
        e.Handled = true;
    }

    private void UpdateCursor(Point p)
    {
        var tl = Timeline;
        if (tl is null) return;
        Cursor = tl.ActiveTool switch
        {
            "Hand" => Cursors.Hand,
            "Zoom" => Cursors.Cross,
            "Razor" => Cursors.Cross,
            "Text" => Cursors.IBeam,
            _ => ClipAt(p) is Clip c && !tl.IsLocked(c) && EdgeAt(c, p.X) != 0 ? Cursors.SizeWE : Cursors.Arrow,
        };
    }

    private void UpdateRazorHover(Point p)
    {
        var tl = Timeline!;
        Clip? clip = ClipAt(p);
        razorHover = clip is not null && !tl.IsLocked(clip) ? (clip, tl.Snap(tl.FrameAt(p.X), [], out _)) : null;
        InvalidateVisual();
    }

    // ===================== Dropping media from the bin =====================

    private sealed record DropGhost(MediaItem Media, Track Track, long Start, long Length);
    private DropGhost? dropGhost;

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var tl = Timeline;
        if (tl is null || e.Data.GetData(MediaDragFormat) is not MediaItem media)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        Point p = e.GetPosition(this);
        Track track = TrackAt(p.Y) ?? tl.Tracks[^1];
        // Put it on a track of the right kind.
        if (media.Kind == MediaKind.Audio && track.Kind != TrackKind.Audio) track = tl.Project.TrackByName("A1");
        if (media.Kind != MediaKind.Audio && track.Kind != TrackKind.Video)
            track = tl.Project.TrackByName(media.Kind == MediaKind.Text ? "V2" : "V1");

        long length = media.Kind is MediaKind.Image or MediaKind.Text
            ? (long)Math.Round(Services.MediaImporter.DefaultImageSeconds * tl.Fps)
            : media.LengthInFrames(tl.Fps);
        long start = tl.Snap(tl.FrameAt(p.X), [], out _);

        dropGhost = new DropGhost(media, track, start, length);
        e.Effects = track.IsLocked ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        dropGhost = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var ghost = dropGhost;
        dropGhost = null;
        if (ghost is not null && Timeline is { } tl)
        {
            tl.AddMedia(ghost.Media, ghost.Track, ghost.Start);
            Focus();
        }
        InvalidateVisual();
        e.Handled = true;
    }
}
