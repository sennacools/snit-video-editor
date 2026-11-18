using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Phase 9: colour wheels, curves, the sound equaliser, noise reduction, volume keyframes and the mixer.</summary>
public class ColorAudioTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const int W = 320, H = 180;

    // ---- Curves ----

    [Fact]
    public void Curves_are_read_and_written_as_text()
    {
        var points = Curves.Parse("1/1 0/0 0.5/0.7");
        Assert.Equal([new CurvePoint(0, 0), new CurvePoint(0.5, 0.7), new CurvePoint(1, 1)], points);
        Assert.Equal("0/0 0.5/0.7 1/1", Curves.Format(points));
        Assert.Equal("", Curves.Format(Curves.Straight));
        Assert.Same(Curves.Straight, Curves.Parse("rubbish"));
    }

    [Fact]
    public void A_curve_goes_smoothly_through_its_points()
    {
        var points = Curves.Parse("0/0 0.5/0.7 1/1");
        Assert.Equal(0.7, Curves.Evaluate(points, 0.5), 3);
        Assert.Equal(0, Curves.Evaluate(points, 0), 3);
        double quarter = Curves.Evaluate(points, 0.25);
        Assert.InRange(quarter, 0.3, 0.45); // lifted, and between its neighbours
        Assert.Equal(0.4, Curves.Evaluate(Curves.Parse("0/0.2 1/0.6"), 0.5), 3);
    }

    // ---- Recipes ----

    [Fact]
    public void Wheels_and_curves_become_FFmpeg_filters()
    {
        var look = ClipLook.Default with { HighlightsX = 1, CurveMaster = "0/0 0.5/0.6 1/1" };
        string chain = VideoFilters.Color(look);
        Assert.Contains("colorbalance=rs=0:gs=0:bs=0:rm=0:gm=0:bm=0:rh=0.5:gh=-0.25:bh=-0.25", chain);
        Assert.Contains("curves=master='0/0 0.5/0.6 1/1'", chain);
        Assert.Equal("", VideoFilters.Color(ClipLook.Default));
    }

    [Fact]
    public void Wheel_colours_add_up_to_zero_so_brightness_stays()
    {
        foreach (var (x, y) in new[] { (1.0, 0.0), (0.3, 0.8), (-0.7, -0.2) })
        {
            var (r, g, b) = ColorWheel.Shift(x, y);
            Assert.Equal(0, r + g + b, 6);
        }
    }

    [Fact]
    public void Removing_colour_keeps_size_and_position()
    {
        var look = ClipLook.Default with { Scale = 50, PositionX = 10, Saturation = 0, CurveRed = "0/0 1/0.5", MidtonesY = 0.4 };
        ClipLook plain = look.WithoutColor();
        Assert.False(plain.HasColor);
        Assert.Equal((50.0, 10.0), (plain.Scale, plain.PositionX));
    }

    [Fact]
    public void Sound_tools_become_FFmpeg_filters_in_a_sensible_order()
    {
        var audio = new ClipAudio { Low = 4, Mid = -3, High = 2, NoiseReduction = 50 };
        string chain = AudioFilters.ClipEffects(-6, 0, 0, 10, audio, null, 30, trackDb: 3, pan: -0.5);
        Assert.Equal(",afftdn=nr=15:nf=-40,bass=g=4:f=100,equalizer=f=1000:t=q:w=1:g=-3,treble=g=2:f=8000," +
                     "volume=-6dB,volume=3dB,pan=stereo|c0=1*c0|c1=0.5*c1", chain);
    }

    [Fact]
    public void Volume_keyframes_replace_the_fixed_volume()
    {
        string chain = AudioFilters.ClipEffects(-6, 0, 0, 10, null, [new Keyframe(0, 0), new Keyframe(30, -20)], 30);
        Assert.StartsWith(",volume='pow(10,(", chain);
        Assert.EndsWith(")/20)':eval=frame", chain);
        Assert.DoesNotContain("-6dB", chain);
    }

    // ---- The mixer ----

    private static (Project, TimelineViewModel) TwoSongs()
    {
        var project = new Project();
        var song = new MediaItem { FilePath = @"C:\fake\song.mp3", Kind = MediaKind.Audio, DurationSeconds = 10, HasAudio = true };
        project.Media.Add(song);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(song, project.TrackByName("A1"), 0);
        tl.AddMedia(song, project.TrackByName("A2"), 0);
        return (project, tl);
    }

    [Fact]
    public void Solo_tracks_are_the_only_ones_heard()
    {
        var (project, _) = TwoSongs();
        Track a1 = project.TrackByName("A1"), a2 = project.TrackByName("A2");
        Assert.True(project.IsAudible(a1) && project.IsAudible(a2));

        a2.IsSolo = true;
        Assert.False(project.IsAudible(a1));
        Assert.True(project.IsAudible(a2));
        Assert.Single(PreviewPlan.BuildAudio(project));

        a2.IsOff = true; // muted beats solo
        Assert.Empty(PreviewPlan.BuildAudio(project));
    }

    [Fact]
    public void Mixer_settings_and_sound_tools_are_saved()
    {
        var (project, _) = TwoSongs();
        Track a2 = project.TrackByName("A2");
        a2.VolumeDb = -4; a2.Pan = 0.25; a2.IsSolo = true;
        project.TrackByName("A1").Clips[0].Audio = new ClipAudio { High = 3, NoiseReduction = 20 };
        project.TrackByName("A1").Clips[0].Keyframes = Keyframes.Set(Keyframes.None, Keyframes.Volume, 10, -12);

        string path = Path.Combine(Path.GetTempPath(), $"snit-mix-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Project loaded = ProjectFile.Load(path);
            Track b2 = loaded.TrackByName("A2");
            Assert.Equal((-4.0, 0.25, true), (b2.VolumeDb, b2.Pan, b2.IsSolo));
            Clip clip = loaded.TrackByName("A1").Clips[0];
            Assert.Equal(new ClipAudio { High = 3, NoiseReduction = 20 }, clip.Audio);
            Assert.Equal(-12, clip.Keyframes[Keyframes.Volume].Single().Value);
        }
        finally { File.Delete(path); }
    }

    // ---- Real exports ----

    private async Task<string> Export(Action<Project, TimelineViewModel> build, string name)
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        build(project, tl);
        string output = Path.Combine(media.Folder, name + ".mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = W, Height = H });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);
        return output;
    }

    private static async Task<(int R, int G, int B)> CentrePixel(string file)
    {
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", file, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        int i = (H / 2 * W + W / 2) * 3;
        return (result.Output[i], result.Output[i + 1], result.Output[i + 2]);
    }

    [Fact]
    public async Task A_curve_darkens_white_in_the_export()
    {
        if (!media.Available) return;
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        string file = await Export((p, tl) =>
        {
            tl.AddMedia(white, p.TrackByName("V1"), 0);
            p.TrackByName("V1").Clips[0].Look = ClipLook.Default with { CurveMaster = "0/0 1/0.5" };
        }, "curve");
        var (r, g, b) = await CentrePixel(file);
        Assert.True(Math.Abs(r - 128) < 20 && Math.Abs(g - 128) < 20 && Math.Abs(b - 128) < 20, $"Expected mid grey, got {(r, g, b)}.");
    }

    [Fact]
    public async Task Pushing_the_highlights_wheel_to_red_tints_white()
    {
        if (!media.Available) return;
        MediaItem white = await MediaImporter.ProbeAsync(media.White);
        string file = await Export((p, tl) =>
        {
            tl.AddMedia(white, p.TrackByName("V1"), 0);
            p.TrackByName("V1").Clips[0].Look = ClipLook.Default with { HighlightsX = 1 };
        }, "wheel");
        var (r, g, b) = await CentrePixel(file);
        Assert.True(r > g + 40 && r > b + 40, $"Expected a red tint, got {(r, g, b)}.");
    }

    [Fact]
    public async Task Panning_left_silences_the_right_speaker()
    {
        if (!media.Available) return;
        MediaItem tone = await MediaImporter.ProbeAsync(media.Tone);
        string file = await Export((p, tl) =>
        {
            tl.AddMedia(tone, p.TrackByName("A1"), 0);
            p.TrackByName("A1").Pan = -1;
        }, "pan");
        var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", file, "-t", "1", "-f", "s16le", "-ac", "2", "pipe:1"]);
        double left = 0, right = 0;
        for (int i = 0; i + 3 < result.Output.Length; i += 4)
        {
            left += Math.Abs(BitConverter.ToInt16(result.Output, i));
            right += Math.Abs(BitConverter.ToInt16(result.Output, i + 2));
        }
        Assert.True(left > 1000 * 44100 / 10, $"The left speaker should play the tone ({left}).");
        Assert.True(right < left * 0.02, $"The right speaker should be (almost) silent (left {left}, right {right}).");
    }
}
