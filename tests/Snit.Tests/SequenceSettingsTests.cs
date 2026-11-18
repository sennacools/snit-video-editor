using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Timeline → Sequence settings: a new size and frame rate keep clips in their place in time.</summary>
public class SequenceSettingsTests
{
    [Fact]
    public void A_new_frame_rate_converts_every_frame_number()
    {
        var project = new Project();
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 10, FrameRate = 30 };
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 60);        // starts at 2 s
        Clip clip = project.TrackByName("V1").Clips[0];
        clip.In = 30; clip.Length = 90;                           // 1 s into the file, 3 s long
        clip.TransitionIn = new Transition(TransitionKind.CrossDissolve, 15);
        clip.Keyframes = Keyframes.Set(Keyframes.None, "Opacity", 30, 50);
        project.Markers = [new Marker(120, "Here")];
        project.InPoint = 30;

        SequenceSettings.Apply(project, 1920, 1080, 25);

        Assert.Equal(25, project.Fps);
        Assert.Equal((50L, 25L, 75L), (clip.Start, clip.In, clip.Length));  // 2 s, 1 s, 3 s at 25 fps
        Assert.Equal(13, clip.TransitionIn!.Frames);                       // half a second (rounded)
        Assert.Equal(25, clip.Keyframes["Opacity"][0].Frame);
        Assert.Equal(100, project.Markers[0].Frame);
        Assert.Equal(25, project.InPoint);
    }

    [Fact]
    public void A_new_size_moves_positions_along_and_stays_even()
    {
        var project = new Project();
        var clip = new Clip { Media = MediaItem.Title, Length = 30, Look = ClipLook.Default with { PositionX = 100, PositionY = -50 } };
        project.TrackByName("V1").Clips.Add(clip);

        SequenceSettings.Apply(project, 3841, 2160, 30);

        Assert.Equal((3840, 2160), (project.Settings.Width, project.Settings.Height));
        Assert.Equal((200.0, -100.0), (clip.Look.PositionX, clip.Look.PositionY));
        Assert.Equal(30, clip.Length);
    }
}
