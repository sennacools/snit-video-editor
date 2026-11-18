using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Snit.Core;

namespace Snit.ViewModels;

/// <summary>
/// The inspector panel: settings of the selected clip. The sliders write straight into the clip,
/// and each change can be undone (one whole slider drag is one undo step).
/// </summary>
public partial class InspectorViewModel : ObservableObject
{
    private readonly TimelineViewModel timeline;
    private string? lastChange;
    private int lastChangeVersion = -1;

    public InspectorViewModel(TimelineViewModel timeline)
    {
        this.timeline = timeline;
        timeline.PropertyChanged += OnTimelinePropertyChanged;
        timeline.ContentChanged += Refresh; // after undo/redo the values may be different
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineViewModel.SelectedClip) or "") Refresh();
    }

    /// <summary>Re-read every value from the selected clip.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public string ClipName => timeline.SelectedClipName;

    // ===================== Audio =====================

    /// <summary>
    /// The clip whose sound we change: the selected clip if it is on an audio track,
    /// otherwise the sound linked to the selected video.
    /// </summary>
    private Clip? AudioClip
    {
        get
        {
            Clip? selected = timeline.SelectedClip;
            if (selected is null) return null;
            return timeline.Project.LinkedGroup(selected)
                .FirstOrDefault(c => timeline.Project.TrackOf(c).Kind == TrackKind.Audio);
        }
    }

    public bool HasAudio => AudioClip is not null;

    public double VolumeDb
    {
        get => AudioClip?.VolumeDb ?? 0;
        set => ChangeAudio(nameof(VolumeDb), c => c.VolumeDb = Math.Round(value, 1));
    }

    public double FadeInSeconds
    {
        get => AudioClip?.FadeInSeconds ?? 0;
        set => ChangeAudio(nameof(FadeInSeconds), c => c.FadeInSeconds = Math.Round(value, 2));
    }

    public double FadeOutSeconds
    {
        get => AudioClip?.FadeOutSeconds ?? 0;
        set => ChangeAudio(nameof(FadeOutSeconds), c => c.FadeOutSeconds = Math.Round(value, 2));
    }

    /// <summary>A fade can be at most as long as the clip (and at most 10 seconds).</summary>
    public double FadeMaximum => AudioClip is Clip c ? Math.Min(10, c.Length / timeline.Fps) : 5;

    private void ChangeAudio(string property, Action<Clip> change)
    {
        if (AudioClip is not Clip clip || timeline.IsLocked(clip)) return;

        // Dragging a slider sends many small changes: only the first one makes an undo step,
        // unless something else was edited (or undone) in between.
        if (lastChange != property + clip.Id || lastChangeVersion != timeline.EditVersion)
        {
            timeline.SaveUndoPoint();
            lastChange = property + clip.Id;
            lastChangeVersion = timeline.EditVersion;
        }

        change(clip);
        timeline.NotifyContentChanged();
    }
}
