using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Keyframes: values in between, editing in the inspector, cutting, saving and exporting.</summary>
public class KeyframeTests(TestMedia media) : IClassFixture<TestMedia>
{
    [Fact]
    public void Values_change_evenly_between_keyframes()
    {
        IReadOnlyList<Keyframe> keys = [new(10, 100), new(30, 50)];
        Assert.Equal(100, Keyframes.ValueAt(keys, 0));   // before the first: stays
        Assert.Equal(75, Keyframes.ValueAt(keys, 20));   // halfway
        Assert.Equal(50, Keyframes.ValueAt(keys, 99));   // after the last: stays
    }

    private static (Project, TimelineViewModel, InspectorViewModel, Clip) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 10, FrameRate = 30 };
        project.Media.Add(video);
        tl.AddMedia(video, project.TrackByName("V1"), 100); // starts at frame 100, selected
        return (project, tl, new InspectorViewModel(tl), project.TrackByName("V1").Clips[0]);
    }

    [Fact]
    public void The_diamond_starts_an_animation_and_the_slider_then_sets_keyframes()
    {
        var (_, tl, inspector, clip) = Setup();
        tl.PlayheadFrame = 100;
        inspector.ToggleKeyframeCommand.Execute("Scale");            // keyframe at the clip start: 100%
        Assert.Equal("◆", inspector.Keys["Scale"].Glyph);

        tl.PlayheadFrame = 160;
        Assert.Equal("◇", inspector.Keys["Scale"].Glyph);            // animated, but no keyframe here
        inspector.Scale = 40;                                         // sets a second keyframe
        Assert.Equal(2, clip.Keyframes["Scale"].Count);

        tl.PlayheadFrame = 130;
        Assert.Equal(70, inspector.Scale);                            // halfway between 100 and 40
        Assert.Equal(70, clip.LookAt(130).Scale);
    }

    [Fact]
    public void Removing_the_last_keyframe_keeps_its_value()
    {
        var (_, tl, inspector, clip) = Setup();
        tl.PlayheadFrame = 120;
        inspector.Rotation = 30;                                      // not animated: a normal setting
        inspector.ToggleKeyframeCommand.Execute("Rotation");          // animate from 30°
        inspector.ToggleKeyframeCommand.Execute("Rotation");          // and remove it again
        Assert.False(clip.Keyframes.ContainsKey("Rotation"));
        Assert.Equal(30, clip.Look.Rotation);
    }

    [Fact]
    public void Trimming_the_start_keeps_the_animation_on_the_same_picture()
    {
        var (project, tl, _, clip) = Setup();
        clip.Keyframes = Keyframes.Set(Keyframes.None, "Opacity", 60, 0); // at timeline frame 160
        TimelineEdits.MoveEdges(clip, 20, 0);                            // clip now starts at 120
        Assert.Equal(40, clip.Keyframes["Opacity"][0].Frame);            // still at timeline frame 160

        Clip right = TimelineEdits.Split(project.TrackByName("V1"), clip, 150)!;
        Assert.Equal(10, right.Keyframes["Opacity"][0].Frame);           // 150 + 10 = 160
    }

    [Fact]
    public void Keyframes_are_saved_and_undone()
    {
        var (project, tl, inspector, clip) = Setup();
        tl.PlayheadFrame = 100;
        inspector.ToggleKeyframeCommand.Execute("PositionX");

        string path = Path.Combine(Path.GetTempPath(), $"snit-kf-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Assert.Single(ProjectFile.Load(path).TrackByName("V1").Clips[0].Keyframes["PositionX"]);
        }
        finally { File.Delete(path); }

        tl.UndoCommand.Execute(null);
        Assert.Empty(project.TrackByName("V1").Clips[0].Keyframes);
    }

    [Fact]
    public void Keyframe_expressions_go_evenly_from_one_value_to_the_next()
    {
        string expr = VideoFilters.Piecewise([new(0, 100), new(30, 50)], 30, "t");
        Assert.Equal("if(lt(t,0),100,if(lt(t,1),100+(-50)*(t-0)/1,50))", expr);
    }

    /// <summary>A white picture whose opacity goes 0 → 100 over 2 seconds, and that slides right.</summary>
    [Fact]
    public async Task Animated_opacity_and_position_end_up_in_the_export()
    {
        if (!media.Available) return;
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(await MediaImporter.ProbeAsync(media.White), project.TrackByName("V1"), 0); // 0–150
        Clip clip = project.TrackByName("V1").Clips[0];
        clip.Look = clip.Look with { Scale = 25 };
        var keys = Keyframes.Set(Keyframes.None, "Opacity", 0, 0);
        keys = Keyframes.Set(keys, "Opacity", 60, 100);
        keys = Keyframes.Set(keys, "PositionX", 0, -600);
        keys = Keyframes.Set(keys, "PositionX", 60, 600);
        clip.Keyframes = keys;

        string output = Path.Combine(media.Folder, "keyframes.mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 320, Height = 180 });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);

        async Task<int> BrightnessAt(int frame, int x)
        {
            var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
                ["-v", "error", "-i", output, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
            return result.Output[90 * 320 + x];
        }

        // Frame 30: halfway, so half see-through and in the middle (x = 160).
        Assert.InRange(await BrightnessAt(30, 160), 95, 160);
        // Frame 60 and later: solid white, moved right by 600 project pixels = 100 export pixels (x = 260).
        Assert.True(await BrightnessAt(90, 260) > 220, "Expected the white box on the right at the end.");
        Assert.True(await BrightnessAt(90, 160) < 30, "The middle should be black once the box has moved.");
    }
}
