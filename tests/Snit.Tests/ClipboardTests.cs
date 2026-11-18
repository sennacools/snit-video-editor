using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

public class ClipboardTests
{
    private static MediaItem Video() => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 2, FrameRate = 30, HasAudio = true,
    };

    [Fact]
    public void Paste_puts_copies_at_the_playhead_on_the_same_tracks()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(), project.TrackByName("V2"), 30);   // V2 + A2, 30–90, selected
        tl.CopyCommand.Execute(null);

        tl.PlayheadFrame = 200;
        tl.PasteCommand.Execute(null);

        Clip copy = project.TrackByName("V2").Clips.Single(c => c.Start == 200);
        Clip copySound = project.TrackByName("A2").Clips.Single(c => c.Start == 200);
        Assert.Equal(copy.LinkId, copySound.LinkId);                                       // still a pair
        Assert.NotEqual(project.TrackByName("V2").Clips.Single(c => c.Start == 30).LinkId, copy.LinkId); // a new pair
        Assert.Equal(260, tl.PlayheadFrame);                                               // playhead after the paste
    }

    [Fact]
    public void Cut_removes_and_paste_brings_back_in_one_undo_step_each()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(), project.TrackByName("V1"), 0);
        tl.CutCommand.Execute(null);
        Assert.Empty(project.TrackByName("V1").Clips);

        tl.PlayheadFrame = 90;
        tl.PasteCommand.Execute(null);
        Assert.Equal(90, project.TrackByName("V1").Clips.Single().Start);

        tl.UndoCommand.Execute(null);
        Assert.Empty(project.TrackByName("V1").Clips);
    }

    [Fact]
    public void Box_select_picks_clips_and_their_partners()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(), project.TrackByName("V1"), 0);
        tl.AddMedia(Video(), project.TrackByName("V1"), 100);
        tl.ClearSelection();

        tl.SelectClips([project.TrackByName("V1").Clips.Single(c => c.Start == 100)], []);
        Assert.Equal(2, tl.Selection.Count); // the video and its sound
    }
}
