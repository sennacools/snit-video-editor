using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Cross-dissolve and dip to black: the plan, editing, and real exports.</summary>
public class TransitionTests(TestMedia media) : IClassFixture<TestMedia>
{
    private static MediaItem Video(double seconds) => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = seconds, FrameRate = 30,
    };

    /// <summary>Two clips meeting at frame 100. The first uses source 0–99 of a 10 s file,
    /// the second source 200–299, so both have footage to spare around the cut.</summary>
    private static (Project, TimelineViewModel, Clip A, Clip B) TwoClips()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var track = project.TrackByName("V1");
        var a = new Clip { Media = Video(10), Start = 0, In = 0, Length = 100 };
        var b = new Clip { Media = Video(10), Start = 100, In = 200, Length = 100 };
        track.Clips.AddRange([a, b]);
        return (project, tl, a, b);
    }

    [Fact]
    public void A_dissolve_uses_footage_from_beyond_the_cut()
    {
        var (project, _, a, b) = TwoClips();
        b.TransitionIn = new Transition(TransitionKind.CrossDissolve, 30);

        var plan = TransitionPlan.Expand(project.TrackByName("V1"), 30);
        RenderClip ra = plan[0], rb = plan[1];
        Assert.Equal(115, ra.Clip.End);                    // the first clip goes on 15 frames longer
        Assert.Equal((85L, 185L), (rb.Clip.Start, rb.Clip.In)); // the second starts 15 frames early
        Assert.Equal(30, rb.FadeInFrames);                 // and fades in over the full 30 frames
        Assert.Equal(0.5, rb.VisibilityAt(100), precision: 2); // halfway at the cut
        Assert.Equal((100L, 100L), (a.End, b.Start));      // the timeline itself is unchanged
    }

    [Fact]
    public void Without_spare_footage_the_clip_simply_fades_in()
    {
        var (project, _, a, b) = TwoClips();
        a.In = 200; // the first clip now ends exactly at the end of its 300-frame file
        b.TransitionIn = new Transition(TransitionKind.CrossDissolve, 30);

        var plan = TransitionPlan.Expand(project.TrackByName("V1"), 30);
        Assert.Equal(100, plan[0].Clip.End);
        Assert.Equal(100, plan[1].Clip.Start);
        Assert.Equal(30, plan[1].FadeInFrames);
    }

    [Fact]
    public void A_dip_to_black_goes_half_down_and_half_up()
    {
        var (project, _, _, b) = TwoClips();
        b.TransitionIn = new Transition(TransitionKind.DipToBlack, 30);

        var plan = TransitionPlan.Expand(project.TrackByName("V1"), 30);
        Assert.Equal(15, plan[0].BlackOutFrames);
        Assert.Equal(15, plan[1].BlackInFrames);
        Assert.Equal(0, plan[0].BrightnessAt(100), precision: 2);  // black right at the cut
        Assert.Equal(1, plan[1].BrightnessAt(120), precision: 2);  // back to normal after half
    }

    [Fact]
    public void A_transition_dropped_on_the_end_of_a_clip_at_a_cut_goes_on_the_next_clip()
    {
        var (_, tl, a, b) = TwoClips();
        tl.SetTransition(a, atStart: false, TransitionKind.CrossDissolve, 20);
        Assert.Null(a.TransitionOut);
        Assert.Equal(new Transition(TransitionKind.CrossDissolve, 20), b.TransitionIn);

        tl.UndoCommand.Execute(null);
        Assert.Null(tl.Project.TrackByName("V1").Clips.Single(c => c.Start == 100).TransitionIn);
    }

    [Fact]
    public void Cutting_a_clip_keeps_its_transitions_at_the_outer_ends()
    {
        var track = new Track { Name = "V1", Kind = TrackKind.Video };
        var clip = new Clip
        {
            Media = Video(10), Start = 0, Length = 100,
            TransitionIn = new Transition(TransitionKind.DipToBlack, 10),
            TransitionOut = new Transition(TransitionKind.CrossDissolve, 10),
        };
        track.Clips.Add(clip);
        Clip right = TimelineEdits.Split(track, clip, 50)!;
        Assert.NotNull(clip.TransitionIn);
        Assert.Null(clip.TransitionOut);
        Assert.Null(right.TransitionIn);
        Assert.NotNull(right.TransitionOut);
    }

    [Fact]
    public void Ctrl_D_adds_a_dissolve_at_the_nearest_cut()
    {
        var (_, tl, _, b) = TwoClips();
        tl.PlayheadFrame = 110;
        tl.AddDissolveAtPlayheadCommand.Execute(null);
        Assert.Equal(TransitionKind.CrossDissolve, b.TransitionIn?.Kind);
    }

    /// <summary>One frame of an export as red-green-blue bytes (320×180).</summary>
    private static async Task<(int R, int G, int B)> MiddlePixelAsync(string file, int frame)
    {
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", file, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        int i = (90 * 320 + 160) * 3;
        return (result.Output[i], result.Output[i + 1], result.Output[i + 2]);
    }

    private async Task<string> ExportWhiteThenRed(TransitionKind kind, string name)
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(await MediaImporter.ProbeAsync(media.White), project.TrackByName("V1"), 0);
        tl.AddMedia(await MediaImporter.ProbeAsync(media.Red), project.TrackByName("V1"), 150); // the cut at 5 s
        tl.SetTransition(project.TrackByName("V1").Clips.Single(c => c.Start == 150), atStart: true, kind, 30);

        string output = Path.Combine(media.Folder, name + ".mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 320, Height = 180 });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);
        return output;
    }

    [Fact]
    public async Task Exported_dissolve_blends_the_two_pictures()
    {
        if (!media.Available) return;
        string file = await ExportWhiteThenRed(TransitionKind.CrossDissolve, "dissolve");

        var before = await MiddlePixelAsync(file, 120);  // white
        var middle = await MiddlePixelAsync(file, 150);  // halfway: pink
        var after = await MiddlePixelAsync(file, 180);   // red
        Assert.True(before.G > 230, $"Expected white before, got {before}.");
        Assert.True(middle.R > 220 && middle.G is > 90 and < 170, $"Expected a pink mix at the cut, got {middle}.");
        Assert.True(after.R > 200 && after.G < 40, $"Expected red after, got {after}.");
    }

    [Fact]
    public async Task Exported_dip_to_black_is_black_at_the_cut()
    {
        if (!media.Available) return;
        string file = await ExportWhiteThenRed(TransitionKind.DipToBlack, "dip");

        var atCut = await MiddlePixelAsync(file, 150);
        var after = await MiddlePixelAsync(file, 180);
        Assert.True(atCut.R < 40 && atCut.G < 40, $"Expected black at the cut, got {atCut}.");
        Assert.True(after.R > 200, $"Expected red after, got {after}.");
    }
}
