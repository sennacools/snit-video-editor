using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Checks the ffmpeg command Snit builds for an export.</summary>
public class ExportTests
{
    private static MediaItem FakeVideo(string path = @"C:\fake\a.mp4") => new()
    {
        FilePath = path, Kind = MediaKind.Video, DurationSeconds = 10, Width = 1280, Height = 720, FrameRate = 30, HasAudio = true,
    };

    [Fact]
    public void Command_cuts_each_clip_and_places_it_in_time()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(FakeVideo(), project.TrackByName("V1"), 30);   // starts at 1 s, with linked audio
        tl.SplitAt(project.TrackByName("V1").Clips[0], 90);         // cut at 3 s

        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = "out.mp4" });
        string args = string.Join(" ", plan.Arguments);

        Assert.Equal(330, plan.TotalFrames);                  // 11 s at 30 fps
        Assert.Contains("-ss 2 -t 8 -i", args);               // right piece: source from 2 s, 8 s long
        Assert.Contains("setpts=PTS-STARTPTS+3/TB", plan.FilterGraph);
        Assert.Contains("adelay=1000:all=1", plan.FilterGraph);
        Assert.Contains("amix=inputs=2", plan.FilterGraph);
        Assert.EndsWith("out.mp4", args);
    }

    [Fact]
    public void Hidden_and_muted_tracks_are_left_out()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(FakeVideo(), project.TrackByName("V2"), 0);
        project.TrackByName("V2").IsOff = true;
        project.TrackByName("A2").IsOff = true;

        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = "out.mp4" });
        Assert.DoesNotContain("-i", plan.Arguments);   // no inputs at all
        Assert.Contains("anullsrc", plan.FilterGraph); // silent sound track
    }

    [Fact]
    public void Numbers_use_dots_even_on_a_Dutch_computer()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nl-BE");
        try
        {
            var project = new Project();
            var tl = new TimelineViewModel(project);
            tl.AddMedia(FakeVideo(), project.TrackByName("V1"), 15); // 0.5 s
            var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = "o.mp4", VideoBitrateMbps = 12.5 });
            Assert.Contains("+0.5/TB", plan.FilterGraph);
            Assert.Contains("12.5M", plan.Arguments);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Empty_timeline_cannot_be_exported()
        => Assert.Throws<InvalidOperationException>(() =>
            ExportCommandBuilder.Build(new Project(), new ExportSettings { OutputPath = "o.mp4" }));
}
