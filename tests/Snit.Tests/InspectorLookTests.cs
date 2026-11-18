using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>The inspector's Transform, Colour and Speed controls.</summary>
public class InspectorLookTests
{
    private static MediaItem Video(double seconds = 10) => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = seconds, FrameRate = 30, HasAudio = true,
    };

    private static (Project, TimelineViewModel, InspectorViewModel) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(), project.TrackByName("V1"), 0);     // 0–300, with sound on A1
        tl.AddMedia(Video(2), project.TrackByName("V1"), 300);  // 300–360, right after it
        tl.Select(project.TrackByName("V1").Clips.Single(c => c.Start == 0), add: false);
        return (project, tl, new InspectorViewModel(tl));
    }

    [Fact]
    public void Sliders_change_the_selected_video()
    {
        var (project, _, inspector) = Setup();
        inspector.Scale = 50;
        inspector.Saturation = 0;
        Clip video = project.TrackByName("V1").Clips.Single(c => c.Start == 0);
        Assert.Equal((50.0, 0.0), (video.Look.Scale, video.Look.Saturation));
    }

    [Fact]
    public void Selecting_the_sound_still_changes_its_video()
    {
        var (project, tl, inspector) = Setup();
        tl.Select(project.TrackByName("A1").Clips[0], add: false);
        inspector.Rotation = 45;
        Assert.Equal(45, project.TrackByName("V1").Clips.Single(c => c.Start == 0).Look.Rotation);
    }

    [Fact]
    public void Reset_puts_everything_back_in_one_undo_step()
    {
        var (project, tl, inspector) = Setup();
        inspector.Scale = 50;
        inspector.Exposure = 1;
        inspector.ResetLookCommand.Execute(null);
        Clip video = project.TrackByName("V1").Clips.Single(c => c.Start == 0);
        Assert.Equal(ClipLook.Default, video.Look);

        tl.UndoCommand.Execute(null);
        Assert.Equal(1, project.TrackByName("V1").Clips.Single(c => c.Start == 0).Look.Exposure);
    }

    [Fact]
    public void Double_speed_halves_the_clip_and_pulls_the_next_one_closer()
    {
        var (project, tl, inspector) = Setup();
        for (double s = 110; s <= 200; s += 10) inspector.SpeedPercent = s; // dragging the slider

        var v1 = project.TrackByName("V1").Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal((150L, 200.0), (v1[0].Length, v1[0].SpeedPercent));
        Assert.Equal(150, v1[1].Start);                                      // slid left to close the gap
        Assert.Equal(150, project.TrackByName("A1").Clips[0].Length);        // the sound follows

        tl.UndoCommand.Execute(null);                                         // one undo for the whole drag
        Assert.Equal(300, project.TrackByName("V1").Clips.OrderBy(c => c.Start).First().Length);
    }

    [Fact]
    public void Pictures_and_titles_have_no_speed()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddTitleAtPlayheadCommand.Execute(null);
        var inspector = new InspectorViewModel(tl);
        Assert.False(inspector.HasSpeed);
        Assert.True(inspector.HasVisual); // but titles can be moved, turned and faded
    }

    [Fact]
    public void Reverse_applies_to_video_and_sound()
    {
        var (project, _, inspector) = Setup();
        inspector.Reverse = true;
        Assert.True(project.TrackByName("V1").Clips.Single(c => c.Start == 0).Reverse);
        Assert.True(project.TrackByName("A1").Clips[0].Reverse);
    }
}
