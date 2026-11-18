using System.IO;
using System.Windows.Media.Imaging;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Title clips: placing, drawing, saving, undo and export.</summary>
public class TitleTests(TestMedia media) : IClassFixture<TestMedia>
{
    private static (Project, TimelineViewModel) NewTimeline()
    {
        var project = new Project();
        return (project, new TimelineViewModel(project));
    }

    [Fact]
    public void A_new_title_is_five_seconds_on_V2()
    {
        var (project, tl) = NewTimeline();
        tl.PlayheadFrame = 60;
        tl.AddTitleAtPlayheadCommand.Execute(null);

        Clip title = Assert.Single(project.TrackByName("V2").Clips);
        Assert.True(title.IsTitle);
        Assert.Equal((60L, 150L), (title.Start, title.Length));
        Assert.Empty(project.TrackByName("A2").Clips); // no sound
        Assert.Equal("Your title", title.Name);
    }

    [Fact]
    public void Titles_are_see_through_in_the_preview_plan()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(MediaItem.Title, project.TrackByName("V2"), 0);

        VideoSegment title = Assert.Single(PreviewPlan.BuildLayers(project)[1]); // on the V2 layer
        Assert.Equal(MediaKind.Text, title.Kind);
        Assert.False(title.CoversFrame(1920, 1080)); // see-through: what's below stays visible
        Assert.Equal("Your title", title.Title!.Text);
    }

    [Fact]
    public void Undo_brings_back_the_old_text()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(MediaItem.Title, project.TrackByName("V2"), 0);

        tl.SaveUndoPoint();
        project.TrackByName("V2").Clips[0].Title!.Text = "Changed";
        tl.UndoCommand.Execute(null);

        Assert.Equal("Your title", project.TrackByName("V2").Clips[0].Title!.Text);
    }

    [Fact]
    public void The_drawing_has_letters_in_the_middle_and_is_see_through_elsewhere()
    {
        var style = new TitleStyle { Text = "HELLO", FontSize = 200, Color = "#FF0000", Shadow = false };
        BitmapSource bitmap = TitleRenderer.Render(style, 640, 360);

        var pixels = new byte[640 * 360 * 4];
        bitmap.CopyPixels(pixels, 640 * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * 640 + x) * 4 + 3];

        Assert.Equal(0, Alpha(5, 5));            // corner: see-through
        int opaqueInMiddleRow = Enumerable.Range(200, 240).Count(x => Alpha(x, 180) > 200);
        Assert.True(opaqueInMiddleRow > 20, "Expected letters in the middle of the picture.");
    }

    [Fact]
    public void Titles_are_saved_and_opened_again()
    {
        var (project, tl) = NewTimeline();
        tl.AddMedia(MediaItem.Title, project.TrackByName("V2"), 30);
        var style = project.TrackByName("V2").Clips[0].Title!;
        style.Text = "Summer\nvlog";
        style.Color = "#E8C547";
        style.Y = 0.8;

        string path = Path.Combine(Path.GetTempPath(), $"snit-title-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Clip loaded = ProjectFile.Load(path).TrackByName("V2").Clips[0];
            Assert.Same(MediaItem.Title, loaded.Media);
            Assert.Equal(("Summer\nvlog", "#E8C547", 0.8), (loaded.Title!.Text, loaded.Title.Color, loaded.Title.Y));
            Assert.Equal("Summer", loaded.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_exported_picture_really_contains_the_title()
    {
        if (!media.Available) return;
        var (project, tl) = NewTimeline();
        tl.AddMedia(MediaItem.Title, project.TrackByName("V2"), 0);
        var style = project.TrackByName("V2").Clips[0].Title!;
        (style.Text, style.Color, style.FontSize, style.Shadow) = ("MMMM", "#FF0000", 400, false);

        string output = Path.Combine(media.Folder, "red title.mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 320, Height = 180 });
        await new Exporter().RunAsync(plan, null, CancellationToken.None);

        // Take frame 10 as raw red-green-blue pixels and count clearly red ones.
        var frame = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
            ["-v", "error", "-i", output, "-vf", "select=eq(n\\,10)", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        Assert.Equal(320 * 180 * 3, frame.Output.Length);

        int red = 0, black = 0;
        for (int i = 0; i < frame.Output.Length; i += 3)
        {
            byte r = frame.Output[i], g = frame.Output[i + 1], b = frame.Output[i + 2];
            if (r > 180 && g < 70 && b < 70) red++;
            if (r < 30 && g < 30 && b < 30) black++;
        }
        int total = 320 * 180;
        Assert.True(red > total / 20, $"Only {red} red pixels: the title is missing.");
        Assert.True(black > total / 5, $"Only {black} black pixels: the see-through part isn't see-through.");
    }

    [Fact]
    public async Task A_title_ends_up_in_the_exported_video()
    {
        if (!media.Available) return;
        var (project, tl) = NewTimeline();
        MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
        tl.AddMedia(video, project.TrackByName("V1"), 0);
        tl.AddMedia(MediaItem.Title, project.TrackByName("V2"), 0);
        project.TrackByName("V2").Clips[0].Title!.Text = "TEST";

        string output = Path.Combine(media.Folder, "title export.mp4");
        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 640, Height = 360 });
        Assert.Contains(plan.Arguments, a => a.EndsWith(".png"));

        await new Exporter().RunAsync(plan, null, CancellationToken.None);

        Assert.True(File.Exists(output));
        Assert.All(plan.TempFiles, f => Assert.False(File.Exists(f))); // title pictures cleaned up
        MediaItem result = await MediaImporter.ProbeAsync(output);
        Assert.InRange(result.DurationSeconds, 4.9, 5.15); // the 5 s title is the longest clip
    }
}
