using System.Diagnostics;
using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Exports for real with ffmpeg (skipped when FFmpeg isn't installed).</summary>
public class ExportRunTests(TestMedia media) : IClassFixture<TestMedia>
{
    /// <summary>A real export: a video, a picture on V2 and a gap, checked with ffprobe afterwards.</summary>
    [Fact]
    public async Task Real_export_has_the_right_length_size_and_streams()
    {
        if (!media.Available) return;

        var project = new Project();
        var tl = new TimelineViewModel(project);
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);   // 4 s
        MediaItem other = await MediaImporter.ProbeAsync(media.Video25);   // 3 s, no sound
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        tl.AddMedia(other, project.TrackByName("V2"), 30);                 // on top from 1 s to 4 s
        tl.AddMedia(video, project.TrackByName("V1"), 150);                // after a 1 s gap: ends at 9 s

        string output = Path.Combine(media.Folder, "export test.mp4");
        var settings = new ExportSettings { OutputPath = output, Width = 640, Height = 360, FrameRate = 25, VideoBitrateMbps = 2 };
        var plan = ExportCommandBuilder.Build(project, settings);

        var progress = new List<ExportProgress>();
        await new Exporter().RunAsync(plan, new Progress<ExportProgress>(progress.Add), CancellationToken.None);

        MediaItem result = await MediaImporter.ProbeAsync(output);
        Assert.Equal(640, result.Width);
        Assert.Equal(25, result.FrameRate, precision: 2);
        Assert.InRange(result.DurationSeconds, 8.9, 9.15);
        Assert.True(result.HasAudio);
        Assert.Equal(225, plan.TotalFrames);
    }

    [Fact]
    public async Task Cancelling_stops_ffmpeg_and_removes_the_half_file()
    {
        if (!media.Available) return;

        var project = new Project();
        var tl = new TimelineViewModel(project);
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        for (int i = 0; i < 8; i++) tl.AddMedia(video, project.TrackByName("V1"), i * 120);

        string output = Path.Combine(media.Folder, "cancelled.mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 3840, Height = 2160 });

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Exporter().RunAsync(plan, null, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(output));
    }
}
