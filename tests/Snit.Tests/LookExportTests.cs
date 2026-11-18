using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>
/// Real exports with colour, transform, speed and reverse, checked by reading the pixels
/// back out of the video. All at 320×180 so they are quick. Skipped without FFmpeg.
/// </summary>
public class LookExportTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const int W = 320, H = 180;

    private async Task<(Project, TimelineViewModel, Clip)> ProjectWith(string file, string track = "V1")
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        MediaItem item = await MediaImporter.ProbeAsync(file);
        tl.AddMedia(item, project.TrackByName(track), 0);
        return (project, tl, project.TrackByName(track).Clips[0]);
    }

    private async Task<string> ExportAsync(Project project, string name)
    {
        string output = Path.Combine(media.Folder, name + ".mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = W, Height = H });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);
        return output;
    }

    /// <summary>One frame as red-green-blue bytes.</summary>
    private static async Task<byte[]> FrameAsync(string file, int frame)
    {
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", file, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        Assert.Equal(W * H * 3, result.Output.Length);
        return result.Output;
    }

    private static (int R, int G, int B) Pixel(byte[] rgb, int x, int y)
    {
        int i = (y * W + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    [Fact]
    public async Task Half_opacity_white_on_black_is_grey()
    {
        if (!media.Available) return;
        var (project, _, clip) = await ProjectWith(media.White);
        clip.Look = clip.Look with { Opacity = 50 };

        var (r, g, b) = Pixel(await FrameAsync(await ExportAsync(project, "opacity"), 5), W / 2, H / 2);
        Assert.InRange(r, 110, 145);
        Assert.InRange(g, 110, 145);
        Assert.InRange(b, 110, 145);
    }

    [Fact]
    public async Task Zero_saturation_leaves_no_colour()
    {
        if (!media.Available) return;
        var (project, _, clip) = await ProjectWith(media.Video30);
        clip.Look = clip.Look with { Saturation = 0 };

        byte[] frame = await FrameAsync(await ExportAsync(project, "grey"), 10);
        int maxDifference = 0;
        for (int i = 0; i < frame.Length; i += 3)
            maxDifference = Math.Max(maxDifference, Math.Max(Math.Abs(frame[i] - frame[i + 1]), Math.Abs(frame[i + 1] - frame[i + 2])));
        Assert.True(maxDifference < 25, $"Still colour in the picture (difference {maxDifference}).");
    }

    [Fact]
    public async Task Half_size_leaves_black_around_it_and_position_moves_it()
    {
        if (!media.Available) return;
        var (project, _, clip) = await ProjectWith(media.White);
        // Half size, moved 480 project pixels right (a quarter of 1920): the centre of the white
        // box lands at 3/4 of the width.
        clip.Look = clip.Look with { Scale = 50, PositionX = 480 };

        byte[] frame = await FrameAsync(await ExportAsync(project, "small"), 5);
        Assert.True(Pixel(frame, 3 * W / 4, H / 2).R > 200, "The white box should be at 3/4 of the width.");
        Assert.True(Pixel(frame, W / 4, H / 2).R < 40, "The left side should be black.");
        Assert.True(Pixel(frame, 3 * W / 4, 5).R < 40, "Above the half-size box should be black.");
    }

    [Fact]
    public async Task Double_speed_halves_the_length()
    {
        if (!media.Available) return;
        var (project, tl, clip) = await ProjectWith(media.Video30); // 4 s with sound
        foreach (Clip c in project.LinkedGroup(clip).ToList())
        {
            c.SpeedPercent = 200;
            c.Length = 60; // 2 s
        }

        MediaItem result = await MediaImporter.ProbeAsync(await ExportAsync(project, "fast"));
        Assert.InRange(result.DurationSeconds, 1.95, 2.1);
        Assert.True(result.HasAudio);
    }

    [Fact]
    public async Task Reverse_starts_with_the_last_picture()
    {
        if (!media.Available) return;
        var (normal, _, _) = await ProjectWith(media.Video25, "V1");
        var (reversed, _, clip) = await ProjectWith(media.Video25, "V1");
        clip.Reverse = true;

        int lastFrame = (int)(normal.DurationFrames - 1);
        byte[] endOfNormal = await FrameAsync(await ExportAsync(normal, "normal"), lastFrame);
        byte[] startOfReversed = await FrameAsync(await ExportAsync(reversed, "reversed"), 0);
        byte[] startOfNormal = await FrameAsync(Path.Combine(media.Folder, "normal.mp4"), 0);

        double Difference(byte[] a, byte[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
        Assert.True(Difference(startOfReversed, endOfNormal) < Difference(startOfReversed, startOfNormal),
            "The reversed clip should start with (roughly) the last picture of the normal one.");
        Assert.True(Difference(startOfReversed, endOfNormal) < 12);
    }

    [Fact]
    public void Speed_filters_use_atempo_in_steps()
    {
        Assert.Equal(",atempo=2,atempo=2", VideoFilters.AudioSpeed(4, false));
        Assert.Equal(",areverse,atempo=0.5,atempo=0.5", VideoFilters.AudioSpeed(0.25, true));
        Assert.Equal("", VideoFilters.AudioSpeed(1, false));
        Assert.Equal(",reverse,setpts=(PTS-STARTPTS)/2", VideoFilters.VideoSpeed(2, true));
    }

    [Fact]
    public void Colour_settings_become_filters()
    {
        Assert.Equal("", VideoFilters.Color(ClipLook.Default));
        string warm = VideoFilters.Color(ClipLook.Default with { Exposure = 1, Temperature = 100 });
        Assert.StartsWith(",colorchannelmixer=rr=2.3:", warm);   // twice as bright, extra red
        Assert.Contains("bb=1.7", warm);                          // less blue
        Assert.Equal(",eq=contrast=1.4:saturation=1.5", VideoFilters.Color(ClipLook.Default with { Contrast = 50, Saturation = 150 }));
    }
}
