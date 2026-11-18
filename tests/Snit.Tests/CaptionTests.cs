using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Captions: the C1 track, .srt files, and burning them into the export.</summary>
public class CaptionTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const string Sample = "﻿1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>there</i>\r\n\r\n" +
                                  "2\r\n00:00:03,000 --> 00:00:04,000\r\nTwo\r\nlines\r\n\r\n" +
                                  "3\n00:00:05.5 --> 00:00:06,000\nDot and short milliseconds\n";

    [Fact]
    public void An_srt_file_is_read_with_its_quirks()
    {
        var lines = Srt.Parse(Sample);
        Assert.Equal(3, lines.Count);
        Assert.Equal(new CaptionLine(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "Hello there"), lines[0]);
        Assert.Equal("Two\nlines", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(5.5), lines[2].Start);
    }

    [Fact]
    public void Writing_and_reading_again_gives_the_same_captions()
    {
        var lines = Srt.Parse(Sample);
        string written = Srt.Write(lines);
        Assert.StartsWith("1\r\n00:00:01,000 --> 00:00:02,500\r\nHello there\r\n\r\n2\r\n", written);
        Assert.Equal(lines, Srt.Parse(written));
    }

    [Fact]
    public void Importing_puts_captions_on_a_C1_track_at_the_top()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        string path = Path.Combine(Path.GetTempPath(), $"snit-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, Sample);
        try
        {
            Assert.Equal(3, tl.ImportCaptions(path));
            Track c1 = project.Tracks[0];
            Assert.Equal(("C1", TrackKind.Caption), (c1.Name, c1.Kind));
            Assert.Equal((30L, 45L), (c1.Clips[0].Start, c1.Clips[0].Length)); // 1 s to 2.5 s at 30 fps
            Assert.Equal(["V2", "V1"], project.Tracks.Where(t => t.Kind == TrackKind.Video).Select(t => t.Name));

            tl.UndoCommand.Execute(null);
            Assert.Null(project.CaptionTrack);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Captions_are_saved_exported_as_srt_and_can_be_deleted()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.PlayheadFrame = 60;
        tl.AddCaptionAtPlayheadCommand.Execute(null);
        project.CaptionTrack!.Clips[0].Title!.Text = "Hi";

        string path = Path.Combine(Path.GetTempPath(), $"snit-cap-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Project loaded = ProjectFile.Load(path);
            Assert.Equal("C1", loaded.Tracks[0].Name);
            Assert.Equal("1\r\n00:00:02,000 --> 00:00:05,000\r\nHi\r\n\r\n", Srt.Write(Captions.Lines(loaded)));
            Assert.Equal("00:00:01,000", Srt.Write(Captions.Lines(loaded, fromFrame: 30)).Split("\r\n")[1][..12]);
        }
        finally { File.Delete(path); }

        tl.RemoveTrackCommand.Execute(project.CaptionTrack);
        Assert.Null(project.CaptionTrack);
    }

    [Fact]
    public async Task Captions_are_burned_into_the_export_unless_switched_off()
    {
        if (!media.Available) return;
        const int W = 320, H = 180;
        var project = new Project();
        project.CaptionTrack?.Clips.Clear();
        Captions.Import(project, [new CaptionLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), "████████")]);

        async Task<int> BrightestNearTheBottom(bool burnIn)
        {
            string output = Path.Combine(media.Folder, $"caption-{burnIn}.mp4");
            var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = W, Height = H, BurnInCaptions = burnIn });
            await new Exporter().RunAsync(plan, null, CancellationToken.None);
            var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
                ["-v", "error", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
            int y = (int)(H * 0.88);
            return Enumerable.Range(W / 3, W / 3).Max(x => (int)result.Output[y * W + x]);
        }

        Assert.True(await BrightestNearTheBottom(true) > 180, "The white caption text should be in the picture.");
        Assert.True(await BrightestNearTheBottom(false) < 40, "Without burn-in the picture stays black.");
    }

    [Fact]
    public void The_preview_shows_captions_on_top()
    {
        var project = new Project();
        Captions.Import(project, [new CaptionLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Top")]);
        var layers = PreviewPlan.BuildLayers(project);
        Assert.Equal("Top", layers[^1].Single().Title!.Text); // the last layer is drawn last, on top
    }
}
