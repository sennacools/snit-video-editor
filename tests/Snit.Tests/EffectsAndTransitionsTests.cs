using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Phase 11: the full effects and transitions catalog.</summary>
public class EffectsAndTransitionsTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const int W = 96, H = 54;

    // ---- The catalogs ----

    [Fact]
    public void Every_transition_kind_is_in_the_catalog_once()
    {
        Assert.Equal(Enum.GetValues<TransitionKind>().Order(), TransitionCatalog.All.Select(t => t.Kind).Order());
        Assert.All(TransitionCatalog.All, t => Assert.Contains(t.Category, TransitionCatalog.Categories));
        Assert.All(EffectCatalog.All, e => Assert.Contains(e.Category, EffectCatalog.Categories));
        Assert.Equal(EffectCatalog.All.Count, EffectCatalog.All.Select(e => e.Kind).Distinct().Count());
        Assert.True(EffectCatalog.All.Count >= 60, $"Only {EffectCatalog.All.Count} effects.");
    }

    // ---- Formulas: the preview works out the same numbers as FFmpeg ----

    [Theory]
    [InlineData("clip((0.5*(1+0.03)-(X/W))/0.03,0,1)", 10, 5, 96, 54, 1)]
    [InlineData("mod(-7,3)", 0, 0, 1, 1, 2)]
    [InlineData("if(gt(X,5),2,3)+hypot(3,4)", 6, 0, 1, 1, 7)]
    [InlineData("-2^2+between(Y,1,2)", 0, 1.5, 1, 1, -3)]
    public void Formulas_work_like_FFmpeg(string formula, double x, double y, double w, double h, double expected)
        => Assert.Equal(expected, Formula.Evaluate(formula, x, y, w, h), 6);

    /// <summary>Runs a check for many items side by side, then fails once with every problem found.</summary>
    private static async Task CheckAll<T>(IEnumerable<T> items, Func<T, Task<string?>> check)
    {
        using var gate = new SemaphoreSlim(8);
        var problems = await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync();
            try { return await check(item); }
            catch (Exception ex) { return $"{item}: {ex.Message}"; }
            finally { gate.Release(); }
        }));
        var failed = problems.Where(p => p is not null).ToList();
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    [Fact]
    public async Task Every_wipe_mask_in_the_preview_matches_FFmpeg()
    {
        if (!media.Available) return;
        await CheckAll(TransitionCatalog.All.Where(t => t.Style == TransitionStyle.Reveal), async def =>
        {
            string mask = Reveal.Appeared(def.Shape!(0), "0.5", def.Feather);
            var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            [
                "-v", "error", "-f", "lavfi", "-i", $"color=c=gray:s={W}x{H}", "-frames:v", "1",
                "-vf", $"format=gray,geq=lum='255*({mask})'", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1",
            ]);
            if (result.Output.Length != W * H) return $"{def.Kind}: FFmpeg failed: {result.Errors}";

            Formula.Compiled ours = Formula.Compile(mask);
            int different = 0;
            for (int yy = 0; yy < H; yy++)
            for (int xx = 0; xx < W; xx++)
            {
                double expected = 255 * Math.Clamp(ours(xx, yy, W, H, 0, 0, 0), 0, 1);
                if (Math.Abs(expected - result.Output[yy * W + xx]) > 3) different++;
            }
            return different <= W * H / 100 ? null : $"{def.Kind}: {different} of {W * H} pixels differ from FFmpeg.";
        });
    }

    [Fact]
    public void Transition_states_move_zoom_and_fade_the_right_way()
    {
        var slide = new Transition(TransitionKind.Slide, 30, Direction: 0);
        TransitionState first = TransitionFormulas.At(slide, 30, null, 0, 100, 200, 100);
        Assert.InRange(first.ShiftX, -1, -0.95);                 // starts just left of the frame
        Assert.Equal(TransitionState.None, TransitionFormulas.At(slide, 30, null, 0, 100, 200, 140)); // done

        var push = new Transition(TransitionKind.Push, 30, Direction: 2);
        TransitionState leaving = TransitionFormulas.At(null, 0, push, 30, 0, 100, 99);
        Assert.InRange(leaving.ShiftY, 0.95, 1);                 // came in from the top, so the old clip leaves downwards

        var zoom = new Transition(TransitionKind.CrossZoom, 30);
        Assert.Equal(0, TransitionFormulas.At(zoom, 30, null, 0, 0, 100, 2).Opacity);
        Assert.InRange(TransitionFormulas.At(zoom, 30, null, 0, 0, 100, 15).Zoom, 3.5, 4);

        var film = new Transition(TransitionKind.FilmDissolve, 30);
        Assert.InRange(TransitionFormulas.At(film, 30, null, 0, 0, 100, 3).Opacity, 0.01, 0.1); // slow start
    }

    [Fact]
    public void Wipes_and_slides_get_a_direction_in_the_inspector()
    {
        var project = new Project();
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 10, FrameRate = 30 };
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        Clip clip = project.TrackByName("V1").Clips[0];
        tl.Select(clip, add: false);
        var inspector = new InspectorViewModel(tl);

        inspector.StartKind = inspector.TransitionChoices.Single(c => c.Value == TransitionKind.Wipe);
        Assert.True(inspector.HasStartDirection);
        inspector.StartDirection = 3;
        Assert.Equal(new Transition(TransitionKind.Wipe, 30, 3), clip.TransitionIn);

        inspector.StartKind = inspector.TransitionChoices.Single(c => c.Value == TransitionKind.IrisRound);
        Assert.False(inspector.HasStartDirection);
    }

    // ---- Every effect and transition really works in FFmpeg ----

    private async Task<string> Export(Action<Project, TimelineViewModel> build, string name)
    {
        var project = new Project();
        project.Settings.Width = W;
        project.Settings.Height = H;
        var tl = new TimelineViewModel(project);
        build(project, tl);
        string output = Path.Combine(media.Folder, name + ".mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = W, Height = H });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);
        return output;
    }

    [Fact]
    public async Task Every_effect_exports_and_previews()
    {
        if (!media.Available) return;
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        await CheckAll(EffectCatalog.All, async definition =>
        {
            ClipEffect effect = definition.Create();

            // In the export…
            string file = await Export((p, tl) =>
            {
                tl.AddMedia(video, p.TrackByName("V1"), 0);
                Clip clip = p.TrackByName("V1").Clips.Single(c => p.TrackOf(c).Kind == TrackKind.Video);
                clip.Length = 15;
                clip.Effects = [effect];
            }, "fx-" + definition.Kind);
            if (new FileInfo(file).Length == 0) return $"{definition.Kind}: empty export";

            // …and in the preview decoder's recipe (a simpler kind of FFmpeg recipe).
            string chain = EffectFilters.Chain([effect], new EffectContext(0.05, 30, "p"));
            var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            [
                "-v", "error", "-i", media.Video30, "-frames:v", "3",
                "-vf", $"fps=30,scale=96:54:force_original_aspect_ratio=decrease{chain},pad=96:54:(ow-iw)/2:(oh-ih)/2:color=black@0,setsar=1",
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
            ]);
            return result.ExitCode == 0 && result.Output.Length == 96 * 54 * 4 * 3 ? null : $"{definition.Kind}: preview recipe failed: {result.Errors}";
        });
    }

    [Fact]
    public async Task Every_transition_exports_at_a_cut_and_at_clip_edges()
    {
        if (!media.Available) return;
        MediaItem red = await MediaImporter.ProbeAsync(media.Red);
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        await CheckAll(TransitionCatalog.All, async definition =>
        {
            TransitionKind kind = definition.Kind;
            string file = await Export((p, tl) =>
            {
                Track v1 = p.TrackByName("V1");
                v1.Clips.Add(new Clip { Media = red, Start = 0, Length = 20, TransitionIn = new Transition(kind, 8, 1) });
                v1.Clips.Add(new Clip { Media = white, Start = 20, Length = 20, TransitionIn = new Transition(kind, 10, 2), TransitionOut = new Transition(kind, 8, 3) });
            }, "tr-" + kind);
            return new FileInfo(file).Length > 0 ? null : $"{kind}: empty export";
        });
    }

    // ---- Pixels ----

    private static async Task<byte[]> Frame(string file, int n)
    {
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", file, "-vf", $"select=eq(n\\,{n})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return result.Output;
    }

    private static (int R, int G, int B) Px(byte[] f, int x, int y) { int i = (y * W + x) * 3; return (f[i], f[i + 1], f[i + 2]); }

    private async Task<byte[]> MidTransition(TransitionKind kind, int direction = 0)
    {
        MediaItem red = await MediaImporter.ProbeAsync(media.Red);
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        string file = await Export((p, tl) =>
        {
            Track v1 = p.TrackByName("V1");
            v1.Clips.Add(new Clip { Media = red, Start = 0, Length = 30 });
            v1.Clips.Add(new Clip { Media = white, Start = 30, Length = 30, TransitionIn = new Transition(kind, 30, direction) });
        }, $"mid-{kind}-{direction}");
        return await Frame(file, 30); // the middle of the transition (frames 15â€“45)
    }

    [Fact]
    public async Task A_wipe_shows_the_new_clip_on_the_side_it_comes_from()
    {
        if (!media.Available) return;
        byte[] f = await MidTransition(TransitionKind.Wipe, direction: 0);
        Assert.True(Px(f, 10, 27).G > 200, $"Left should already be white, got {Px(f, 10, 27)}.");
        Assert.True(Px(f, 85, 27).G < 60 && Px(f, 85, 27).R > 180, $"Right should still be red, got {Px(f, 85, 27)}.");
    }

    [Fact]
    public async Task Push_moves_both_clips()
    {
        if (!media.Available) return;
        byte[] f = await MidTransition(TransitionKind.Push, direction: 1); // the new clip comes in from the right
        Assert.True(Px(f, 85, 27).G > 200, $"Right should be the new white clip, got {Px(f, 85, 27)}.");
        Assert.True(Px(f, 10, 27).R > 180 && Px(f, 10, 27).G < 60, $"Left should be the old red clip, got {Px(f, 10, 27)}.");
    }

    [Fact]
    public async Task Iris_round_opens_in_the_middle()
    {
        if (!media.Available) return;
        byte[] f = await MidTransition(TransitionKind.IrisRound);
        Assert.True(Px(f, 48, 27).G > 200, "The middle should be white.");
        Assert.True(Px(f, 2, 2).G < 60, "A corner should still be red.");
    }

    [Fact]
    public async Task Dip_to_white_goes_through_white()
    {
        if (!media.Available) return;
        MediaItem red = await MediaImporter.ProbeAsync(media.Red);
        string file = await Export((p, tl) =>
        {
            Track v1 = p.TrackByName("V1");
            v1.Clips.Add(new Clip { Media = red, Start = 0, Length = 30 });
            v1.Clips.Add(new Clip { Media = red, Start = 30, Length = 30, TransitionIn = new Transition(TransitionKind.DipToWhite, 20) });
        }, "dip-white");
        var (r, g, b) = Px(await Frame(file, 30), 48, 27);
        Assert.True(r > 220 && g > 200 && b > 200, $"Expected (nearly) white at the cut, got {(r, g, b)}.");
    }
}
