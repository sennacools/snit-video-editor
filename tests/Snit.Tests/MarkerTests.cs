using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

public class MarkerTests
{
    [Fact]
    public void Markers_are_added_jumped_to_renamed_and_undone()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.PlayheadFrame = 90; tl.AddMarkerCommand.Execute(null);
        tl.PlayheadFrame = 30; tl.AddMarkerCommand.Execute(null);
        Assert.Equal([30L, 90L], project.Markers.Select(m => m.Frame)); // kept in time order

        tl.PlayheadFrame = 0;
        tl.GoToNextMarker();
        Assert.Equal(30, tl.PlayheadFrame);
        tl.GoToNextMarker();
        Assert.Equal(90, tl.PlayheadFrame);
        tl.GoToPreviousMarker();
        Assert.Equal(30, tl.PlayheadFrame);

        tl.RenameMarker(project.Markers[0], "Intro");
        Assert.Equal("Intro", project.Markers[0].Name);
        tl.UndoCommand.Execute(null);
        Assert.Equal("Marker 2", project.Markers[0].Name);
    }

    [Fact]
    public void Clips_snap_to_markers()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.PlayheadFrame = 120;
        tl.AddMarkerCommand.Execute(null);
        tl.PlayheadFrame = 0;
        Assert.Equal(120, tl.Snap(118, [], out _));
    }

    [Fact]
    public void Markers_are_saved()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.PlayheadFrame = 45;
        tl.AddMarkerCommand.Execute(null);
        string path = Path.Combine(Path.GetTempPath(), $"snit-markers-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Assert.Equal(45, ProjectFile.Load(path).Markers.Single().Frame);
        }
        finally { File.Delete(path); }
    }
}
