using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Phase 9 screens: the scopes, and the Color and Audio panels' settings.</summary>
public class ColorAudioPanelTests
{
    // ---- Scopes ----

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    private static int Brightness(ScopeImage s, int x, int y)
    {
        int i = (y * s.Width + x) * 4;
        return s.Pixels[i] + s.Pixels[i + 1] + s.Pixels[i + 2];
    }

    [Fact]
    public void A_white_picture_shows_at_the_top_of_the_waveform()
    {
        ScopeImage scope = ScopeRenderer.Render(ScopeKind.Waveform, Solid(64, 36, 255, 255, 255), 64, 36, 64 * 4);
        Assert.Equal((256, 256), (scope.Width, scope.Height));
        Assert.True(Brightness(scope, 100, 0) > 400, "The top row should be lit.");
        Assert.True(Brightness(scope, 100, 128) < 100, "The middle should stay dark.");
    }

    [Fact]
    public void Red_lands_on_the_red_target_of_the_vectorscope()
    {
        ScopeImage scope = ScopeRenderer.Render(ScopeKind.Vectorscope, Solid(64, 36, 255, 0, 0), 64, 36, 64 * 4);
        var (x, y) = ScopeRenderer.VectorPosition(255, 0, 0);
        Assert.True(x < 128 && y < 128, "Red sits up and to the left of the centre.");
        Assert.True(Brightness(scope, x, y) > 500);
        Assert.True(Brightness(scope, 128, 128) < 200, "Nothing grey in a pure red picture.");
    }

    [Fact]
    public void The_histogram_has_a_bar_for_each_colour()
    {
        ScopeImage scope = ScopeRenderer.Render(ScopeKind.Histogram, Solid(64, 36, 200, 100, 50), 64, 36, 64 * 4);
        int bottom = scope.Height - 1;
        int Red(int x) => scope.Pixels[(bottom * scope.Width + x) * 4 + 2];
        Assert.True(Red(200) > 150, "A red bar at 200.");
        Assert.True(Red(120) < 60, "No red at 120.");
    }

    [Fact]
    public void The_parade_puts_red_green_and_blue_side_by_side()
    {
        ScopeImage scope = ScopeRenderer.Render(ScopeKind.Parade, Solid(64, 36, 255, 0, 0), 64, 36, 64 * 4);
        Assert.Equal(384, scope.Width);
        Assert.True(Brightness(scope, 60, 0) > 200, "Red is at the top of the red part.");
        Assert.True(Brightness(scope, 128 + 61, 0) < 150, "Green is at the bottom, not the top."); // odd column: off the dotted guide line
        Assert.True(Brightness(scope, 128 + 60, 255) > 200);
    }

    // ---- Panels ----

    private static (Project, TimelineViewModel, InspectorViewModel) Setup()
    {
        var project = new Project();
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 5, FrameRate = 30, HasAudio = true };
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        tl.Select(project.TrackByName("V1").Clips[0], add: false);
        return (project, tl, new InspectorViewModel(tl));
    }

    [Fact]
    public void Wheels_curves_and_reset_change_only_the_colour()
    {
        var (project, _, inspector) = Setup();
        inspector.Scale = 50;
        inspector.MidtonesX = 0.5;
        inspector.MidtonesY = -0.25;
        inspector.CurveRed = "1/0.8 0/0";
        inspector.Tint = 30;

        ClipLook look = project.TrackByName("V1").Clips[0].Look;
        Assert.Equal((0.5, -0.25, "0/0 1/0.8", 30.0), (look.MidtonesX, look.MidtonesY, look.CurveRed, look.Tint));

        inspector.CurveRed = "0/0 1/1";          // straight again = no curve
        Assert.Equal("", project.TrackByName("V1").Clips[0].Look.CurveRed);

        inspector.ResetColorCommand.Execute(null);
        look = project.TrackByName("V1").Clips[0].Look;
        Assert.False(look.HasColor);
        Assert.Equal(50, look.Scale);
    }

    [Fact]
    public void Moving_a_wheel_puck_is_one_undo_step()
    {
        var (project, tl, inspector) = Setup();
        inspector.ShadowsX = 0.1; inspector.ShadowsY = 0.1;
        inspector.ShadowsX = 0.2; inspector.ShadowsY = 0.3;
        tl.UndoCommand.Execute(null);
        Assert.False(project.TrackByName("V1").Clips[0].Look.HasWheels);
    }

    [Fact]
    public void Volume_keyframes_follow_the_playhead()
    {
        var (project, tl, inspector) = Setup();
        Clip sound = project.TrackByName("A1").Clips[0];

        inspector.VolumeDb = -6;
        inspector.ToggleVolumeKeyframeCommand.Execute(null);    // keyframe at 0: -6 dB
        Assert.True(inspector.VolumeKey.IsAnimated);
        tl.PlayheadFrame = 60;
        inspector.VolumeDb = -20;                               // a second keyframe at 60
        Assert.Equal([new Keyframe(0, -6), new Keyframe(60, -20)], sound.Keyframes[Keyframes.Volume]);

        tl.PlayheadFrame = 30;
        Assert.Equal(-13, inspector.VolumeDb);                  // halfway

        Assert.Contains("volume='pow(10,", PreviewPlan.BuildAudio(project).Single() is { } piece
            ? AudioFilters.ClipEffects(piece.VolumeDb, 0, 0, 6, piece.Audio, piece.VolumeKeys, 30) : "");
    }

    [Fact]
    public void Equaliser_and_noise_reduction_go_on_the_sound_clip()
    {
        var (project, _, inspector) = Setup();
        inspector.EqLow = 6;
        inspector.NoiseReduction = 40;
        Clip sound = project.TrackByName("A1").Clips[0];
        Assert.Equal(new ClipAudio { Low = 6, NoiseReduction = 40 }, sound.Audio);

        inspector.ResetSoundToolsCommand.Execute(null);
        Assert.Equal(ClipAudio.Default, project.TrackByName("A1").Clips[0].Audio);
    }

    [Fact]
    public void The_mixer_lists_the_sound_tracks()
    {
        var (project, tl, _) = Setup();
        Assert.Equal(["A1", "A2"], tl.AudioTracks.Select(t => t.Name));
        tl.AddAudioTrackCommand.Execute(null);
        Assert.Equal(3, tl.AudioTracks.Count);
    }
}
