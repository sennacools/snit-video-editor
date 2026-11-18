using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Speed and reverse: which source frames are shown, and cutting/trimming such clips.</summary>
public class SpeedTests
{
    private static MediaItem Video(double seconds = 10) => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = seconds, FrameRate = 30,
    };

    /// <summary>A clip using source frames 100–199 (100 frames) shown at the given speed.</summary>
    private static Clip ClipAt(double speedPercent, bool reverse = false) => new()
    {
        Media = Video(), Start = 0, In = 100, SpeedPercent = speedPercent, Reverse = reverse,
        Length = (long)Math.Round(100 / (speedPercent / 100)),
    };

    [Fact]
    public void Double_speed_uses_every_second_frame()
    {
        Clip clip = ClipAt(200);
        Assert.Equal(50, clip.Length);
        Assert.Equal(100, clip.SourceLength);
        Assert.Equal(100, clip.SourceFrameAt(0));
        Assert.Equal(102, clip.SourceFrameAt(1));
        Assert.Equal(198, clip.SourceFrameAt(49));
    }

    [Fact]
    public void Reverse_starts_at_the_end_of_the_source()
    {
        Clip clip = ClipAt(100, reverse: true);
        Assert.Equal(199, clip.SourceFrameAt(0));
        Assert.Equal(100, clip.SourceFrameAt(99));
    }

    [Fact]
    public void Cutting_a_sped_up_clip_splits_the_source_at_the_right_place()
    {
        var track = new Track { Name = "V1", Kind = TrackKind.Video };
        Clip clip = ClipAt(200);
        track.Clips.Add(clip);

        Clip right = TimelineEdits.Split(track, clip, 25)!; // halfway on the timeline

        Assert.Equal((100L, 50L), (clip.In, clip.SourceLength));   // first half of the source
        Assert.Equal((150L, 50L), (right.In, right.SourceLength)); // second half
        Assert.Equal(150, right.SourceFrameAt(25));
    }

    [Fact]
    public void Cutting_a_reversed_clip_keeps_the_picture_the_same()
    {
        var track = new Track { Name = "V1", Kind = TrackKind.Video };
        Clip clip = ClipAt(100, reverse: true);
        track.Clips.Add(clip);
        long[] before = Enumerable.Range(0, 100).Select(f => clip.SourceFrameAt(f)).ToArray();

        Clip right = TimelineEdits.Split(track, clip, 40)!;

        long[] after = Enumerable.Range(0, 100).Select(f => f < 40 ? clip.SourceFrameAt(f) : right.SourceFrameAt(f)).ToArray();
        Assert.Equal(before, after); // the same frame at every moment, just in two clips now
    }

    [Fact]
    public void A_reversed_clip_can_grow_at_its_start_into_the_end_of_the_file()
    {
        // Source frames 100–199 of a 300-frame file, reversed: its start shows frame 199,
        // so it can grow 100 frames to the left (frames 200–299) but its end only 100 (frames 0–99).
        Clip clip = ClipAt(100, reverse: true);
        clip.Start = 500;
        var (before, after) = TimelineEdits.RoomToGrow(clip, 30);
        Assert.Equal((100L, 100L), (before, after));

        clip.In = 50; // now frames 50–149: 150 unused after it in the file, 50 before
        (before, after) = TimelineEdits.RoomToGrow(clip, 30);
        Assert.Equal((150L, 50L), (before, after));
    }

    [Fact]
    public void Trimming_a_sped_up_clip_stops_at_the_end_of_the_file()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(Video(10), project.TrackByName("V1"), 0); // 300 frames
        Clip clip = project.TrackByName("V1").Clips[0];
        clip.SpeedPercent = 200;
        clip.Length = 150; // the whole file at double speed

        tl.SnappingEnabled = false;
        tl.BeginTrim(clip, leftEdge: false, ripple: false);
        tl.UpdateTrim(400);
        tl.EndTrim();
        Assert.Equal(150, clip.Length); // can't go further: no more file
    }

    [Fact]
    public void Pictures_can_be_stretched_at_their_start_too()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var picture = new MediaItem { FilePath = @"C:\fake\p.png", Kind = MediaKind.Image };
        tl.AddMedia(picture, project.TrackByName("V1"), 300);
        Clip clip = project.TrackByName("V1").Clips[0];

        tl.SnappingEnabled = false;
        tl.BeginTrim(clip, leftEdge: true, ripple: false);
        tl.UpdateTrim(100);
        tl.EndTrim();
        Assert.Equal(100, clip.Start);
    }

    [Fact]
    public void Look_speed_and_reverse_are_saved()
    {
        var project = new Project();
        MediaItem video = Video();
        project.Media.Add(video);
        new TimelineViewModel(project).AddMedia(video, project.TrackByName("V1"), 0);
        Clip clip = project.TrackByName("V1").Clips[0];
        clip.Look = clip.Look with { Scale = 50, Rotation = 15, Saturation = 0, PositionX = -200 };
        clip.SpeedPercent = 50;
        clip.Reverse = true;

        string path = Path.Combine(Path.GetTempPath(), $"snit-look-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Clip loaded = ProjectFile.Load(path).TrackByName("V1").Clips[0];
            Assert.Equal(clip.Look, loaded.Look);
            Assert.Equal((50.0, true), (loaded.SpeedPercent, loaded.Reverse));
        }
        finally { File.Delete(path); }
    }
}
