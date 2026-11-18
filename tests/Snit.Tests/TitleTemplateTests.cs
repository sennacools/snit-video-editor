using System.IO;
using System.Windows.Media.Imaging;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Title templates in the Text tab, and the box behind the text.</summary>
public class TitleTemplateTests
{
    [Fact]
    public void A_template_gives_the_new_title_its_look_but_its_own_copy()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        TitleTemplate lowerThird = TitleTemplates.All.Single(t => t.Name == "Lower third");

        tl.AddTitleAtPlayhead(lowerThird.Style);
        Clip clip = project.TitleTrack.Clips.Single();
        Assert.Equal(("Name Surname", TitleTemplates.DarkBox, 0.83), (clip.Title!.Text, clip.Title.Box, clip.Title.Y));

        clip.Title.Text = "Changed";
        Assert.Equal("Name Surname", lowerThird.Style.Text);   // the template itself is untouched
        Assert.Null(tl.NextTitleStyle);

        tl.PlayheadFrame = 1000;
        tl.AddTitleAtPlayheadCommand.Execute(null);             // a plain title again
        Assert.Equal("", project.TitleTrack.Clips.Single(c => c.Start == 1000).Title!.Box);
    }

    [Fact]
    public void The_box_is_drawn_behind_the_text_and_saved()
    {
        var style = new TitleStyle { Text = "Hello", Box = "#FF000000", Shadow = false, Y = 0.5 };
        BitmapSource picture = TitleRenderer.Render(style, 320, 180);
        var pixels = new byte[320 * 180 * 4];
        picture.CopyPixels(pixels, 320 * 4, 0);
        int Alpha(int x, int y) => pixels[(y * 320 + x) * 4 + 3];

        // Just left of the text, inside the box: solid. Far left: see-through.
        Assert.True(Enumerable.Range(100, 40).Any(x => Alpha(x, 90) == 255), "The box should be there.");
        Assert.Equal(0, Alpha(5, 90));

        var project = new Project();
        project.TitleTrack.Clips.Add(new Clip { Media = MediaItem.Title, Length = 30, Title = style });
        string path = Path.Combine(Path.GetTempPath(), $"snit-box-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Assert.Equal("#FF000000", ProjectFile.Load(path).TitleTrack.Clips.Single().Title!.Box);
        }
        finally { File.Delete(path); }
    }
}
