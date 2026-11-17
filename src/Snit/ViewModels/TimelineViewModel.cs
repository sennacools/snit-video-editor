using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>
/// The timeline: tracks, clips, the playhead, zoom, selection, and every edit
/// (adding, moving, trimming, cutting, deleting) with undo and redo.
/// The TimelineCanvas control draws it and turns mouse actions into calls here.
/// </summary>
public partial class TimelineViewModel : ObservableObject
{
    public TimelineViewModel(Project project) => Load(project);

    public Project Project { get; private set; } = null!;
    public IReadOnlyList<Track> Tracks => Project.Tracks;
    public double Fps => Project.Fps;

    /// <summary>Clips were added, moved, cut... The preview needs updating and the project needs saving.</summary>
    public event Action? ContentChanged;

    /// <summary>Only the look changed (selection, snap line). Just redraw.</summary>
    public event Action? RedrawNeeded;

    public void RequestRedraw() => RedrawNeeded?.Invoke();

    public void Load(Project project)
    {
        if (Project is not null)
            foreach (var t in Project.Tracks) t.PropertyChanged -= OnTrackChanged;

        Project = project;
        foreach (var t in project.Tracks) t.PropertyChanged += OnTrackChanged;

        Selection.Clear();
        undoStack.Clear();
        redoStack.Clear();
        PlayheadFrame = 0;
        ScrollX = 0;
        OnPropertyChanged(string.Empty); // refresh every binding
        RefreshCommands();
        RedrawNeeded?.Invoke();
    }

