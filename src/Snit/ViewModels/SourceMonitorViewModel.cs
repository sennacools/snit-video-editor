using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>
/// The Source monitor: watch one file from the bin, mark the part you want with in/out, and put it
/// on the timeline with Insert (",") or Overwrite ("."). It uses its own preview player on a tiny
/// project that holds just this file, so sound, scrubbing and stepping work like the main preview.
/// </summary>
public partial class SourceMonitorViewModel : ObservableObject, IDisposable
{
    private readonly Func<TimelineViewModel> timeline;
    private PlaybackEngine? player;
    private Project? sourceProject;
    private bool positionFromPlayer;

    public SourceMonitorViewModel(Func<TimelineViewModel> timeline) => this.timeline = timeline;

    /// <summary>The file being watched, or null when nothing is open.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMedia), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(InsertCommand), nameof(OverwriteCommand))]
    private MediaItem? media;

    public bool HasMedia => Media is not null;
    public string Title => Media is null ? "No clip open — double-click a file in the bin" : Media.Name;

    public ObservableCollection<PreviewLayer> Layers { get; } = [];

    private double Fps => sourceProject?.Fps ?? 30;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentTimecode), nameof(PlayheadSeconds))]
    private long playheadFrame;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScrubPixelsPerSecond))]
    private long durationFrames;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarkedTimecode), nameof(InSeconds))]
    private long? inPoint;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(MarkedTimecode), nameof(OutSeconds))]
    private long? outPoint;

    [ObservableProperty]
    private bool isPlaying;

    public string CurrentTimecode => Timecode.FromFrames(PlayheadFrame, Fps);

    /// <summary>For the scrub bar: the playhead in seconds.</summary>
    public double PlayheadSeconds
    {
        get => PlayheadFrame / Fps;
        set => PlayheadFrame = Math.Clamp((long)Math.Round(value * Fps), 0, DurationFrames);
    }

    public double SourceFps => Fps;

    /// <summary>Width of the scrub bar (set by the window), so the whole file fits in it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScrubPixelsPerSecond))]
    private double scrubWidth = 600;

    public double ScrubPixelsPerSecond => ScrubWidth / Math.Max(1, DurationFrames / Fps);

    /// <summary>How long the marked part is (the whole file when nothing is marked).</summary>
    public string MarkedTimecode => Timecode.FromFrames(MarkedEnd - MarkedStart, Fps);

    public double InSeconds => InPoint is long i ? i / Fps : double.NaN;
    public double OutSeconds => OutPoint is long o ? o / Fps : double.NaN;

    private long MarkedStart => InPoint ?? 0;
    private long MarkedEnd => OutPoint is long o && o > MarkedStart ? o : DurationFrames;

    /// <summary>Opens a file from the bin in the Source monitor.</summary>
    public void Open(MediaItem item, ProjectSettings settings)
    {
        Stop();
        player?.Dispose();

        // A tiny project with just this file: the whole video on V1 (and its sound on A1).
        sourceProject = new Project { Settings = new ProjectSettings { Width = settings.Width, Height = settings.Height, FrameRate = settings.FrameRate } };
        long length = item.Kind is MediaKind.Image or MediaKind.Text
            ? (long)Math.Round(MediaImporter.DefaultImageSeconds * settings.FrameRate)
            : item.LengthInFrames(settings.FrameRate);
        if (item.Kind != MediaKind.Audio) sourceProject.FirstVideoTrack.Clips.Add(new Clip { Media = item, Length = length });
        if (item.HasAudio || item.Kind == MediaKind.Audio) sourceProject.FirstAudioTrack.Clips.Add(new Clip { Media = item, Length = length });

        player = new PlaybackEngine(sourceProject.Settings, 640);
        player.FrameReady += bundle => PreviewLayer.ApplyBundle(Layers, bundle, sourceProject.Settings, _ => null!);
        player.PositionChanged += frame =>
        {
            positionFromPlayer = true;
            PlayheadFrame = frame;
            positionFromPlayer = false;
        };
        player.Stopped += () => IsPlaying = false;
        player.UpdateTimeline(sourceProject);

        Media = item;
        DurationFrames = length;
        InPoint = OutPoint = null;
        PlayheadFrame = 0;
        player.Seek(0);
        OnPropertyChanged(nameof(CurrentTimecode));
        OnPropertyChanged(nameof(MarkedTimecode));
    }

    partial void OnPlayheadFrameChanged(long value)
    {
        if (positionFromPlayer || player is null) return;
        if (IsPlaying) player.Play(value);
        else player.Seek(value);
    }

    // ---- Transport ----

    [RelayCommand]
    public void PlayPause()
    {
        if (player is null) return;
        if (IsPlaying) { Stop(); return; }
        if (PlayheadFrame >= DurationFrames) PlayheadFrame = 0;
        IsPlaying = true;
        player.Play(PlayheadFrame);
    }

    public void Stop()
    {
        if (!IsPlaying) return;
        player?.Stop();
        IsPlaying = false;
    }

    [RelayCommand] public void StepBack() { Stop(); PlayheadFrame = Math.Max(0, PlayheadFrame - 1); }
    [RelayCommand] public void StepForward() { Stop(); PlayheadFrame = Math.Min(DurationFrames, PlayheadFrame + 1); }
    [RelayCommand] public void GoToStart() => PlayheadFrame = 0;
    [RelayCommand] public void GoToEnd() => PlayheadFrame = DurationFrames;

    // ---- Marks ----

    [RelayCommand]
    public void MarkIn()
    {
        InPoint = PlayheadFrame;
        if (OutPoint <= InPoint) OutPoint = null;
    }

    [RelayCommand]
    public void MarkOut()
    {
        OutPoint = PlayheadFrame;
        if (InPoint >= OutPoint) InPoint = null;
    }

    // ---- Putting it on the timeline ----

    [RelayCommand(CanExecute = nameof(HasMedia))]
    public void Insert() => Place(insert: true);

    [RelayCommand(CanExecute = nameof(HasMedia))]
    public void Overwrite() => Place(insert: false);

    private void Place(bool insert)
    {
        if (Media is null) return;
        Stop();
        timeline().PlaceFromSource(Media, MarkedStart, Math.Max(1, MarkedEnd - MarkedStart), insert);
    }

    public void Dispose() => player?.Dispose();
}
