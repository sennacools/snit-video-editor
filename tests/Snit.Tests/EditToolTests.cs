using Snit.Core;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Rolling edit, slip, slide and rate stretch.</summary>
public class EditToolTests
{
    /// <summary>Three touching clips on V1, each 100 frames, from the middle of a 20 s file (lots of spare footage).</summary>
    private static (TimelineViewModel, Clip A, Clip B, Clip C) ThreeClips()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        var media = new MediaItem { FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 20, FrameRate = 30 };
        var track = project.TrackByName("V1");
        var a = new Clip { Media = media, Start = 0, In = 100, Length = 100 };
        var b = new Clip { Media = media, Start = 100, In = 300, Length = 100 };
        var c = new Clip { Media = media, Start = 200, In = 500, Length = 100 };
        track.Clips.AddRange([a, b, c]);
        return (tl, a, b, c);
    }

    [Fact]
    public void Rolling_the_cut_lengthens_one_clip_and_shortens_the_other()
    {
        var (tl, a, b, _) = ThreeClips();
        tl.BeginRoll(a, b);
        tl.UpdateTool(20);
        tl.EndTool();
        Assert.Equal((0L, 120L), (a.Start, a.End));
        Assert.Equal((120L, 200L, 320L), (b.Start, b.End, b.In)); // b's end stays, its start shows later footage
    }

    [Fact]
    public void Slipping_keeps_the_clip_in_place_but_shows_other_footage()
    {
        var (tl, _, b, _) = ThreeClips();
        tl.BeginSlip(b);
        tl.UpdateTool(30); // drag right: earlier footage
        tl.EndTool();
        Assert.Equal((100L, 100L, 270L), (b.Start, b.Length, b.In));
    }

    [Fact]
    public void Sliding_moves_the_clip_and_its_neighbours_close_the_gaps()
    {
        var (tl, a, b, c) = ThreeClips();
        tl.BeginSlide(b);
        tl.UpdateTool(-40);
        tl.EndTool();
        Assert.Equal(60, a.End);              // left neighbour shorter
        Assert.Equal((60L, 160L), (b.Start, b.End));
        Assert.Equal((160L, 300L), (c.Start, c.End)); // right neighbour starts earlier, same end
        Assert.Equal(460, c.In);
    }

    [Fact]
    public void Rate_stretch_changes_speed_to_fit_the_new_length()
    {
        var (tl, _, b, _) = ThreeClips();
        tl.BeginRateStretch(b, leftEdge: false);
        tl.UpdateTool(-50); // half as long
        tl.EndTool();
        Assert.Equal(50, b.Length);
        Assert.Equal(200, b.SpeedPercent);
        Assert.Equal(100, b.SourceLength); // still the same footage
    }

    [Fact]
    public void A_tool_drag_is_one_undo_step()
    {
        var (tl, a, b, _) = ThreeClips();
        tl.BeginRoll(a, b);
        for (long d = 1; d <= 20; d++) tl.UpdateTool(d);
        tl.EndTool();
        tl.UndoCommand.Execute(null);
        Assert.Equal(100, tl.Project.TrackByName("V1").Clips.OrderBy(c => c.Start).First().End);
    }
}
