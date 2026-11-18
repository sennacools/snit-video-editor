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
        Keys = new KeyframeMarks(this);
        timeline.PropertyChanged += OnTimelinePropertyChanged;
        timeline.ContentChanged += Refresh; // after undo/redo the values may be different
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineViewModel.SelectedClip) or "") Refresh();
        // Animated values change as the playhead moves.
        else if (e.PropertyName == nameof(TimelineViewModel.PlayheadFrame)
                 && (VisualClip is { Keyframes.Count: > 0 } || AudioClip is { Keyframes.Count: > 0 })) Refresh();
    }

    /// <summary>Re-read every value from the selected clip.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        Keys?.Refresh();
        SyncEffects();
    }

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

    /// <summary>
    /// The clip's volume. With volume keyframes it shows the value at the playhead, and changing
    /// it sets a keyframe there (like the transform settings).
    /// </summary>
    public double VolumeDb
    {
        get
        {
            if (AudioClip is not Clip c) return 0;
            return Core.Keyframes.Of(c.Keyframes, Core.Keyframes.Volume) is { } keys
                ? Math.Round(Core.Keyframes.ValueAt(keys, AudioPlayheadInClip), 1)
                : c.VolumeDb;
        }
        set
        {
            double v = Math.Round(value, 1);
            if (AudioClip is Clip c && c.Keyframes.ContainsKey(Core.Keyframes.Volume))
            {
                long at = AudioPlayheadInClip;
                ChangeAudio(nameof(VolumeDb), x => x.Keyframes = Core.Keyframes.Set(x.Keyframes, Core.Keyframes.Volume, at, v));
            }
            else ChangeAudio(nameof(VolumeDb), x => x.VolumeDb = v);
        }
    }

    /// <summary>Where the playhead is inside the selected sound clip (frames from its start).</summary>
    private long AudioPlayheadInClip => AudioClip is Clip c ? Math.Clamp(timeline.PlayheadFrame - c.Start, 0, Math.Max(0, c.Length - 1)) : 0;

    /// <summary>The diamond next to the volume slider.</summary>
    public KeyMark VolumeKey
    {
        get
        {
            if (AudioClip is not Clip c || Core.Keyframes.Of(c.Keyframes, Core.Keyframes.Volume) is not { } keys)
                return new KeyMark("◇", false, "Animate the volume: set a keyframe at the playhead");
            return keys.Any(k => k.Frame == AudioPlayheadInClip)
                ? new KeyMark("◆", true, "Remove the volume keyframe at the playhead")
                : new KeyMark("◇", true, "Add a volume keyframe at the playhead");
        }
    }

    /// <summary>Volume diamond: start animating, add a keyframe here, or remove the one here.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleVolumeKeyframe()
    {
        if (AudioClip is not Clip clip) return;
        long at = AudioPlayheadInClip;
        double current = VolumeDb;
        bool hereAlready = Core.Keyframes.Of(clip.Keyframes, Core.Keyframes.Volume)?.Any(k => k.Frame == at) == true;
        ChangeAudio("volume-keyframe" + Guid.NewGuid(), c =>
        {
            if (!hereAlready)
            {
                c.Keyframes = Core.Keyframes.Set(c.Keyframes, Core.Keyframes.Volume, at, current);
                return;
            }
            c.Keyframes = Core.Keyframes.Remove(c.Keyframes, Core.Keyframes.Volume, at);
            if (!c.Keyframes.ContainsKey(Core.Keyframes.Volume)) c.VolumeDb = current; // keep the value it had
        });
    }

    // Equaliser and noise reduction.
    public double EqLow { get => AudioClip?.Audio.Low ?? 0; set => ChangeAudio(nameof(EqLow), c => c.Audio = c.Audio with { Low = Math.Round(value, 1) }); }
    public double EqMid { get => AudioClip?.Audio.Mid ?? 0; set => ChangeAudio(nameof(EqMid), c => c.Audio = c.Audio with { Mid = Math.Round(value, 1) }); }
    public double EqHigh { get => AudioClip?.Audio.High ?? 0; set => ChangeAudio(nameof(EqHigh), c => c.Audio = c.Audio with { High = Math.Round(value, 1) }); }
    public double NoiseReduction { get => AudioClip?.Audio.NoiseReduction ?? 0; set => ChangeAudio(nameof(NoiseReduction), c => c.Audio = c.Audio with { NoiseReduction = Math.Round(value) }); }

    /// <summary>Puts the equaliser and noise reduction back to off.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetSoundTools()
    {
        if (AudioClip is { } clip && clip.Audio != ClipAudio.Default)
            ChangeAudio("reset-sound" + Guid.NewGuid(), c => c.Audio = ClipAudio.Default);
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

    /// <summary>A see-through dark box behind the text.</summary>
    public bool TitleBox
    {
        get => Style is { Box.Length: > 0 };
        set => ChangeTitle(nameof(TitleBox), s => s.Box = value ? (s.Box.Length > 0 ? s.Box : TitleTemplates.DarkBox) : "");
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

    // The five transform settings can be animated with keyframes. They show their value at the
    // playhead; when a setting is animated, changing it sets a keyframe at the playhead.
    public double Scale { get => Animatable(nameof(Scale)); set => SetAnimatable(nameof(Scale), Math.Round(value)); }
    public double Rotation { get => Animatable(nameof(Rotation)); set => SetAnimatable(nameof(Rotation), Math.Round(value)); }
    public double Opacity { get => Animatable(nameof(Opacity)); set => SetAnimatable(nameof(Opacity), Math.Round(value)); }
    public double PositionX { get => Animatable(nameof(PositionX)); set => SetAnimatable(nameof(PositionX), Math.Round(value)); }
    public double PositionY { get => Animatable(nameof(PositionY)); set => SetAnimatable(nameof(PositionY), Math.Round(value)); }

    // ===================== Keyframes =====================

    /// <summary>Where the playhead is inside the selected clip (frames from its start).</summary>
    private long PlayheadInClip => VisualClip is Clip c ? Math.Clamp(timeline.PlayheadFrame - c.Start, 0, Math.Max(0, c.Length - 1)) : 0;

    private double Animatable(string name)
    {
        ClipLook now = VisualClip is Clip c ? Core.Keyframes.LookAt(c.Look, c.Keyframes, PlayheadInClip) : ClipLook.Default;
        return name switch
        {
            nameof(Scale) => now.Scale,
            nameof(Rotation) => now.Rotation,
            nameof(Opacity) => now.Opacity,
            nameof(PositionX) => now.PositionX,
            _ => now.PositionY,
        };
    }

    private static ClipLook WithValue(ClipLook look, string name, double value) => name switch
    {
        nameof(Scale) => look with { Scale = value },
        nameof(Rotation) => look with { Rotation = value },
        nameof(Opacity) => look with { Opacity = value },
        nameof(PositionX) => look with { PositionX = value },
        _ => look with { PositionY = value },
    };

    private void SetAnimatable(string name, double value)
    {
        if (VisualClip is not Clip clip) return;
        long at = PlayheadInClip;
        if (clip.Keyframes.ContainsKey(name))
            Change(clip, name, c => c.Keyframes = Core.Keyframes.Set(c.Keyframes, name, at, value));
        else
            ChangeLook(name, l => WithValue(l, name, value));
    }

    /// <summary>The diamond buttons next to the transform sliders: Keys["Scale"] etc.</summary>
    public KeyframeMarks Keys { get; }

    /// <summary>
    /// Diamond button: start animating (keyframe at the playhead), add a keyframe here, or remove
    /// the keyframe that is here (removing the last one stops the animation at that value).
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleKeyframe(string name)
    {
        if (VisualClip is not Clip clip) return;
        long at = PlayheadInClip;
        double current = Animatable(name);
        bool hereAlready = clip.Keyframes.TryGetValue(name, out var keys) && keys.Any(k => k.Frame == at);

        Change(clip, "keyframe" + Guid.NewGuid(), c =>
        {
            if (!hereAlready)
            {
                c.Keyframes = Core.Keyframes.Set(c.Keyframes, name, at, current);
                return;
            }
            c.Keyframes = Core.Keyframes.Remove(c.Keyframes, name, at);
            if (!c.Keyframes.ContainsKey(name)) c.Look = WithValue(c.Look, name, current); // keep the value it had
        });
    }

    /// <summary>The keyframe state of each setting, for the diamond buttons.</summary>
    public sealed class KeyframeMarks : ObservableObject
    {
        private readonly InspectorViewModel owner;
        public KeyframeMarks(InspectorViewModel owner) => this.owner = owner;

        public KeyMark this[string name]
        {
            get
            {
                if (owner.VisualClip is not Clip clip || !clip.Keyframes.TryGetValue(name, out var keys))
                    return new KeyMark("◇", false, "Animate: set a keyframe at the playhead");
                bool here = keys.Any(k => k.Frame == owner.PlayheadInClip);
                return here
                    ? new KeyMark("◆", true, "Remove the keyframe at the playhead")
                    : new KeyMark("◇", true, "Add a keyframe at the playhead");
            }
        }

        public void Refresh() => OnPropertyChanged("Item[]");
    }

    public sealed record KeyMark(string Glyph, bool IsAnimated, string ToolTip);

    public double Exposure { get => Look.Exposure; set => ChangeLook(nameof(Exposure), l => l with { Exposure = Math.Round(value, 1) }); }
    public double Contrast { get => Look.Contrast; set => ChangeLook(nameof(Contrast), l => l with { Contrast = Math.Round(value) }); }
    public double Saturation { get => Look.Saturation; set => ChangeLook(nameof(Saturation), l => l with { Saturation = Math.Round(value) }); }
    public double Temperature { get => Look.Temperature; set => ChangeLook(nameof(Temperature), l => l with { Temperature = Math.Round(value) }); }
    public double Tint { get => Look.Tint; set => ChangeLook(nameof(Tint), l => l with { Tint = Math.Round(value) }); }

    // Colour wheels: puck position (-1..1) and the lightness slider under each wheel.
    private static double R3(double v) => Math.Round(Math.Clamp(v, -1, 1), 3);
    public double ShadowsX { get => Look.ShadowsX; set => ChangeLook("ShadowsPuck", l => l with { ShadowsX = R3(value) }); }
    public double ShadowsY { get => Look.ShadowsY; set => ChangeLook("ShadowsPuck", l => l with { ShadowsY = R3(value) }); }
    public double ShadowsLight { get => Look.ShadowsLight; set => ChangeLook(nameof(ShadowsLight), l => l with { ShadowsLight = R3(value) }); }
    public double MidtonesX { get => Look.MidtonesX; set => ChangeLook("MidtonesPuck", l => l with { MidtonesX = R3(value) }); }
    public double MidtonesY { get => Look.MidtonesY; set => ChangeLook("MidtonesPuck", l => l with { MidtonesY = R3(value) }); }
    public double MidtonesLight { get => Look.MidtonesLight; set => ChangeLook(nameof(MidtonesLight), l => l with { MidtonesLight = R3(value) }); }
    public double HighlightsX { get => Look.HighlightsX; set => ChangeLook("HighlightsPuck", l => l with { HighlightsX = R3(value) }); }
    public double HighlightsY { get => Look.HighlightsY; set => ChangeLook("HighlightsPuck", l => l with { HighlightsY = R3(value) }); }
    public double HighlightsLight { get => Look.HighlightsLight; set => ChangeLook(nameof(HighlightsLight), l => l with { HighlightsLight = R3(value) }); }

    // Curves, as text ("" = straight).
    public string CurveMaster { get => Look.CurveMaster; set => ChangeLook(nameof(CurveMaster), l => l with { CurveMaster = Clean(value) }); }
    public string CurveRed { get => Look.CurveRed; set => ChangeLook(nameof(CurveRed), l => l with { CurveRed = Clean(value) }); }
    public string CurveGreen { get => Look.CurveGreen; set => ChangeLook(nameof(CurveGreen), l => l with { CurveGreen = Clean(value) }); }
    public string CurveBlue { get => Look.CurveBlue; set => ChangeLook(nameof(CurveBlue), l => l with { CurveBlue = Clean(value) }); }
    private static string Clean(string? curve) => Curves.Format(Curves.Parse(curve));

    /// <summary>Puts every colour setting (not size or position) back to normal.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetColor()
    {
        if (VisualClip is Clip clip && clip.Look.HasColor)
            ChangeLook(nameof(ResetColor) + Guid.NewGuid(), l => l.WithoutColor());
    }

    /// <summary>The frame size, for the position sliders' range.</summary>
    public double FrameWidth => timeline.Project.Settings.Width;
    public double FrameHeight => timeline.Project.Settings.Height;

    /// <summary>Puts every transform and colour setting back to normal (one undo step).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ResetLook()
    {
        if (VisualClip is Clip clip && (clip.Look != ClipLook.Default || clip.Keyframes.Count > 0))
            Change(clip, nameof(ResetLook) + Guid.NewGuid(), c =>
            {
                c.Look = ClipLook.Default;
                c.Keyframes = Core.Keyframes.None;
            });
    }

    // ===================== Effects =====================

    /// <summary>The selected clip's effects, top to bottom (only for clips on video tracks).</summary>
    public System.Collections.ObjectModel.ObservableCollection<EffectRowViewModel> EffectRows { get; } = [];

    public bool HasEffects => EffectRows.Count > 0;

    /// <summary>
    /// Keeps the effect rows in step with the clip. The rows are only rebuilt when effects are added,
    /// removed or reordered; otherwise they just re-read their values, so a slider you are dragging
    /// is never replaced under your mouse.
    /// </summary>
    private void SyncEffects()
    {
        Clip? clip = VisualClip;
        var effects = clip?.Effects ?? [];
        if (!EffectRows.Select(r => r.Id).SequenceEqual(effects.Select(e => e.Id)))
        {
            EffectRows.Clear();
            foreach (ClipEffect effect in effects)
            {
                Guid id = effect.Id;
                EffectRows.Add(new EffectRowViewModel(effect,
                    current: () => VisualClip?.Effects.FirstOrDefault(e => e.Id == id),
                    change: (what, update) => ChangeEffect(id, what, update),
                    remove: () => RemoveEffect(id)));
            }
            OnPropertyChanged(nameof(HasEffects));
        }
        else
        {
            foreach (var row in EffectRows) row.Refresh();
        }
    }

    private void ChangeEffect(Guid id, string what, Func<ClipEffect, ClipEffect> update)
    {
        if (VisualClip is not Clip clip) return;
        Change(clip, $"effect:{id}:{what}", c => c.Effects = c.Effects.Select(e => e.Id == id ? update(e) : e).ToList());
    }

    private void RemoveEffect(Guid id)
    {
        if (VisualClip is not Clip clip) return;
        Change(clip, "remove-effect" + Guid.NewGuid(), c => c.Effects = c.Effects.Where(e => e.Id != id).ToList());
    }

    // ===================== Transitions =====================

    /// <summary>The choices in the transition dropdowns. Null = no transition.</summary>
    public IReadOnlyList<Choice<TransitionKind?>> TransitionChoices { get; } =
    [
        new("None", null),
        new("Cross dissolve", TransitionKind.CrossDissolve),
        new("Dip to black", TransitionKind.DipToBlack),
    ];

    public bool HasClip => timeline.SelectedClip is not null;

    /// <summary>The transition at the start of the selected clip.</summary>
    private Transition? StartTransition => timeline.SelectedClip?.TransitionIn;

    /// <summary>The transition at the end: at a cut it is stored on the next clip's start.</summary>
    private Transition? EndTransition => timeline.SelectedClip is Clip c
        ? (timeline.NextClipAtCut(c) is Clip next ? next.TransitionIn : c.TransitionOut)
        : null;

    public Choice<TransitionKind?> StartKind
    {
        get => TransitionChoices.First(t => t.Value == StartTransition?.Kind);
        set => ChangeTransition(atStart: true, value.Value, (long)Math.Round(StartSeconds * timeline.Fps));
    }

    public double StartSeconds
    {
        get => (StartTransition?.Frames ?? TransitionPlan.DefaultFrames) / timeline.Fps;
        set { if (StartTransition is { } t) ChangeTransition(atStart: true, t.Kind, (long)Math.Round(value * timeline.Fps)); }
    }

    public Choice<TransitionKind?> EndKind
    {
        get => TransitionChoices.First(t => t.Value == EndTransition?.Kind);
        set => ChangeTransition(atStart: false, value.Value, (long)Math.Round(EndSeconds * timeline.Fps));
    }

    public double EndSeconds
    {
        get => (EndTransition?.Frames ?? TransitionPlan.DefaultFrames) / timeline.Fps;
        set { if (EndTransition is { } t) ChangeTransition(atStart: false, t.Kind, (long)Math.Round(value * timeline.Fps)); }
    }

    /// <summary>A transition can be at most 4 seconds (and never longer than the clip).</summary>
    public double TransitionMaximum => timeline.SelectedClip is Clip c ? Math.Max(0.1, Math.Min(4, c.Length / timeline.Fps)) : 4;

    private void ChangeTransition(bool atStart, TransitionKind? kind, long frames)
    {
        if (timeline.SelectedClip is not Clip clip || timeline.IsLocked(clip)) return;
        string property = (atStart ? "TransitionIn" : "TransitionOut") + clip.Id;
        // One undo step per slider drag, like the other settings.
        bool newStep = lastChange != property || lastChangeVersion != timeline.EditVersion;
        if (newStep)
        {
            timeline.SaveUndoPoint();
            lastChange = property;
            lastChangeVersion = timeline.EditVersion;
        }
        timeline.SetTransition(clip, atStart, kind, Math.Max(1, frames), saveUndo: false);
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
