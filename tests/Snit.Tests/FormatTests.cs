using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Every export format, exported for real and checked with ffprobe. Skipped without FFmpeg.</summary>
public class FormatTests(TestMedia media) : IClassFixture<TestMedia>
{
    [Theory]
    [InlineData(ExportFormat.Mp4H264, ".mp4", "h264", "aac")]
    [InlineData(ExportFormat.Mp4H265, ".mp4", "hevc", "aac")]
    [InlineData(ExportFormat.WebmVp9, ".webm", "vp9", "opus")]
    [InlineData(ExportFormat.MovProRes, ".mov", "prores", "pcm_s16le")]
    [InlineData(ExportFormat.Gif, ".gif", "gif", null)]
    [InlineData(ExportFormat.Mp3, ".mp3", null, "mp3")]
    public async Task Each_format_has_the_right_picture_and_sound(ExportFormat format, string extension, string? video, string? audio)
    {
        if (!media.Available) return;
        Assert.Equal(extension, ExportCommandBuilder.Extension(format));

        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddMedia(await MediaImporter.ProbeAsync(media.Video30), project.TrackByName("V1"), 0); // 4 s with sound
        tl.SetInPoint(); tl.PlayheadFrame = 30; tl.SetOutPoint(); // only 1 s, to keep it quick

        string output = Path.Combine(media.Folder, $"format test {format}{extension}");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings
        {
            OutputPath = output, Format = format, Width = 320, Height = 180, FrameRate = 15, VideoBitrateMbps = 1,
            RangeStart = project.InPoint, RangeEnd = project.OutPoint,
        });
        var reports = new List<ExportProgress>();
        await new Exporter().RunAsync(plan, new Progress<ExportProgress>(reports.Add), CancellationToken.None);

        var probe = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfprobePath!,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", output]);
        var streams = System.Text.Encoding.UTF8.GetString(probe.Output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .ToDictionary(p => p[1], p => p[0]); // codec_type → codec_name

        Assert.Equal(video, streams.GetValueOrDefault("video"));
        Assert.Equal(audio, streams.GetValueOrDefault("audio"));
        Assert.Equal(15, plan.TotalFrames); // 1 s at 15 fps
    }
}
