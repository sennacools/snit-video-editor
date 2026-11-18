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
        if (AudioClip is Clip clip) Change(clip, property, change);
    }

    // ===================== Text (titles) =====================

    private Clip? TitleClip => timeline.SelectedClip is { IsTitle: true } c ? c : null;
    private TitleStyle? Style => TitleClip?.Title;

    public bool HasTitle => TitleClip is not null;

    public string TitleText
    {
        get => Style?.Text ?? "";
        set => ChangeTitle(nameof(TitleText), s => s.Text = value);
    }

    public string TitleFont
    {
        get => Style?.FontFamily ?? "";
        set { if (!string.IsNullOrEmpty(value)) ChangeTitle(nameof(TitleFont), s => s.FontFamily = value); }
    }

    public double TitleSize
    {
        get => Style?.FontSize ?? 96;
        set => ChangeTitle(nameof(TitleSize), s => s.FontSize = Math.Round(value));
    }

    public string TitleColor
    {
        get => Style?.Color ?? "#FFFFFF";
        set
        {
            string color = value.Trim();
            if (!color.StartsWith('#')) color = "#" + color;
            if (Services.TitleRenderer.IsColor(color)) ChangeTitle(nameof(TitleColor), s => s.Color = color.ToUpperInvariant());
            else OnPropertyChanged(); // not a colour: put the old value back in the box
        }
    }

    public bool TitleBold
    {
        get => Style?.Bold ?? true;
        set => ChangeTitle(nameof(TitleBold), s => s.Bold = value);
    }

    public bool TitleShadow
    {
        get => Style?.Shadow ?? true;
        set => ChangeTitle(nameof(TitleShadow), s => s.Shadow = value);
    }

    /// <summary>Position in percent: 0 = left edge, 50 = centre, 100 = right edge.</summary>
    public double TitleX
    {
        get => (Style?.X ?? 0.5) * 100;
        set => ChangeTitle(nameof(TitleX), s => s.X = Math.Round(value) / 100);
    }

    /// <summary>Position in percent: 0 = top, 50 = middle, 100 = bottom.</summary>
    public double TitleY
    {
        get => (Style?.Y ?? 0.5) * 100;
        set => ChangeTitle(nameof(TitleY), s => s.Y = Math.Round(value) / 100);
    }

    /// <summary>All fonts installed on this computer, A to Z.</summary>
    public IReadOnlyList<string> Fonts { get; } = System.Windows.Media.Fonts.SystemFontFamilies
        .Select(f => f.Source).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Quick colour choices (the app's own colours plus black and white).</summary>
    public IReadOnlyList<string> ColorChoices { get; } =
        ["#FFFFFF", "#0E0F11", "#E8C547", "#FF7A45", "#5B9BFF", "#3FB57A", "#E5484D", "#B388FF"];

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetTitleColor(string color) => TitleColor = color;

    private void ChangeTitle(string property, Action<TitleStyle> change)
    {
        if (TitleClip is Clip clip) Change(clip, property, c => change(c.Title!));
    }

    // ===================== Transform and colour =====================

    /// <summary>The clip whose picture we change: the selected clip, or the video linked to a selected sound.</summary>
    private Clip? VisualClip
    {
        get
        {
            Clip? selected = timeline.SelectedClip;
            if (selected is null) return null;
            return timeline.Project.LinkedGroup(selected)
                .FirstOrDefault(c => timeline.Project.TrackOf(c).Kind == TrackKind.Video);
        }
    }

    public bool HasVisual => VisualClip is not null;

    private ClipLook Look => VisualClip?.Look ?? ClipLook.Default;

    private void ChangeLook(string property, Func<ClipLook, ClipLook> change)
    {
        if (VisualClip is Clip clip) Change(clip, property, c => c.Look = change(c.Look));
    }

    public double Scale { get => Look.Scale; set => ChangeLook(nameof(Scale), l => l with { Scale = Math.Round(value) }); }
    public double Rotation { get => Look.Rotation; set => ChangeLook(nameof(Rotation), l => l with { Rotation = Math.Round(value) }); }
    public double Opacity { get => Look.Opacity; set => ChangeLook(nameof(Opacity), l => l with { Opacity = Math.Round(value) }); }
    public double PositionX { get => Look.PositionX; set => ChangeLook(nameof(PositionX), l => l with { PositionX = Math.Round(value) }); }
    public double PositionY { get => Look.PositionY; set => ChangeLook(nameof(PositionY), l => l with { PositionY = Math.Round(value) }); }

    public double Exposure { get => Look.Exposure; set => ChangeLook(nameof(Exposure), l => l with { Exposure = Math.Round(value, 1) }); }
    public double Contrast { get => Look.Contrast; set => ChangeLook(nameof(Contrast), l => l with { Contrast = Math.Round(value) }); }
    public double Saturation { get => Look.Saturation; set => ChangeLook(nameof(Saturation), l => l with { Saturation = Math.Round(value) }); }
    public double Temperature { get => Look.Temperature; set => ChangeLook(nameof(Temperature), l => l with { Temperature = Math.Round(value) }); }

    /// <summary>The frame size, for the position sliders' range.</summary>
    public double FrameWidth => timeline.Project.Settings.Width;
    public double FrameHeight => timeline.Project.Settings.Height;

    /// <summary>Puts every transform and colour setting back to normal (one undo step).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetLook()
    {
        if (VisualClip is Clip clip && clip.Look != ClipLook.Default)
            Change(clip, nameof(ResetLook) + Guid.NewGuid(), c => c.Look = ClipLook.Default);
    }

    // ===================== Speed =====================

    public bool HasSpeed => timeline.SelectedClip is Clip c && TimelineViewModel.CanChangeSpeed(c);

    public double SpeedPercent
    {
        get => timeline.SelectedClip?.SpeedPercent ?? 100;
        set { if (timeline.SelectedClip is Clip c) timeline.SetSpeed(c, value); }
    }

    public bool Reverse
    {
        get => timeline.SelectedClip?.Reverse ?? false;
        set { if (timeline.SelectedClip is Clip c) timeline.SetReverse(c, value); }
    }

    // ===================== Shared =====================

    private void Change(Clip clip, string property, Action<Clip> change)
    {
        if (timeline.IsLocked(clip)) return;

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
