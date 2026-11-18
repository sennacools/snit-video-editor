using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Insert and overwrite from the Source monitor.</summary>
public class SourceMonitorTests
{
    private static MediaItem Video(double seconds = 10) => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = seconds, FrameRate = 30, HasAudio = true,
    };

    /// <summary>One 200-frame clip (with its sound) on the timeline, playhead at 100.</summary>
    private static (Project, TimelineViewModel) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(200 / 30.0), project.TrackByName("V1"), 0);
        tl.PlayheadFrame = 100;
        return (project, tl);
    }

    [Fact]
    public void Insert_cuts_at_the_playhead_and_pushes_the_rest_right()
    {
        var (project, tl) = Setup();
        tl.PlaceFromSource(Video(), sourceIn: 60, length: 30, insert: true);

        var v1 = project.TrackByName("V1").Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal([(0L, 100L), (100L, 130L), (130L, 230L)], v1.Select(c => (c.Start, c.End)));
        Assert.Equal(60, v1[1].In);                                   // the marked part of the source
        Assert.Equal(3, project.TrackByName("A1").Clips.Count);       // the sound was cut and pushed too
        Assert.Equal(130, tl.PlayheadFrame);                          // ready for the next piece
    }

    [Fact]
    public void Overwrite_covers_what_is_there_without_moving_anything()
    {
        var (project, tl) = Setup();
        tl.PlaceFromSource(Video(), sourceIn: 0, length: 30, insert: false);

        var v1 = project.TrackByName("V1").Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal([(0L, 100L), (100L, 130L), (130L, 200L)], v1.Select(c => (c.Start, c.End)));
        Assert.Equal(200, project.DurationFrames); // nothing was pushed
    }

    [Fact]
    public void Insert_also_pushes_markers()
    {
        var (project, tl) = Setup();
        tl.PlayheadFrame = 150;
        tl.AddMarkerCommand.Execute(null);
        tl.PlayheadFrame = 100;
        tl.PlaceFromSource(Video(), 0, 30, insert: true);
        Assert.Equal(180, project.Markers.Single().Frame);
    }

    [Fact]
    public void The_source_monitor_places_its_marked_part()
    {
        var (project, tl) = Setup();
        using var source = new SourceMonitorViewModel(() => tl);
        source.Open(Video(), project.Settings);
        source.PlayheadFrame = 30; source.MarkIn();
        source.PlayheadFrame = 75; source.MarkOut();
        Assert.Equal("00:00:01:15", source.MarkedTimecode); // 45 frames

        source.Insert();
        Clip placed = project.TrackByName("V1").Clips.Single(c => c.Start == 100);
        Assert.Equal((30L, 45L), (placed.In, placed.Length));
    }
}
