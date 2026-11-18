using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Jumping between cuts, and the in/out marks.</summary>
public class MarkTests
{
    private static (Project, TimelineViewModel) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var video = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 2, FrameRate = 30 };
        project.Media.Add(video);
        tl.AddMedia(video, project.TrackByName("V1"), 0);    // 0–60
        tl.AddMedia(video, project.TrackByName("V1"), 100);  // 100–160
        return (project, tl);
    }

    [Fact]
    public void Up_and_down_jump_to_the_cuts()
    {
        var (_, tl) = Setup();
        tl.PlayheadFrame = 80;
        tl.GoToNextEdit();
        Assert.Equal(100, tl.PlayheadFrame);
        tl.GoToNextEdit();
        Assert.Equal(160, tl.PlayheadFrame);
        tl.GoToNextEdit();
        Assert.Equal(160, tl.PlayheadFrame); // nothing after the end: stays
        tl.GoToPreviousEdit();
        Assert.Equal(100, tl.PlayheadFrame);
        tl.PlayheadFrame = 10;
        tl.GoToPreviousEdit();
        Assert.Equal(0, tl.PlayheadFrame);
    }

    [Fact]
    public void In_and_out_marks_make_a_range()
    {
        var (_, tl) = Setup();
        tl.PlayheadFrame = 30;
        tl.SetInPoint();
        Assert.False(tl.HasInOutRange);
        tl.PlayheadFrame = 120;
        tl.SetOutPoint();
        Assert.True(tl.HasInOutRange);
        Assert.Equal(1.0, tl.InSeconds);

        tl.PlayheadFrame = 150;
        tl.SetInPoint(); // after the out mark: the out mark goes away
        Assert.Null(tl.OutPoint);

        tl.ClearInOut();
        Assert.True(double.IsNaN(tl.InSeconds));
    }

    [Fact]
    public void Marks_are_saved_with_the_project()
    {
        var (project, tl) = Setup();
        tl.PlayheadFrame = 20; tl.SetInPoint();
        tl.PlayheadFrame = 90; tl.SetOutPoint();

        string path = Path.Combine(Path.GetTempPath(), $"snit-marks-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Project loaded = ProjectFile.Load(path);
            Assert.Equal((20L, 90L), (loaded.InPoint, loaded.OutPoint));
        }
        finally { File.Delete(path); }
    }
}