    private void OnTrackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Hiding, muting or locking a track: the preview and the saved file change.
        if (sender is Track t && e.PropertyName == nameof(Track.IsLocked) && t.IsLocked)
            Selection.RemoveWhere(c => t.Clips.Contains(c));
        NotifyContentChanged();
    }

    // ===================== Playhead and duration =====================

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayheadSeconds), nameof(CurrentTimecode))]
    private long playheadFrame;

    /// <summary>The playhead in seconds (the ruler uses this). Always lands on a whole frame.</summary>
    public double PlayheadSeconds
    {
        get => PlayheadFrame / Fps;
        set => PlayheadFrame = Math.Max(0, (long)Math.Round(value * Fps));
    }

    public string CurrentTimecode => Timecode.FromFrames(PlayheadFrame, Fps);
    public long DurationFrames => Project.DurationFrames;
    public string DurationTimecode => Timecode.FromFrames(DurationFrames, Fps);

    partial void OnPlayheadFrameChanged(long value) => RedrawNeeded?.Invoke();

    // ===================== Zoom and scrolling =====================

    public const double MinPixelsPerSecond = 2;
    public const double MaxPixelsPerSecond = 400;

    /// <summary>Zoom: how many pixels one second takes up.</summary>
    [ObservableProperty]
    private double pixelsPerSecond = 60;

    /// <summary>How far the timeline is scrolled to the right, in pixels.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScrollMaximum))]
    private double scrollX;

    /// <summary>Width of the visible part of the timeline (set by the canvas).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScrollMaximum))]
    private double viewportWidth;

    /// <summary>The whole timeline plus 30 seconds of room to drop clips after the end.</summary>
    public double ContentWidth => (DurationFrames / Fps + 30) * PixelsPerSecond;
    public double ScrollMaximum => Math.Max(0, ContentWidth - ViewportWidth);

    partial void OnPixelsPerSecondChanging(double oldValue, double newValue)
    {
        // Keep the playhead in the same spot on screen while zooming.
        double playheadOnScreen = PlayheadSeconds * oldValue - ScrollX;
        pendingScroll = PlayheadSeconds * newValue - playheadOnScreen;
    }

    private double pendingScroll;

    partial void OnPixelsPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(ScrollMaximum));
        ScrollX = Math.Clamp(pendingScroll, 0, ScrollMaximum);
        RedrawNeeded?.Invoke();
    }

    partial void OnScrollXChanged(double value) => RedrawNeeded?.Invoke();

    [RelayCommand]
    private void ZoomIn() => PixelsPerSecond = Math.Min(MaxPixelsPerSecond, PixelsPerSecond * 1.25);

    [RelayCommand]
    private void ZoomOut() => PixelsPerSecond = Math.Max(MinPixelsPerSecond, PixelsPerSecond / 1.25);

    /// <summary>Zoom so the point under the mouse stays where it is.</summary>
    public void ZoomAt(double x, double factor)
    {
        double secondsAtMouse = (x + ScrollX) / PixelsPerSecond;
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
        ScrollX = Math.Clamp(secondsAtMouse * PixelsPerSecond - x, 0, ScrollMaximum);
    }

    public void ScrollBy(double pixels) => ScrollX = Math.Clamp(ScrollX + pixels, 0, ScrollMaximum);

    /// <summary>During playback: turn the page when the playhead runs off the right edge.</summary>
    public void KeepPlayheadVisible()
    {
        double x = FrameToX(PlayheadFrame);
        if (x > ViewportWidth - 20 || x < 0)
            ScrollX = Math.Clamp(PlayheadSeconds * PixelsPerSecond - 40, 0, Math.Max(ScrollMaximum, PlayheadSeconds * PixelsPerSecond));
    }

    // Converting between frames and screen positions.
    public double FrameToX(long frame) => frame / Fps * PixelsPerSecond - ScrollX;
    public double ExactFrameAt(double x) => (x + ScrollX) / PixelsPerSecond * Fps;
    public long FrameAt(double x) => Math.Max(0, (long)Math.Round(ExactFrameAt(x)));

    // ===================== Tools and snapping =====================

    /// <summary>"Select", "Razor", "Ripple", "Text", "Hand" or "Zoom".</summary>
    [ObservableProperty]
    private string activeTool = "Select";

    [ObservableProperty]
    private bool snappingEnabled = true;

    /// <summary>The frame where a snap happened (drawn as a line), or null.</summary>
    public long? SnapLineFrame { get; private set; }

    private const double SnapDistancePixels = 8;

    /// <summary>
    /// If snapping is on and <paramref name="frame"/> is close to the playhead or a clip edge,
    /// returns that edge. <paramref name="distance"/> says how far it moved (for comparing options).
    /// </summary>
    public long Snap(long frame, ICollection<Clip> ignore, out long distance)
    {
        distance = long.MaxValue;
        if (!SnappingEnabled) return frame;

        long maxDistance = (long)Math.Ceiling(SnapDistancePixels / PixelsPerSecond * Fps);
        var targets = Project.AllClips.Where(c => !ignore.Contains(c))
            .SelectMany(c => new[] { c.Start, c.End })
            .Append(0)
            .Append(PlayheadFrame);

        long best = frame;
        foreach (long t in targets)
        {
            long d = Math.Abs(t - frame);
            if (d <= maxDistance && d < distance)
            {
                distance = d;
                best = t;
            }
        }
        return best;
    }

    private void ShowSnapLine(long? frame) => SnapLineFrame = frame;

    // ===================== Selection =====================

    public HashSet<Clip> Selection { get; } = [];

    public Clip? SelectedClip => Selection.FirstOrDefault();

    public string SelectedClipName => SelectedClip?.Name ?? "No clip selected";

    public bool IsLocked(Clip clip) => Project.TrackOf(clip).IsLocked;

    /// <summary>Select a clip (and its linked partner). With add = true, toggles it in the selection.</summary>
    public void Select(Clip clip, bool add)
    {
        var group = Project.LinkedGroup(clip).Where(c => !IsLocked(c)).ToList();
        if (add)
        {
            if (Selection.Contains(clip)) Selection.ExceptWith(group);
            else Selection.UnionWith(group);
        }
        else if (!Selection.Contains(clip))
        {
            Selection.Clear();
            Selection.UnionWith(group);
        }
        SelectionChanged();
    }

    public void ClearSelection()
    {
        if (Selection.Count == 0) return;
        Selection.Clear();
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedClip));
        OnPropertyChanged(nameof(SelectedClipName));
        RefreshCommands();
        RedrawNeeded?.Invoke();
    }

    // ===================== Undo / redo =====================
    // Before each edit we save a copy of every track's clips. Undo puts the copy back.

    private sealed record Snapshot(List<List<Clip>> Tracks, HashSet<Guid> Selected);

    private readonly Stack<Snapshot> undoStack = new();
    private readonly Stack<Snapshot> redoStack = new();

    private Snapshot TakeSnapshot() => new(
        Project.Tracks.Select(t => t.Clips.Select(c => c.Copy()).ToList()).ToList(),
        Selection.Select(c => c.Id).ToHashSet());

    private void RestoreSnapshot(Snapshot s)
    {
        for (int i = 0; i < Project.Tracks.Count; i++)
            Project.Tracks[i].Clips = s.Tracks[i].Select(c => c.Copy()).ToList();
        Selection.Clear();
        Selection.UnionWith(Project.AllClips.Where(c => s.Selected.Contains(c.Id)));
        SelectionChanged();
        NotifyContentChanged();
    }

    /// <summary>Call this right before changing clips.</summary>
    public void SaveUndoPoint()
    {
        undoStack.Push(TakeSnapshot());
        redoStack.Clear();
        RefreshCommands();
    }

    /// <summary>A drag that ended up changing nothing: forget its undo point.</summary>
    private void DropUndoPoint()
    {
        if (undoStack.Count > 0) undoStack.Pop();
        RefreshCommands();
    }

    private bool CanUndo() => undoStack.Count > 0;
    private bool CanRedo() => redoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (undoStack.Count == 0) return;
        redoStack.Push(TakeSnapshot());
        RestoreSnapshot(undoStack.Pop());
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (redoStack.Count == 0) return;
        undoStack.Push(TakeSnapshot());
        RestoreSnapshot(redoStack.Pop());
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        RippleDeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    public void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(DurationFrames));
        OnPropertyChanged(nameof(DurationTimecode));
        OnPropertyChanged(nameof(ScrollMaximum));
        ContentChanged?.Invoke();
        RedrawNeeded?.Invoke();
    }

    // ===================== Adding clips =====================

    /// <summary>
    /// Puts a media file on the timeline at <paramref name="start"/>. Videos with sound get a
    /// linked audio clip on the matching audio track (V1 → A1, V2 → A2). Anything already
    /// there is overwritten.
    /// </summary>
    public void AddMedia(MediaItem media, Track track, long start)
    {
        // Pictures and video go on video tracks, sound on audio tracks.
        if (media.Kind == MediaKind.Audio && track.Kind != TrackKind.Audio) track = Project.TrackByName("A1");
        if (media.Kind != MediaKind.Audio && track.Kind != TrackKind.Video) track = Project.TrackByName("V1");
        if (track.IsLocked) return;

        long length = media.Kind == MediaKind.Image
            ? (long)Math.Round(MediaImporter.DefaultImageSeconds * Fps)
            : media.LengthInFrames(Fps);

        SaveUndoPoint();
        Guid? link = media.Kind == MediaKind.Video && media.HasAudio ? Guid.NewGuid() : null;
        var placed = new List<Clip>();

        var clip = new Clip { Media = media, Start = start, In = 0, Length = length, LinkId = link };
        TimelineEdits.ClearRange(track, clip.Start, clip.End, []);
        track.Clips.Add(clip);
        placed.Add(clip);

        if (link is not null)
        {
            Track audioTrack = Project.PartnerAudioTrack(track);
            if (!audioTrack.IsLocked)
            {
                var sound = clip.Copy();
                sound.Id = Guid.NewGuid();
                TimelineEdits.ClearRange(audioTrack, sound.Start, sound.End, []);
                audioTrack.Clips.Add(sound);
                placed.Add(sound);
            }
            else
            {
                clip.LinkId = null;
            }
        }

        Selection.Clear();
        Selection.UnionWith(placed);
        SelectionChanged();
        NotifyContentChanged();
    }

    /// <summary>Removes every clip that uses this media file (when it's removed from the bin).</summary>
    public void RemoveClipsUsing(MediaItem media)
    {
        if (!Project.AllClips.Any(c => c.Media == media)) return;
        SaveUndoPoint();
        foreach (var t in Project.Tracks) t.Clips.RemoveAll(c => c.Media == media);
        Selection.RemoveWhere(c => c.Media == media);
        SelectionChanged();
        NotifyContentChanged();
    }

    // ===================== Cutting and deleting =====================

    /// <summary>Razor: cut a clip (and its linked partner) in two at a frame.</summary>
    public void SplitAt(Clip clip, long frame)
    {
        var group = Project.LinkedGroup(clip).Where(c => !IsLocked(c) && c.Start < frame && frame < c.End).ToList();
        if (group.Count == 0) return;

        SaveUndoPoint();
        Guid? newLink = group.Count > 1 ? Guid.NewGuid() : null;
        foreach (var c in group)
        {
            Clip? right = TimelineEdits.Split(Project.TrackOf(c), c, frame);
            if (right is not null) right.LinkId = newLink ?? (group.Count > 1 ? right.LinkId : null);
        }
        NotifyContentChanged();
    }

    /// <summary>Cut every clip under the playhead (selected clips only, if any are selected).</summary>
    [RelayCommand]
    private void SplitAtPlayhead()
    {
        long frame = PlayheadFrame;
        var candidates = Project.Tracks.Where(t => !t.IsLocked)
            .SelectMany(t => t.Clips)
            .Where(c => c.Start < frame && frame < c.End)
            .Where(c => Selection.Count == 0 || Selection.Contains(c))
            .ToList();
        if (candidates.Count == 0) return;

        SaveUndoPoint();
        // Linked clips cut together stay linked as two new pairs.
        var newLinks = new Dictionary<Guid, Guid>();
        foreach (var c in candidates)
        {
            Clip? right = TimelineEdits.Split(Project.TrackOf(c), c, frame);
            if (right?.LinkId is Guid old)
            {
                if (!newLinks.TryGetValue(old, out Guid fresh)) newLinks[old] = fresh = Guid.NewGuid();
                right.LinkId = fresh;
            }
        }
        NotifyContentChanged();
    }

    private bool HasSelection() => Selection.Count > 0;

    /// <summary>Delete: remove the selected clips and leave a gap.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelection()
    {
        if (Selection.Count == 0) return;
        SaveUndoPoint();
        foreach (var t in Project.Tracks) t.Clips.RemoveAll(Selection.Contains);
        Selection.Clear();
        SelectionChanged();
        NotifyContentChanged();
    }

    /// <summary>Ripple delete: remove the selected clips and slide everything after them left to close the gap.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RippleDeleteSelection()
    {
        if (Selection.Count == 0) return;
        SaveUndoPoint();
        foreach (var track in Project.Tracks)
        {
            // Work from right to left so earlier shifts don't disturb later ones.
            foreach (var removed in track.Clips.Where(Selection.Contains).OrderByDescending(c => c.Start).ToList())
            {
                track.Clips.Remove(removed);
                foreach (var later in track.Clips.Where(c => c.Start >= removed.End))
                    later.Start -= removed.Length;
            }
        }
        Selection.Clear();
        SelectionChanged();
        NotifyContentChanged();
    }

    // ===================== Moving clips (drag) =====================

    private sealed record Original(Clip Clip, long Start, long In, long Length, Track Track);

    private List<Original> dragOriginals = [];
    private List<Original> rippleOriginals = [];
    private Clip? dragAnchor;
    private long dragGrabFrame;

    /// <summary>Start dragging the selected clips; <paramref name="anchor"/> is the one under the mouse.</summary>
    public void BeginMove(Clip anchor, long grabFrame)
    {
        SaveUndoPoint();
        dragAnchor = anchor;
        dragGrabFrame = grabFrame;
        dragOriginals = Selection.Select(c => new Original(c, c.Start, c.In, c.Length, Project.TrackOf(c))).ToList();
    }

    public void UpdateMove(long mouseFrame, Track? trackUnderMouse)
    {
        if (dragAnchor is null) return;
        var moving = dragOriginals.Select(o => o.Clip).ToHashSet();

        long delta = mouseFrame - dragGrabFrame;
        delta = Math.Max(delta, -dragOriginals.Min(o => o.Start)); // never before 0

        // Snap whichever edge (start or end of any moving clip) is closest to something.
        long? snapLine = null;
        if (SnappingEnabled)
        {
            long bestDistance = long.MaxValue, bestDelta = delta;
            foreach (var o in dragOriginals)
            {
                foreach (long edge in new[] { o.Start + delta, o.Start + o.Length + delta })
                {
                    long snapped = Snap(edge, moving, out long distance);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestDelta = delta + (snapped - edge);
                        snapLine = snapped;
                    }
                }
            }
            if (bestDistance != long.MaxValue && bestDelta >= -dragOriginals.Min(o => o.Start))
                delta = bestDelta;
            else
                snapLine = null;
        }
        ShowSnapLine(snapLine);

        // Moving up or down: only between tracks of the same kind (video ↔ video, audio ↔ audio).
        var anchorOriginal = dragOriginals.First(o => o.Clip == dragAnchor);
        var sameKind = Project.Tracks.Where(t => t.Kind == anchorOriginal.Track.Kind).ToList();
        int trackShift = 0;
        if (trackUnderMouse is not null && trackUnderMouse.Kind == anchorOriginal.Track.Kind)
            trackShift = sameKind.IndexOf(trackUnderMouse) - sameKind.IndexOf(anchorOriginal.Track);

        foreach (var o in dragOriginals)
        {
            o.Clip.Start = o.Start + delta;

            Track target = o.Track;
            if (o.Track.Kind == anchorOriginal.Track.Kind && trackShift != 0)
            {
                int index = sameKind.IndexOf(o.Track) + trackShift;
                if (index >= 0 && index < sameKind.Count && !sameKind[index].IsLocked)
                    target = sameKind[index];
            }
            Track current = Project.TrackOf(o.Clip);
            if (current != target)
            {
                current.Clips.Remove(o.Clip);
                target.Clips.Add(o.Clip);
            }
        }
        RedrawNeeded?.Invoke();
    }

    public void EndMove()
    {
        ShowSnapLine(null);
        var moving = dragOriginals.Select(o => o.Clip).ToHashSet();
        bool changed = dragOriginals.Any(o => o.Clip.Start != o.Start || Project.TrackOf(o.Clip) != o.Track);

        if (changed)
        {
            // Dropped on top of other clips: those get cut away (overwrite).
            foreach (var clip in moving)
                TimelineEdits.ClearRange(Project.TrackOf(clip), clip.Start, clip.End, moving);
            NotifyContentChanged();
        }
        else
        {
            DropUndoPoint();
            RedrawNeeded?.Invoke();
        }
        dragOriginals = [];
        dragAnchor = null;
    }

    // ===================== Trimming clip edges (drag) =====================

    private bool trimLeft;
    private bool trimRipple;

    /// <summary>Start dragging the left or right edge of a clip. Ripple: later clips slide along.</summary>
    public void BeginTrim(Clip clip, bool leftEdge, bool ripple)
    {
        SaveUndoPoint();
        trimLeft = leftEdge;
        trimRipple = ripple;
        dragAnchor = clip;

        var group = Project.LinkedGroup(clip).Where(c => !IsLocked(c)).ToList();
        dragOriginals = group.Select(c => new Original(c, c.Start, c.In, c.Length, Project.TrackOf(c))).ToList();

        // For a ripple trim, remember everything after the trimmed clip on the same tracks.
        rippleOriginals = [];
        if (ripple)
        {
            foreach (var o in dragOriginals)
                rippleOriginals.AddRange(o.Track.Clips
                    .Where(c => !group.Contains(c) && c.Start >= o.Start + o.Length)
                    .Select(c => new Original(c, c.Start, c.In, c.Length, o.Track)));
        }
    }

    public void UpdateTrim(long mouseFrame)
    {
        if (dragAnchor is null) return;
        var anchor = dragOriginals.First(o => o.Clip == dragAnchor);
        var ignore = dragOriginals.Select(o => o.Clip).Concat(rippleOriginals.Select(o => o.Clip)).ToHashSet();

        long snapped = Snap(mouseFrame, ignore, out long distance);
        ShowSnapLine(distance != long.MaxValue ? snapped : null);

        // How far the edge moves, kept within what the source file allows.
        long edge = trimLeft ? anchor.Start : anchor.Start + anchor.Length;
        long delta = snapped - edge;
        foreach (var o in dragOriginals)
        {
            long mediaLength = o.Clip.Media.LengthInFrames(Fps);
            if (trimLeft)
            {
                // Left edge: can go back until the start of the source file (and frame 0),
                // and forward until the clip is one frame long.
                long min = -Math.Min(o.In, trimRipple ? o.In : o.Start);
                delta = Math.Clamp(delta, min, o.Length - 1);
            }
            else
            {
                // Right edge: can go forward until the end of the source file.
                long max = mediaLength == long.MaxValue ? long.MaxValue / 4 : mediaLength - (o.In + o.Length);
                delta = Math.Clamp(delta, -(o.Length - 1), max);
            }
        }

        foreach (var o in dragOriginals)
        {
            if (trimLeft)
            {
                o.Clip.In = o.In + delta;
                o.Clip.Length = o.Length - delta;
                // Normal trim: the clip's start moves. Ripple trim: the start stays, everything after slides.
                o.Clip.Start = trimRipple ? o.Start : o.Start + delta;
            }
            else
            {
                o.Clip.Length = o.Length + delta;
            }
        }

        if (trimRipple)
        {
            long shift = trimLeft ? -delta : delta;
            foreach (var r in rippleOriginals) r.Clip.Start = r.Start + shift;
        }
        RedrawNeeded?.Invoke();
    }

    public void EndTrim()
    {
        ShowSnapLine(null);
        bool changed = dragOriginals.Any(o => o.Clip.Start != o.Start || o.Clip.Length != o.Length);
        if (changed)
        {
            if (!trimRipple)
            {
                var trimmed = dragOriginals.Select(o => o.Clip).ToHashSet();
                foreach (var clip in trimmed)
                    TimelineEdits.ClearRange(Project.TrackOf(clip), clip.Start, clip.End, trimmed);
            }
            NotifyContentChanged();
        }
        else
        {
            DropUndoPoint();
            RedrawNeeded?.Invoke();
        }
        dragOriginals = [];
        rippleOriginals = [];
        dragAnchor = null;
    }
}
