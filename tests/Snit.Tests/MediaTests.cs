using System.Diagnostics;
using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>
/// Makes a few short test files with FFmpeg once, in a temp folder (never in the repo).
/// Tests that need FFmpeg are skipped when it isn't installed.
/// </summary>
public sealed class TestMedia : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "snit-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public bool Available { get; }
    public string Video30 => Path.Combine(Folder, "pattern 30fps.mp4");
    public string Video25 => Path.Combine(Folder, "pattern 25fps.mp4");
    public string Tone => Path.Combine(Folder, "tone.mp3");
    public string White => Path.Combine(Folder, "white.png");

    public TestMedia()
    {
        Available = FFmpegLocator.CheckAsync().Result.IsAvailable;
        if (!Available) return;

        Directory.CreateDirectory(Folder);
        Make($"-f lavfi -i testsrc2=size=320x180:rate=30:duration=4 -f lavfi -i sine=frequency=440:duration=4 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{Video30}\"");
        Make($"-f lavfi -i testsrc2=size=320x180:rate=25:duration=3 -c:v libx264 -pix_fmt yuv420p \"{Video25}\"");
        Make($"-f lavfi -i sine=frequency=330:duration=2 -c:a libmp3lame \"{Tone}\"");
        Make($"-f lavfi -i color=c=white:s=320x180 -frames:v 1 \"{White}\"");
    }

    private static void Make(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(FFmpegLocator.Current.FfmpegPath!, "-hide_banner -loglevel error -y " + args)
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        p.WaitForExit();
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch { /* temp files; fine to leave */ }
    }
}

public class MediaTests(TestMedia media) : IClassFixture<TestMedia>
{
    [Fact]
    public async Task Probe_reads_length_size_and_sound()
    {
        if (!media.Available) return;

        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        Assert.Equal(MediaKind.Video, video.Kind);
        Assert.Equal(320, video.Width);
        Assert.Equal(30, video.FrameRate, precision: 2);
        Assert.InRange(video.DurationSeconds, 3.9, 4.2);
        Assert.True(video.HasAudio);

        MediaItem tone = await MediaImporter.ProbeAsync(media.Tone);
        Assert.Equal(MediaKind.Audio, tone.Kind);
    }

    [Fact]
    public async Task Probe_rejects_files_that_are_not_media()
    {
        if (!media.Available) return;
        string text = Path.Combine(media.Folder, "notes.txt");
        File.WriteAllText(text, "hello");
        await Assert.ThrowsAnyAsync<Exception>(() => MediaImporter.ProbeAsync(text));
    }

    /// <summary>
    /// Frame accuracy: reading frame 70 by playing from the start must give exactly
    /// the same picture as jumping straight to frame 70.
    /// </summary>
    [Theory]
    [InlineData(nameof(TestMedia.Video30), 70)]
    [InlineData(nameof(TestMedia.Video25), 50)] // 25 fps file in a 30 fps project
    public void Seeking_lands_on_the_same_frame_as_playing(string which, long target)
    {
        if (!media.Available) return;
        string file = which == nameof(TestMedia.Video30) ? media.Video30 : media.Video25;
        var pool = new FramePool(320, 180);

        byte[] played;
        using (var fromStart = new VideoDecoder(FFmpegLocator.Current.FfmpegPath!, file, 0, 30, pool))
        {
            for (long i = 0; i < target; i++) pool.Return(fromStart.ReadNext()!);
            played = fromStart.ReadNext()!;
        }

        byte[] jumped;
        using (var direct = new VideoDecoder(FFmpegLocator.Current.FfmpegPath!, file, target, 30, pool))
            jumped = direct.ReadNext()!;

        Assert.NotNull(played);
        Assert.NotNull(jumped);
        Assert.True(played.AsSpan().SequenceEqual(jumped), "Seeking gave a different frame than playing.");

        // And it's really a different picture from a neighbouring frame (the test pattern moves).
        using var neighbour = new VideoDecoder(FFmpegLocator.Current.FfmpegPath!, file, target + 3, 30, pool);
        Assert.False(neighbour.ReadNext()!.AsSpan().SequenceEqual(jumped));
    }

    [Fact]
    public async Task Project_file_round_trip_keeps_clips_and_links()
    {
        if (!media.Available) return;
        var project = new Project();
        var tl = new TimelineViewModel(project);
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        project.Media.Add(video);
        tl.AddMedia(video, project.TrackByName("V2"), 15);
        tl.SplitAt(project.TrackByName("V2").Clips[0], 40);
        project.TrackByName("A2").IsLocked = true;

        string path = Path.Combine(media.Folder, "test.snit");
        ProjectFile.Save(project, path);
        Project loaded = ProjectFile.Load(path);

        Assert.Single(loaded.Media);
        Assert.False(loaded.Media[0].IsOffline);
        var v2 = loaded.TrackByName("V2").Clips.OrderBy(c => c.Start).ToList();
        var a2 = loaded.TrackByName("A2").Clips.OrderBy(c => c.Start).ToList();
        Assert.Equal(2, v2.Count);
        Assert.Equal((40L, 25L), (v2[1].Start, v2[1].In));
        Assert.Equal(v2[1].LinkId, a2[1].LinkId);
        Assert.True(loaded.TrackByName("A2").IsLocked);
        Assert.Same(loaded.Media[0], v2[0].Media);
    }

    [Fact]
    public void Missing_media_is_marked_offline_when_opening()
    {
        string path = Path.Combine(Path.GetTempPath(), $"snit-missing-{Guid.NewGuid():N}.snit");
        var project = new Project();
        project.Media.Add(new MediaItem { FilePath = @"C:\does\not\exist.mp4", Kind = MediaKind.Video, DurationSeconds = 5 });
        ProjectFile.Save(project, path);
        try
        {
            Assert.True(ProjectFile.Load(path).Media[0].IsOffline);
        }
        finally { File.Delete(path); }
    }
}
