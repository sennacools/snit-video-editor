using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

public class InspectorTitleTests
{
    private static (Project, TimelineViewModel, InspectorViewModel) Setup()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddTitleAtPlayheadCommand.Execute(null); // adds and selects a title
        return (project, tl, new InspectorViewModel(tl));
    }

    [Fact]
    public void Selecting_a_title_shows_its_text_settings()
    {
        var (_, _, inspector) = Setup();
        Assert.True(inspector.HasTitle);
        Assert.False(inspector.HasAudio);
        Assert.Equal("Your title", inspector.TitleText);
        Assert.Equal(50, inspector.TitleX);
    }

    [Fact]
    public void Typing_changes_the_title_and_is_one_undo_step()
    {
        var (project, tl, inspector) = Setup();
        foreach (string text in new[] { "H", "He", "Hel", "Hell", "Hello" }) inspector.TitleText = text;

        Assert.Equal("Hello", project.TrackByName("V2").Clips[0].Title!.Text);
        Assert.Equal("Hello", inspector.ClipName);

        tl.UndoCommand.Execute(null);
        Assert.Equal("Your title", inspector.TitleText);
    }

    [Fact]
    public void Colour_codes_are_checked()
    {
        var (_, _, inspector) = Setup();
        inspector.TitleColor = "e8c547";      // the # is added
        Assert.Equal("#E8C547", inspector.TitleColor);

        inspector.TitleColor = "#12";         // not a colour: ignored
        Assert.Equal("#E8C547", inspector.TitleColor);
    }

    [Fact]
    public void Position_is_shown_in_percent()
    {
        var (project, _, inspector) = Setup();
        inspector.TitleY = 80;
        Assert.Equal(0.8, project.TrackByName("V2").Clips[0].Title!.Y, precision: 3);
    }
}
