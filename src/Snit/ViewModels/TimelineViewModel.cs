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
    public System.Collections.ObjectModel.ObservableCollection<Track> Tracks => Project.Tracks;

    /// <summary>The sound tracks, A1 first (for the audio mixer).</summary>
    public IReadOnlyList<Track> AudioTracks => Project.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
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

        if (Project is not null) Project.Tracks.CollectionChanged -= OnTracksChanged;
        Project = project;
        foreach (var t in project.Tracks) t.PropertyChanged += OnTrackChanged;
        project.Tracks.CollectionChanged += OnTracksChanged;
        OnPropertyChanged(nameof(AudioTracks));

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

    // ===================== Adding and removing tracks =====================

    [RelayCommand]
    private void AddVideoTrack() => AddTrack(TrackKind.Video);

    [RelayCommand]
    private void AddAudioTrack() => AddTrack(TrackKind.Audio);

    private void AddTrack(TrackKind kind)
    {
        SaveUndoPoint();
        Track track = Project.AddTrack(kind);
        track.PropertyChanged += OnTrackChanged;
        NotifyContentChanged();
    }

    /// <summary>Removes a track and its clips (after asking, if it has clips). Undo brings it back.</summary>
    [RelayCommand]
    private void RemoveTrack(Track? track)
    {
        if (track is null) return;
        int sameKind = track.Kind == TrackKind.Video ? Project.VideoTrackCount : Project.AudioTrackCount;
        if (track.Kind != TrackKind.Caption && sameKind <= 1) return; // always keep one video and one sound track
        SaveUndoPoint();
        Selection.RemoveWhere(track.Clips.Contains);
        track.PropertyChanged -= OnTrackChanged;
        Project.RemoveTrack(track);
        SelectionChanged();
        NotifyContentChanged();
    }

    // ===================== Jumping between cuts =====================

    /// <summary>Every place where a clip starts or ends, plus 0.</summary>
    private IEnumerable<long> EditPoints => Project.AllClips.SelectMany(c => new[] { c.Start, c.End }).Append(0).Distinct();

    /// <summary>Up arrow: to the previous cut.</summary>
    public void GoToPreviousEdit()
        => PlayheadFrame = EditPoints.Where(f => f < PlayheadFrame).DefaultIfEmpty(0).Max();

    /// <summary>Down arrow: to the next cut.</summary>
    public void GoToNextEdit()
        => PlayheadFrame = EditPoints.Where(f => f > PlayheadFrame).DefaultIfEmpty(PlayheadFrame).Min();

    // ===================== Markers =====================

    public IReadOnlyList<Marker> Markers => Project.Markers;

    /// <summary>M key: a marker at the playhead (named "Marker 1", "Marker 2"…).</summary>
    [RelayCommand]
    private void AddMarker()
    {
        if (Project.Markers.Any(m => m.Frame == PlayheadFrame)) return;
        SaveUndoPoint();
        Project.Markers = [.. Project.Markers, new Marker(PlayheadFrame, $"Marker {Project.Markers.Count + 1}")];
        Project.Markers.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        MarkersChanged();
    }

    /// <summary>The marker within a few pixels of a spot on the ruler, or null.</summary>
    public Marker? MarkerNear(double x) => Project.Markers
        .Where(m => Math.Abs(FrameToX(m.Frame) - x) <= 6)
        .OrderBy(m => Math.Abs(FrameToX(m.Frame) - x))
        .FirstOrDefault();

    public void RenameMarker(Marker marker, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Project.Markers.Contains(marker)) return;
        SaveUndoPoint();
        Project.Markers = Project.Markers.Select(m => m == marker ? m with { Name = name.Trim() } : m).ToList();
        MarkersChanged();
    }

    public void DeleteMarker(Marker marker)
    {
        if (!Project.Markers.Contains(marker)) return;
        SaveUndoPoint();
        Project.Markers = Project.Markers.Where(m => m != marker).ToList();
        MarkersChanged();
    }

    /// <summary>Shift+M: to the next marker.</summary>
    public void GoToNextMarker()
    {
        if (Project.Markers.FirstOrDefault(m => m.Frame > PlayheadFrame) is Marker m) PlayheadFrame = m.Frame;
    }

    /// <summary>Ctrl+Shift+M: to the previous marker.</summary>
    public void GoToPreviousMarker()
    {
        if (Project.Markers.LastOrDefault(m => m.Frame < PlayheadFrame) is Marker m) PlayheadFrame = m.Frame;
    }

    private void MarkersChanged()
    {
        OnPropertyChanged(nameof(Markers));
        NotifyContentChanged(); // saved with the project
    }

    // ===================== In and out points =====================

    public long? InPoint => Project.InPoint;
    public long? OutPoint => Project.OutPoint;

    /// <summary>True when both marks are set and the out mark is after the in mark.</summary>
    public bool HasInOutRange => InPoint is long i && OutPoint is long o && o > i;

    /// <summary>For the ruler: the marks in seconds, or NaN when not set.</summary>
    public double InSeconds => InPoint is long i ? i / Fps : double.NaN;
    public double OutSeconds => OutPoint is long o ? o / Fps : double.NaN;

    /// <summary>I key: put the in mark at the playhead (the out mark moves along if it was before it).</summary>
    public void SetInPoint()
    {
        Project.InPoint = PlayheadFrame;
        if (Project.OutPoint <= PlayheadFrame) Project.OutPoint = null;
        InOutChanged();
    }

    /// <summary>O key: put the out mark at the playhead.</summary>
    public void SetOutPoint()
    {
        Project.OutPoint = PlayheadFrame;
        if (Project.InPoint >= PlayheadFrame) Project.InPoint = null;
        InOutChanged();
    }

    /// <summary>Ctrl+Shift+X: remove both marks.</summary>
    public void ClearInOut()
    {
        Project.InPoint = Project.OutPoint = null;
        InOutChanged();
    }

    private void InOutChanged()
    {
        OnPropertyChanged(nameof(InPoint));
        OnPropertyChanged(nameof(OutPoint));
        OnPropertyChanged(nameof(HasInOutRange));
        OnPropertyChanged(nameof(InSeconds));
        OnPropertyChanged(nameof(OutSeconds));
        NotifyContentChanged(); // saved with the project
    }

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
            .Append(PlayheadFrame)
            .Concat(Project.Markers.Select(m => m.Frame));

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

    private sealed record TrackState(Track Track, List<Clip> Clips);
    private sealed record Snapshot(List<TrackState> Tracks, HashSet<Guid> Selected, List<Marker> Markers);

    private readonly Stack<Snapshot> undoStack = new();
    private readonly Stack<Snapshot> redoStack = new();

    private Snapshot TakeSnapshot() => new(
        Project.Tracks.Select(t => new TrackState(t, t.Clips.Select(c => c.Copy()).ToList())).ToList(),
        Selection.Select(c => c.Id).ToHashSet(),
        Project.Markers.ToList());

    private void RestoreSnapshot(Snapshot s)
    {
        // Put back the same tracks in the same order (tracks may have been added or removed).
        if (!Project.Tracks.SequenceEqual(s.Tracks.Select(t => t.Track)))
        {
            foreach (var t in Project.Tracks) t.PropertyChanged -= OnTrackChanged;
            Project.Tracks.Clear();
            foreach (var t in s.Tracks)
            {
                Project.Tracks.Add(t.Track);
                t.Track.PropertyChanged += OnTrackChanged;
            }
            Project.RenumberTracks();
        }
        foreach (var t in s.Tracks)
            t.Track.Clips = t.Clips.Select(c => c.Copy()).ToList();
        Project.Markers = s.Markers.ToList();
        OnPropertyChanged(nameof(Markers));
        Selection.Clear();
        Selection.UnionWith(Project.AllClips.Where(c => s.Selected.Contains(c.Id)));
        SelectionChanged();
        NotifyContentChanged();
    }

    /// <summary>Goes up by one with every new undo step, undo and redo. The inspector uses it
    /// to make one whole slider drag a single undo step.</summary>
    public int EditVersion { get; private set; }

    /// <summary>Call this right before changing clips.</summary>
    public void SaveUndoPoint()
    {
        undoStack.Push(TakeSnapshot());
        redoStack.Clear();
        EditVersion++;
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
        EditVersion++;
        redoStack.Push(TakeSnapshot());
        RestoreSnapshot(undoStack.Pop());
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (redoStack.Count == 0) return;
        EditVersion++;
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
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
    }

    public void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(DurationFrames));
        OnPropertyChanged(nameof(DurationTimecode));
        OnPropertyChanged(nameof(ScrollMaximum));
        ContentChanged?.Invoke();
        RedrawNeeded?.Invoke();
    }

    // ===================== Nested sequences =====================

    /// <summary>A sequence can't go inside itself (or inside a sequence that is inside it), and an empty one has nothing to show.</summary>
    public bool CanPlace(MediaItem media)
        => !media.IsSequence
           || (Project.SequenceOf(media) is Sequence inner && inner.DurationFrames > 0
               && !NestedSequences.WouldLoop(Project, Project.ActiveSequence, inner));

    /// <summary>
    /// Clip → Nest: moves the selected clips into a new sequence and puts that sequence in their
    /// place as one clip (with its sound linked, on the matching audio track). Returns the new sequence.
    /// </summary>
    public Sequence? NestSelection(string name)
    {
        var clips = Selection.SelectMany(Project.LinkedGroup).Distinct()
            .Where(c => !IsLocked(c)).ToList();
        if (clips.Count == 0) return null;

        SaveUndoPoint();
        long start = clips.Min(c => c.Start), end = clips.Max(c => c.End);
        var placement = clips.Select(c => (Clip: c, Track: Project.TrackOf(c))).ToList();

        var inner = new Sequence
        {
            Name = name,
            Settings = new ProjectSettings { Width = Project.Settings.Width, Height = Project.Settings.Height, FrameRate = Project.Settings.FrameRate },
        };
        Project.Sequences.Add(inner);
        Project.With(inner, () =>
        {
            foreach (var (clip, track) in placement)
            {
                Track target = track.Kind == TrackKind.Caption ? Captions.EnsureTrack(Project) : Project.EnsureTrack(track.Name);
                Clip copy = clip.Copy();
                copy.Start -= start;
                target.Clips.Add(copy);
            }
            return true;
        });
        foreach (var (clip, track) in placement) track.Clips.Remove(clip);

        MediaItem item = NestedSequences.EnsureMediaItem(Project, inner);
        var pictureTracks = placement.Where(p => p.Track.Kind != TrackKind.Audio).Select(p => p.Track).ToList();
        var soundTracks = placement.Where(p => p.Track.Kind == TrackKind.Audio).Select(p => p.Track).ToList();
        Track? videoTrack = pictureTracks.Count > 0
            ? pictureTracks.Where(t => t.Kind == TrackKind.Video).OrderByDescending(Project.Tracks.IndexOf).FirstOrDefault() ?? Project.FirstVideoTrack
            : null;
        Track? audioTrack = soundTracks.Count > 0
            ? (videoTrack is not null ? Project.PartnerAudioTrack(videoTrack) : soundTracks.OrderBy(Project.Tracks.IndexOf).First())
            : null;
        Guid? link = videoTrack is not null && audioTrack is not null ? Guid.NewGuid() : null;

        Selection.Clear();
        foreach (Track? track in new[] { videoTrack, audioTrack })
        {
            if (track is null) continue;
            var nested = new Clip { Media = item, Start = start, Length = end - start, LinkId = link };
            TimelineEdits.ClearRange(track, nested.Start, nested.End, []);
            track.Clips.Add(nested);
            Selection.Add(nested);
        }
        SelectionChanged();
        NotifyContentChanged();
        return inner;
    }

    // ===================== Adding clips =====================

    /// <summary>
    /// Puts a media file on the timeline at <paramref name="start"/>. Videos with sound get a
    /// linked audio clip on the matching audio track (V1 → A1, V2 → A2). Anything already
    /// there is overwritten.
    /// </summary>
    public void AddMedia(MediaItem media, Track track, long start)
    {
        long length = media.Kind is MediaKind.Image or MediaKind.Text
            ? (long)Math.Round(MediaImporter.DefaultImageSeconds * Fps)
            : media.LengthInFrames(Fps);
        AddMediaPart(media, track, start, 0, length, insert: false);
    }

    /// <summary>
    /// Source monitor ("," insert and "." overwrite): puts part of a file (from source frame
    /// <paramref name="sourceIn"/>, <paramref name="length"/> frames) at the playhead on V1/A1.
    /// Insert pushes everything after the playhead to the right; overwrite covers what's there.
    /// </summary>
    public void PlaceFromSource(MediaItem media, long sourceIn, long length, bool insert)
    {
        Track track = media.Kind == MediaKind.Audio ? Project.FirstAudioTrack : Project.FirstVideoTrack;
        AddMediaPart(media, track, PlayheadFrame, sourceIn, length, insert);
        PlayheadFrame += length; // like Premiere: ready for the next piece
    }

    private void AddMediaPart(MediaItem media, Track track, long start, long sourceIn, long length, bool insert)
    {
        // Pictures and video go on video tracks (titles on V2), sound on audio tracks.
        if (media.Kind == MediaKind.Audio && track.Kind != TrackKind.Audio) track = Project.FirstAudioTrack;
        if (media.Kind != MediaKind.Audio && track.Kind != TrackKind.Video)
            track = (media.Kind == MediaKind.Text ? Project.TitleTrack : Project.FirstVideoTrack);
        if (track.IsLocked || length <= 0) return;
        if (!CanPlace(media)) return;

        SaveUndoPoint();

        if (insert)
        {
            // Make room: cut every clip that crosses the playhead, then slide everything after it.
            foreach (Track t in Project.Tracks.Where(t => !t.IsLocked))
            {
                foreach (Clip c in t.Clips.Where(c => c.Start < start && c.End > start).ToList())
                    TimelineEdits.Split(t, c, start);
                foreach (Clip c in t.Clips.Where(c => c.Start >= start)) c.Start += length;
            }
            foreach (Marker m in Project.Markers.Where(m => m.Frame >= start).ToList())
                Project.Markers[Project.Markers.IndexOf(m)] = m with { Frame = m.Frame + length };
        }

        Guid? link = media.Kind == MediaKind.Video && media.HasAudio ? Guid.NewGuid() : null;
        var placed = new List<Clip>();

        var clip = new Clip
        {
            Media = media, Start = start, In = sourceIn, Length = length, LinkId = link,
            Title = media.Kind == MediaKind.Text ? (NextTitleStyle?.Copy() ?? new TitleStyle()) : null,
        };
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

    /// <summary>Adds a new title (5 seconds, on V2) at the playhead.</summary>
    [RelayCommand]
    private void AddTitleAtPlayhead() => AddMedia(MediaItem.Title, Project.TitleTrack, PlayheadFrame);

    // ===================== Captions =====================

    /// <summary>Adds a 3-second caption at the playhead on the C1 track (made when needed), selected for typing.</summary>
    [RelayCommand]
    private void AddCaptionAtPlayhead()
    {
        SaveUndoPoint();
        Track track = Captions.EnsureTrack(Project);
        if (track.IsLocked) return;
        long length = (long)Math.Round(3 * Fps);
        Clip clip = Captions.MakeClip(PlayheadFrame, length, "New caption");
        TimelineEdits.ClearRange(track, clip.Start, clip.End, []);
        track.Clips.Add(clip);
        Select(clip, add: false);
        NotifyContentChanged();
    }

    /// <summary>Reads an .srt file onto the captions track. Returns how many captions it had.</summary>
    public int ImportCaptions(string path)
    {
        var lines = Srt.Parse(System.IO.File.ReadAllText(path));
        if (lines.Count == 0) return 0;
        SaveUndoPoint();
        int count = Captions.Import(Project, lines);
        NotifyContentChanged();
        return count;
    }

    /// <summary>Writes the captions as an .srt file. Returns how many there were.</summary>
    public int ExportCaptions(string path)
    {
        var lines = Captions.Lines(Project);
        System.IO.File.WriteAllText(path, Srt.Write(lines));
        return lines.Count;
    }

    public bool HasCaptions => Project.CaptionTrack is { Clips.Count: > 0 };

    /// <summary>
    /// The look the next new title gets (set while a title template is dragged from the Text tab);
    /// null = the plain title.
    /// </summary>
    public TitleStyle? NextTitleStyle { get; set; }

    /// <summary>Adds a title with a template's look at the playhead (double-click in the Text tab).</summary>
    public void AddTitleAtPlayhead(TitleStyle style)
    {
        NextTitleStyle = style;
        try { AddTitleAtPlayhead(); }
        finally { NextTitleStyle = null; }
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

    // ===================== Copy, cut and paste =====================

    /// <summary>Copied clips, with the name of the track each came from.</summary>
    private List<(string TrackName, Clip Clip)> clipboard = [];

    public bool HasClipboard => clipboard.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        clipboard = Selection.Select(c => (Project.TrackOf(c).Name, c.Copy())).ToList();
        PasteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        Copy();
        DeleteSelection();
    }

    /// <summary>
    /// Puts the copied clips at the playhead, on the tracks they came from (or the first track of
    /// the right kind if that track is gone), keeping their spacing. Anything there is overwritten.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasClipboard))]
    private void Paste()
    {
        if (clipboard.Count == 0) return;
        SaveUndoPoint();
        long offset = PlayheadFrame - clipboard.Min(c => c.Clip.Start);
        var newLinks = new Dictionary<Guid, Guid>();
        var pasted = new List<Clip>();

        foreach (var (trackName, original) in clipboard)
        {
            Clip clip = original.Copy();
            clip.Id = Guid.NewGuid();
            clip.Start += offset;
            if (clip.LinkId is Guid old) // pasted pairs stay pairs, but separate from the originals
            {
                if (!newLinks.TryGetValue(old, out Guid fresh)) newLinks[old] = fresh = Guid.NewGuid();
                clip.LinkId = fresh;
            }

            TrackKind kind = trackName.StartsWith('A') ? TrackKind.Audio : TrackKind.Video;
            Track track = Project.Tracks.FirstOrDefault(t => t.Name == trackName)
                          ?? (kind == TrackKind.Audio ? Project.FirstAudioTrack : Project.FirstVideoTrack);
            if (track.IsLocked) continue;
            TimelineEdits.ClearRange(track, clip.Start, clip.End, pasted);
            track.Clips.Add(clip);
            pasted.Add(clip);
        }

        Selection.Clear();
        Selection.UnionWith(pasted);
        if (pasted.Count > 0) PlayheadFrame = pasted.Max(c => c.End); // like Premiere: ready to paste again after it
        SelectionChanged();
        NotifyContentChanged();
    }

    /// <summary>Box-select: selects these clips (and their linked partners), optionally added to the clips already selected.</summary>
    public void SelectClips(IEnumerable<Clip> clips, IReadOnlyCollection<Clip> keep)
    {
        Selection.Clear();
        Selection.UnionWith(keep);
        foreach (Clip c in clips)
            Selection.UnionWith(Project.LinkedGroup(c).Where(x => !IsLocked(x)));
        SelectionChanged();
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

    // ===================== Effects =====================

    /// <summary>Adds an effect to a clip on a video track (at the bottom of its effect list).</summary>
    public void AddEffect(Clip clip, string kind)
    {
        if (IsLocked(clip) || Project.TrackOf(clip).Kind != TrackKind.Video || EffectCatalog.Find(kind) is not EffectDefinition definition) return;
        SaveUndoPoint();
        clip.Effects = [.. clip.Effects, definition.Create()];
        Select(clip, add: false);
        NotifyContentChanged();
    }

    // ===================== Transitions =====================

    /// <summary>
    /// Puts a transition at the start or end of a clip (and its linked partner), or removes it
    /// (kind = null). At a cut, a transition on the end of the first clip is stored on the start
    /// of the next clip instead, so a cut only ever has one transition.
    /// </summary>
    public void SetTransition(Clip clip, bool atStart, TransitionKind? kind, long frames, bool saveUndo = true)
    {
        if (IsLocked(clip)) return;
        if (!atStart && NextClipAtCut(clip) is Clip next)
        {
            clip = next;
            atStart = true;
        }

        if (saveUndo) SaveUndoPoint();
        Transition? transition = kind is TransitionKind k ? new Transition(k, Math.Clamp(frames, 1, clip.Length)) : null;
        foreach (Clip c in Project.LinkedGroup(clip).Where(c => !IsLocked(c)))
        {
            if (atStart) c.TransitionIn = transition;
            else c.TransitionOut = transition;
        }
        NotifyContentChanged();
    }

    /// <summary>The clip that starts exactly where this one ends, on the same track (a cut), or null.</summary>
    public Clip? NextClipAtCut(Clip clip) => Project.TrackOf(clip).Clips.FirstOrDefault(c => c != clip && c.Start == clip.End);

    /// <summary>The clip that ends exactly where this one starts, on the same track, or null.</summary>
    public Clip? PreviousClipAtCut(Clip clip) => Project.TrackOf(clip).Clips.FirstOrDefault(c => c != clip && c.End == clip.Start);

    /// <summary>Ctrl+D: a cross-dissolve at the cut (on a video track) nearest to the playhead, within one second.</summary>
    [RelayCommand]
    private void AddDissolveAtPlayhead()
    {
        long reach = (long)Math.Round(Fps);
        Clip? best = Project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsLocked)
            .SelectMany(t => t.Clips)
            .Where(c => PreviousClipAtCut(c) is not null && Math.Abs(c.Start - PlayheadFrame) <= reach)
            .OrderBy(c => Math.Abs(c.Start - PlayheadFrame))
            .FirstOrDefault();
        if (best is not null) SetTransition(best, atStart: true, TransitionKind.CrossDissolve, TransitionPlan.DefaultFrames);
    }

    // ===================== Speed and reverse =====================

    private sealed record SpeedChange(int Version, Guid ClipId, List<(Clip Clip, long Length, double Speed)> Group, List<Original> Later);
    private SpeedChange? speedChange;

    public const double MinSpeedPercent = 10, MaxSpeedPercent = 400;

    /// <summary>Speed can be changed for video and sound, not for pictures and titles.</summary>
    public static bool CanChangeSpeed(Clip clip) => clip.Media.Kind is MediaKind.Video or MediaKind.Audio;

    /// <summary>
    /// Changes the speed of a clip and its linked partner. The clip keeps showing the same part
    /// of the file, so it gets shorter (faster) or longer (slower), and the clips after it on the
    /// same tracks slide along. Dragging the slider counts as one undo step.
    /// </summary>
    public void SetSpeed(Clip clip, double percent)
    {
        if (!CanChangeSpeed(clip) || IsLocked(clip)) return;
        percent = Math.Clamp(Math.Round(percent), MinSpeedPercent, MaxSpeedPercent);

        // A new change (not the next step of the same slider drag): remember how things were.
        if (speedChange is null || speedChange.ClipId != clip.Id || speedChange.Version != EditVersion)
        {
            SaveUndoPoint();
            var group = Project.LinkedGroup(clip).Where(c => !IsLocked(c) && CanChangeSpeed(c)).ToList();
            var later = new List<Original>();
            foreach (Clip c in group)
            {
                Track track = Project.TrackOf(c);
                later.AddRange(track.Clips.Where(o => !group.Contains(o) && o.Start >= c.End)
                    .Select(o => new Original(o, o.Start, o.In, o.Length, track)));
            }
            speedChange = new SpeedChange(EditVersion, clip.Id, group.Select(c => (c, c.Length, c.Speed)).ToList(), later);
        }

        long delta = 0;
        foreach (var (c, length, speed) in speedChange.Group)
        {
            c.SpeedPercent = percent;
            c.Length = Math.Max(1, (long)Math.Round(length * speed / (percent / 100)));
            if (c == clip) delta = c.Length - length;
        }
        foreach (var o in speedChange.Later) o.Clip.Start = o.Start + delta;

        OnPropertyChanged(nameof(SelectedClip));
        NotifyContentChanged();
    }

    /// <summary>Plays a clip (and its linked partner) backwards, or forwards again.</summary>
    public void SetReverse(Clip clip, bool reverse)
    {
        if (!CanChangeSpeed(clip) || IsLocked(clip) || clip.Reverse == reverse) return;
        SaveUndoPoint();
        foreach (Clip c in Project.LinkedGroup(clip).Where(c => !IsLocked(c) && CanChangeSpeed(c)))
            c.Reverse = reverse;
        NotifyContentChanged();
    }

    // ===================== Rolling edit, slip, slide, rate stretch (drag) =====================
    // All four remember the clips as they were at the start of the drag; every mouse move starts
    // again from there, so nothing drifts. Linked partners (a video's sound) do the same.

    private sealed record Before(Clip Clip, long Start, long In, long Length, double Speed);

    private List<Before> toolBefore = [];
    private string? toolMode;
    private Clip? toolLeft, toolRight;   // roll: the clips on each side of the cut; slide: the clip itself in toolLeft
    private bool toolLeftEdge;

    private Before Remember(Clip c) => new(c, c.Start, c.In, c.Length, c.SpeedPercent);

    private void Restore()
    {
        foreach (var b in toolBefore)
            (b.Clip.Start, b.Clip.In, b.Clip.Length, b.Clip.SpeedPercent) = (b.Start, b.In, b.Length, b.Speed);
    }

    private List<Clip> Group(Clip c) => Project.LinkedGroup(c).Where(x => !IsLocked(x)).ToList();

    /// <summary>The partner of <paramref name="c"/> (its linked clip) on another track, matching <paramref name="like"/>'s group.</summary>
    private static Clip? PartnerOf(Clip c, List<Clip> group) => group.FirstOrDefault(x => x.LinkId == c.LinkId && x != c);

    /// <summary>Rolling edit: drag the cut between two touching clips.</summary>
    public void BeginRoll(Clip left, Clip right)
    {
        SaveUndoPoint();
        toolMode = "Roll";
        toolLeft = left;
        toolRight = right;
        toolBefore = Group(left).Concat(Group(right)).Distinct().Select(Remember).ToList();
    }

    /// <summary>Slip: drag inside a clip to change which part of its file it shows.</summary>
    public void BeginSlip(Clip clip)
    {
        SaveUndoPoint();
        toolMode = "Slip";
        toolLeft = clip;
        toolBefore = Group(clip).Select(Remember).ToList();
    }

    /// <summary>Slide: drag a clip between its neighbours; they get shorter/longer so no gap appears.</summary>
    public void BeginSlide(Clip clip)
    {
        SaveUndoPoint();
        toolMode = "Slide";
        toolLeft = clip;
        var involved = new List<Clip>();
        foreach (Clip c in Group(clip))
        {
            involved.Add(c);
            if (PreviousClipAtCut(c) is Clip before && !IsLocked(before)) involved.Add(before);
            if (NextClipAtCut(c) is Clip after && !IsLocked(after)) involved.Add(after);
        }
        toolBefore = involved.Distinct().Select(Remember).ToList();
    }

    /// <summary>Rate stretch: drag a clip's edge; it keeps showing the same part of its file, faster or slower.</summary>
    public void BeginRateStretch(Clip clip, bool leftEdge)
    {
        SaveUndoPoint();
        toolMode = "Rate";
        toolLeft = clip;
        toolLeftEdge = leftEdge;
        toolBefore = Group(clip).Where(CanChangeSpeed).Select(Remember).ToList();
    }

    /// <summary>The mouse moved <paramref name="delta"/> frames since the drag began.</summary>
    public void UpdateTool(long delta)
    {
        if (toolMode is null || toolLeft is null) return;
        Restore();

        switch (toolMode)
        {
            case "Roll" when toolRight is not null:
            {
                // The cut moves; each pair of clips at the cut changes together.
                var pairs = new List<(Clip L, Clip R)> { (toolLeft, toolRight) };
                foreach (Clip l in Group(toolLeft).Where(x => x != toolLeft))
                    if (NextClipAtCut(l) is Clip r && Group(toolRight).Contains(r)) pairs.Add((l, r));
                foreach (var (l, r) in pairs)
                {
                    var (_, leftAfter) = TimelineEdits.RoomToGrow(l, Fps);
                    var (rightBefore, _) = TimelineEdits.RoomToGrow(r, Fps);
                    delta = Math.Clamp(delta, -Math.Min(l.Length - 1, rightBefore), Math.Min(r.Length - 1, leftAfter));
                }
                foreach (var (l, r) in pairs)
                {
                    TimelineEdits.MoveEdges(l, 0, delta);
                    TimelineEdits.MoveEdges(r, delta, 0);
                }
                ShowSnapLine(toolLeft.End);
                break;
            }
            case "Slip":
            {
                // Same place and length; the source moves the other way (drag right = earlier part of the file).
                foreach (Clip c in Group(toolLeft))
                {
                    if (c.Media.Kind is MediaKind.Image or MediaKind.Text) continue;
                    long media = c.Media.LengthInFrames(Fps);
                    long shift = (long)Math.Round(-delta * c.Speed) * (c.Reverse ? -1 : 1);
                    c.In = Math.Clamp(c.In + shift, 0, Math.Max(0, media - c.SourceLength));
                }
                break;
            }
            case "Slide":
            {
                foreach (Clip c in Group(toolLeft))
                {
                    Clip? before = PreviousClipAtCut(c);
                    Clip? after = NextClipAtCut(c);
                    // Limits: the neighbours must keep at least one frame and have footage to grow into.
                    long min = before is null ? -c.Start : -(before.Length - 1);
                    long max = after is null ? long.MaxValue / 4 : after.Length - 1;
                    if (after is not null) min = Math.Max(min, -TimelineEdits.RoomToGrow(after, Fps).BeforeStart);
                    if (before is not null) max = Math.Min(max, TimelineEdits.RoomToGrow(before, Fps).AfterEnd);
                    delta = Math.Clamp(delta, min, max);
                }
                foreach (Clip c in Group(toolLeft))
                {
                    Clip? before = PreviousClipAtCut(c);
                    Clip? after = NextClipAtCut(c);
                    c.Start += delta;
                    if (before is not null) TimelineEdits.MoveEdges(before, 0, delta);
                    if (after is not null) TimelineEdits.MoveEdges(after, delta, 0);
                }
                break;
            }
            case "Rate":
            {
                foreach (var b in toolBefore)
                {
                    // New length from the drag; speed so that the same source frames fit in it.
                    long source = (long)Math.Round(b.Length * b.Speed / 100);
                    long length = toolLeftEdge ? b.Length - delta : b.Length + delta;
                    long min = (long)Math.Ceiling(source / (MaxSpeedPercent / 100)), max = (long)Math.Floor(source / (MinSpeedPercent / 100));
                    length = Math.Clamp(length, Math.Max(1, min), Math.Max(1, max));
                    b.Clip.SpeedPercent = Math.Round(source * 100.0 / length, 2);
                    b.Clip.Length = length;
                    if (toolLeftEdge) b.Clip.Start = b.Start + b.Length - length;
                }
                break;
            }
        }
        RedrawNeeded?.Invoke();
    }

    public void EndTool()
    {
        ShowSnapLine(null);
        bool changed = toolBefore.Any(b => b.Clip.Start != b.Start || b.Clip.In != b.In || b.Clip.Length != b.Length || b.Clip.SpeedPercent != b.Speed);
        if (changed)
        {
            // A rate-stretched clip that got longer may now cover its neighbour: overwrite like a trim.
            if (toolMode == "Rate")
            {
                var stretched = toolBefore.Select(b => b.Clip).ToHashSet();
                foreach (Clip c in stretched) TimelineEdits.ClearRange(Project.TrackOf(c), c.Start, c.End, stretched);
            }
            NotifyContentChanged();
        }
        else
        {
            DropUndoPoint();
            RedrawNeeded?.Invoke();
        }
        toolBefore = [];
        toolMode = null;
        toolLeft = toolRight = null;
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

        // Start again from how the clips were when the drag began.
        foreach (var o in dragOriginals)
            (o.Clip.Start, o.Clip.In, o.Clip.Length) = (o.Start, o.In, o.Length);

        // How far the edge moves, kept within what the source file allows (speed and reverse included),
        // never before frame 0, and never shorter than one frame.
        long edge = trimLeft ? anchor.Start : anchor.Start + anchor.Length;
        long delta = snapped - edge;
        foreach (var o in dragOriginals)
        {
            var (before, after) = TimelineEdits.RoomToGrow(o.Clip, Fps);
            if (trimLeft)
            {
                long min = -(trimRipple ? before : Math.Min(before, o.Start));
                delta = Math.Clamp(delta, min, o.Length - 1);
            }
            else
            {
                delta = Math.Clamp(delta, -(o.Length - 1), after);
            }
        }

        foreach (var o in dragOriginals)
        {
            if (trimLeft)
            {
                TimelineEdits.MoveEdges(o.Clip, delta, 0);
                // Ripple trim: the clip's start stays put and everything after it slides instead.
                if (trimRipple) o.Clip.Start = o.Start;
            }
            else
            {
                TimelineEdits.MoveEdges(o.Clip, 0, delta);
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

    private void OnTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(AudioTracks));
}
