using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>The Effects section of the inspector: rows follow the clip, sliders change it, undo works.</summary>
public class EffectInspectorTests
{
    private static (Project, TimelineViewModel, InspectorViewModel, Clip) Setup()
    {
        var project = new Project();
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 5, FrameRate = 30, HasAudio = true };
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        Clip clip = project.TrackByName("V1").Clips[0];
        tl.Select(clip, add: false);
        return (project, tl, new InspectorViewModel(tl), clip);
    }

    [Fact]
    public void Adding_an_effect_shows_a_row_with_its_sliders()
    {
        var (_, tl, inspector, clip) = Setup();
        Assert.False(inspector.HasEffects);

        tl.AddEffect(clip, "crop");
        Assert.True(inspector.HasEffects);
        EffectRowViewModel row = Assert.Single(inspector.EffectRows);
        Assert.Equal("Crop", row.Name);
        Assert.Equal(4, row.Parameters.Count);
    }

    [Fact]
    public void A_slider_changes_the_clip_and_one_undo_puts_it_back()
    {
        var (project, tl, inspector, clip) = Setup();
        tl.AddEffect(clip, "blur");
        EffectParameterViewModel amount = inspector.EffectRows[0].Parameters[0];

        amount.Value = 50;
        amount.Value = 70;   // a drag: one undo step
        Assert.Equal(70, clip.Effects[0]["amount"]);

        tl.UndoCommand.Execute(null);   // undo puts back copies, so look the clip up again
        Assert.Equal(20, project.TrackByName("V1").Clips[0].Effects[0]["amount"]);
        Assert.Equal(20, inspector.EffectRows[0].Parameters[0].Value);
    }

    [Fact]
    public void Switches_colours_and_removing_work()
    {
        var (_, tl, inspector, clip) = Setup();
        tl.AddEffect(clip, "flip");
        tl.AddEffect(clip, "chromakey");
        Assert.Equal(2, inspector.EffectRows.Count);

        EffectParameterViewModel vertical = inspector.EffectRows[0].Parameters.Single(p => p.Label == "Up-down");
        Assert.True(vertical.IsSwitch);
        vertical.IsOn = true;
        Assert.Equal(1, clip.Effects[0]["vertical"]);

        EffectRowViewModel key = inspector.EffectRows[1];
        Assert.True(key.NeedsColor);
        key.Color = "0000ff";
        Assert.Equal("#0000FF", clip.Effects[1].Text);
        key.Color = "not a colour";            // ignored
        Assert.Equal("#0000FF", clip.Effects[1].Text);

        inspector.EffectRows[0].Enabled = false;
        Assert.False(clip.Effects[0].Enabled);

        inspector.EffectRows[0].RemoveCommand.Execute(null);
        Assert.Equal("chromakey", Assert.Single(clip.Effects).Kind);
        Assert.Single(inspector.EffectRows);
    }

    [Fact]
    public void Effects_only_go_on_video_clips()
    {
        var (project, tl, inspector, _) = Setup();
        Clip sound = project.TrackByName("A1").Clips[0];
        tl.AddEffect(sound, "blur");
        Assert.Empty(sound.Effects);
        tl.AddEffect(project.TrackByName("V1").Clips[0], "no-such-effect");
        Assert.Empty(project.TrackByName("V1").Clips[0].Effects);
        Assert.False(inspector.HasEffects);
    }
}
