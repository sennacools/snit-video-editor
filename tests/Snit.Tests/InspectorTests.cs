using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

public class InspectorTests
{
    private static (Project, TimelineViewModel, InspectorViewModel) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var video = new MediaItem
        {
            FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 10, FrameRate = 30, HasAudio = true,
        };
        tl.AddMedia(video, project.TrackByName("V1"), 0); // also selects the video and its sound
        return (project, tl, new InspectorViewModel(tl));
    }

    [Fact]
    public void Selecting_a_video_controls_its_linked_sound()
    {
        var (project, _, inspector) = Setup();
        Assert.True(inspector.HasAudio);

        inspector.VolumeDb = -6;
        Assert.Equal(-6, project.TrackByName("A1").Clips[0].VolumeDb);
    }

    [Fact]
    public void A_whole_slider_drag_is_one_undo_step()
    {
        var (project, tl, inspector) = Setup();
        for (double v = -1; v >= -10; v--) inspector.VolumeDb = v; // many small changes while dragging

        tl.UndoCommand.Execute(null);
        Assert.Equal(0, project.TrackByName("A1").Clips[0].VolumeDb);
        Assert.Equal(0, inspector.VolumeDb);

        tl.UndoCommand.Execute(null); // the next undo removes the clips: proves there was only one volume step
        Assert.Empty(project.TrackByName("A1").Clips);
    }

    [Fact]
    public void Nothing_selected_means_no_audio_controls()
    {
        var (_, tl, inspector) = Setup();
        tl.ClearSelection();
        Assert.False(inspector.HasAudio);
        Assert.Equal("No clip selected", inspector.ClipName);
    }
}
