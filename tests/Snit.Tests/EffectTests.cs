using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Effects: their FFmpeg recipes, saving, and real exports checked pixel by pixel.</summary>
public class EffectTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const int W = 320, H = 180;

    private static ClipEffect Effect(string kind, params (string Name, double Value)[] values)
    {
        ClipEffect e = EffectCatalog.Find(kind)!.Create();
        foreach (var (name, value) in values) e = e.With(name, value);
        return e;
    }

    [Fact]
    public void Recipes_follow_the_effect_list_in_order_and_skip_switched_off_effects()
    {
        var effects = new List<ClipEffect>
        {
            Effect("flip"),
            Effect("blur", ("amount", 10)) with { Enabled = false },
            Effect("vignette", ("amount", 100)),
        };
        Assert.Equal(",hflip,vignette=angle=1.5708", EffectFilters.Chain(effects));
    }

    [Fact]
    public void A_LUT_path_is_escaped_for_FFmpeg()
    {
        string cube = Path.Combine(Path.GetTempPath(), $"snit-look-{Guid.NewGuid():N}.cube");
        File.WriteAllText(cube, "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
        try
        {
            string chain = EffectFilters.Chain([Effect("lut") with { Text = cube }]);
            Assert.StartsWith(",lut3d=file='", chain);
            Assert.Contains("\\:/", chain);    // C\:/Users/...
            Assert.DoesNotContain("\\\\", chain);
        }
        finally { File.Delete(cube); }
    }

    [Fact]
    public void Effects_are_saved_and_opened_again()
    {
        var project = new Project();
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 5, FrameRate = 30 };
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        tl.AddEffect(project.TrackByName("V1").Clips[0], "chromakey");

        string path = Path.Combine(Path.GetTempPath(), $"snit-fx-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            ClipEffect loaded = ProjectFile.Load(path).TrackByName("V1").Clips[0].Effects.Single();
            Assert.Equal(("chromakey", "#00FF00", 30.0), (loaded.Kind, loaded.Text, loaded["similarity"]));
        }
        finally { File.Delete(path); }
    }

    // ---- Real exports ----

    private async Task<byte[]> ExportFrame(Action<Project, TimelineViewModel> build, string name, int frame = 5)
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        build(project, tl);
        string output = Path.Combine(media.Folder, name + ".mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = W, Height = H });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", output, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return result.Output;
    }

    private static (int R, int G, int B) Px(byte[] rgb, int x, int y) { int i = (y * W + x) * 3; return (rgb[i], rgb[i + 1], rgb[i + 2]); }

    [Fact]
    public async Task Crop_makes_the_cut_part_see_through()
    {
        if (!media.Available) return;
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        byte[] f = await ExportFrame((p, tl) =>
        {
            tl.AddMedia(white, p.TrackByName("V1"), 0);
            p.TrackByName("V1").Clips[0].Effects = [Effect("crop", ("left", 40))];
        }, "crop");
        Assert.True(Px(f, 40, 90).R < 30, "The cropped left part should show the black below.");
        Assert.True(Px(f, 250, 90).R > 220, "The rest should still be white.");
    }

    [Fact]
    public async Task Green_screen_lets_the_track_below_show_through()
    {
        if (!media.Available) return;
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        MediaItem red = await MediaImporter.ProbeAsync(media.Red);
        byte[] f = await ExportFrame((p, tl) =>
        {
            tl.AddMedia(white, p.TrackByName("V1"), 0);
            tl.AddMedia(red, p.TrackByName("V2"), 0);
            p.TrackByName("V2").Clips[0].Effects = [Effect("chromakey") with { Text = "#FF0000" }]; // key out the red
        }, "key");
        var (r, g, b) = Px(f, 160, 90);
        Assert.True(r > 220 && g > 220 && b > 220, $"Expected the white below to show, got {(r, g, b)}.");
    }

    [Fact]
    public async Task Flip_mirrors_left_and_right()
    {
        if (!media.Available) return;
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        byte[] normal = await ExportFrame((p, tl) => tl.AddMedia(video, p.TrackByName("V1"), 0), "normal-flip");
        byte[] flipped = await ExportFrame((p, tl) =>
        {
            tl.AddMedia(video, p.TrackByName("V1"), 0);
            p.TrackByName("V1").Clips.Single(c => p.TrackOf(c).Kind == TrackKind.Video).Effects = [Effect("flip")];
        }, "flipped");

        // The test pattern's first colour bar (left) should now be on the right.
        var left = Px(normal, 15, 100);
        var mirrored = Px(flipped, W - 16, 100);
        Assert.True(Math.Abs(left.R - mirrored.R) + Math.Abs(left.G - mirrored.G) + Math.Abs(left.B - mirrored.B) < 60,
            $"Expected {left} on the right after flipping, got {mirrored}.");
    }

    [Fact]
    public async Task Blur_softens_a_sharp_edge()
    {
        if (!media.Available) return;
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        // "Edginess": the steepest step between two neighbouring pixels along a row of the colour
        // bars. A sharp edge jumps in one step; a blurred one climbs gradually.
        async Task<long> Edginess(bool blurred)
        {
            byte[] f = await ExportFrame((p, tl) =>
            {
                tl.AddMedia(video, p.TrackByName("V1"), 0);
                Clip c = p.TrackByName("V1").Clips.Single(x => p.TrackOf(x).Kind == TrackKind.Video);
                if (blurred) c.Effects = [Effect("blur", ("amount", 40))];
            }, blurred ? "blur" : "sharp");
            long steepest = 0;
            for (int x = 1; x < W; x++)
            {
                var (r1, g1, b1) = Px(f, x - 1, 60);
                var (r2, g2, b2) = Px(f, x, 60);
                steepest = Math.Max(steepest, Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2));
            }
            return steepest;
        }
        long sharp = await Edginess(false), soft = await Edginess(true);
        Assert.True(soft < sharp * 0.5, $"Blur should soften the edges (sharp {sharp}, blurred {soft}).");
    }
}
