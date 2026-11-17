using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Tests for the editing rules: placing, overwriting, cutting, trimming, undo.</summary>
public class TimelineTests
{
    private static MediaItem Video(double seconds = 10, bool audio = true) => new()
    {
        FilePath = @"C:\fake\video.mp4", Kind = MediaKind.Video, DurationSeconds = seconds,
        Width = 1920, Height = 1080, FrameRate = 30, HasAudio = audio,
    };

    private static MediaItem Picture() => new() { FilePath = @"C:\fake\pic.png", Kind = MediaKind.Image };

    private static (Project, TimelineViewModel) NewTimeline()
    {
        var project = new Project();
        return (project, new TimelineViewModel(project));
    }

    [Fact]
    public void Timecode_is_hours_minutes_seconds_frames()
    {
        Assert.Equal("00:01:05:15", Timecode.Format(65.5, 30));
        Assert.Equal("00:00:01:00", Timecode.FromFrames(30, 30));
        Assert.Equal("00:00:00:29", Timecode.FromFrames(29, 30));
    }

    [Fact]
    public void Video_with_sound_gets_a_linked_audio_clip()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(Video(), project.TrackByName("V1"), 30);

        Clip v = Assert.Single(project.TrackByName("V1").Clips);
        Clip a = Assert.Single(project.TrackByName("A1").Clips);
        Assert.Equal(30, v.Start);
        Assert.Equal(300, v.Length); // 10 s at 30 fps
        Assert.NotNull(v.LinkId);
        Assert.Equal(v.LinkId, a.LinkId);
        Assert.Equal(330, project.DurationFrames);
    }

    [Fact]
    public void Pictures_are_five_seconds_and_go_on_video_tracks()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(Picture(), project.TrackByName("A2"), 0); // dropped on an audio track
        Clip c = Assert.Single(project.TrackByName("V1").Clips);
        Assert.Equal(150, c.Length);
        Assert.Empty(project.TrackByName("A1").Clips);
    }

    [Fact]
    public void Overwrite_cuts_a_hole_in_the_middle_of_a_clip()
    {
        var track = new Track { Name = "V1", Kind = TrackKind.Video };
        var media = Video();
        var existing = new Clip { Media = media, Start = 0, In = 0, Length = 300 };
        track.Clips.Add(existing);

        TimelineEdits.ClearRange(track, 100, 150, []);

        var parts = track.Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal((0L, 100L), (parts[0].Start, parts[0].End));
        Assert.Equal((150L, 300L), (parts[1].Start, parts[1].End));
        Assert.Equal(150, parts[1].In); // the right part continues where the source was
    }

    [Fact]
    public void Overwrite_trims_heads_and_tails_and_removes_covered_clips()
    {
        var track = new Track { Name = "V1", Kind = TrackKind.Video };
        var media = Video();
        track.Clips.Add(new Clip { Media = media, Start = 0, Length = 100 });    // tail overlaps
        track.Clips.Add(new Clip { Media = media, Start = 120, Length = 20 });   // fully covered
        track.Clips.Add(new Clip { Media = media, Start = 180, Length = 100 });  // head overlaps

        TimelineEdits.ClearRange(track, 50, 200, []);

        var parts = track.Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(50, parts[0].End);
        Assert.Equal(200, parts[1].Start);
        Assert.Equal(20, parts[1].In);
        Assert.Equal(80, parts[1].Length);
    }

    [Fact]
    public void Razor_splits_both_linked_clips_and_keeps_pairs_linked()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(Video(), project.TrackByName("V1"), 0);
        Clip v = project.TrackByName("V1").Clips[0];

        tl.SplitAt(v, 90);

        var video = project.TrackByName("V1").Clips.OrderBy(c => c.Start).ToList();
        var audio = project.TrackByName("A1").Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal(2, video.Count);
        Assert.Equal(2, audio.Count);
        Assert.Equal(90, video[1].Start);
        Assert.Equal(90, video[1].In);
        Assert.Equal(video[0].LinkId, audio[0].LinkId);
        Assert.Equal(video[1].LinkId, audio[1].LinkId);
        Assert.NotEqual(video[0].LinkId, video[1].LinkId);
    }

    [Fact]
    public void Undo_and_redo_restore_the_timeline()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(Video(), project.TrackByName("V1"), 0);
        tl.SplitAt(project.TrackByName("V1").Clips[0], 100);
        Assert.Equal(2, project.TrackByName("V1").Clips.Count);

        tl.UndoCommand.Execute(null);
        Assert.Single(project.TrackByName("V1").Clips);

        tl.UndoCommand.Execute(null);
        Assert.Empty(project.TrackByName("V1").Clips);

        tl.RedoCommand.Execute(null);
        tl.RedoCommand.Execute(null);
        Assert.Equal(2, project.TrackByName("V1").Clips.Count);
    }

    [Fact]
    public void Moving_a_clip_onto_another_overwrites_it()
    {
        var (project, tl) = NewTimeline();
        var v1 = project.TrackByName("V1");
        tl.AddMedia(Video(2, audio: false), v1, 0);    // frames 0-60
        tl.AddMedia(Video(2, audio: false), v1, 200);  // frames 200-260
        Clip second = v1.Clips.Single(c => c.Start == 200);

        tl.SnappingEnabled = false;
        tl.Select(second, add: false);
        tl.BeginMove(second, grabFrame: 200);
        tl.UpdateMove(30, v1);    // drag 170 frames left: now 30-90, covering the end of the first clip
        tl.EndMove();

        var clips = v1.Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal(2, clips.Count);
        Assert.Equal(30, clips[0].End);   // first clip shortened
        Assert.Equal(30, clips[1].Start); // moved clip
    }

    [Fact]
    public void Trimming_stops_at_the_end_of_the_source_file()
    {
        var (project, tl) = NewTimeline();
        var v1 = project.TrackByName("V1");
        tl.AddMedia(Video(2, audio: false), v1, 0); // 60 frames, the whole file
        Clip clip = v1.Clips[0];

        tl.BeginTrim(clip, leftEdge: false, ripple: false);
        tl.UpdateTrim(500); // try to stretch way past the end
        tl.EndTrim();
        Assert.Equal(60, clip.Length);

        tl.BeginTrim(clip, leftEdge: true, ripple: false);
        tl.UpdateTrim(20);
        tl.EndTrim();
        Assert.Equal((20L, 20L, 40L), (clip.Start, clip.In, clip.Length));
    }

    [Fact]
    public void Ripple_delete_closes_the_gap()
    {
        var (project, tl) = NewTimeline();
        var v1 = project.TrackByName("V1");
        tl.AddMedia(Video(2, audio: false), v1, 0);
        tl.AddMedia(Video(2, audio: false), v1, 60);
        tl.AddMedia(Video(2, audio: false), v1, 120);

        tl.Select(v1.Clips.Single(c => c.Start == 60), add: false);
        tl.RippleDeleteSelectionCommand.Execute(null);

        Assert.Equal([0L, 60L], v1.Clips.Select(c => c.Start).Order());
    }

    [Fact]
    public void Preview_plan_shows_the_top_track_and_merges_continuous_pieces()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(Video(10, audio: false), project.TrackByName("V1"), 0);   // 0-300
        tl.AddMedia(Picture(), project.TrackByName("V2"), 100);               // 100-250 on top

        var segments = PreviewPlan.BuildVideo(project);
        Assert.Equal(3, segments.Count);
        Assert.Equal(MediaKind.Image, segments[1].Kind);
        Assert.Equal(250, segments[2].Start);
        Assert.Equal(250, segments[2].SourceStart);

        project.TrackByName("V2").IsOff = true; // hide V2: one continuous piece again
        Assert.Single(PreviewPlan.BuildVideo(project));
    }
}
