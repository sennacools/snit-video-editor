using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Adding, removing and renumbering tracks.</summary>
public class TrackTests
{
    private static MediaItem Video() => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 2, FrameRate = 30, HasAudio = true,
    };

    [Fact]
    public void New_video_tracks_go_on_top_and_audio_tracks_at_the_bottom()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddVideoTrackCommand.Execute(null);
        tl.AddAudioTrackCommand.Execute(null);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2", "A3"], project.Tracks.Select(t => t.Name));
    }

    [Fact]
    public void A_video_on_V3_gets_its_sound_on_A3()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddVideoTrackCommand.Execute(null);
        tl.AddAudioTrackCommand.Execute(null);
        tl.AddMedia(Video(), project.TrackByName("V3"), 0);
        Assert.Single(project.TrackByName("A3").Clips);
    }

    [Fact]
    public void Removing_a_track_renumbers_the_rest_and_undo_brings_it_back()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.AddVideoTrackCommand.Execute(null);                    // V3, V2, V1
        tl.AddMedia(Video(), project.TrackByName("V2"), 0);
        Track v2 = project.TrackByName("V2");

        tl.RemoveTrackCommand.Execute(v2);
        Assert.Equal(["V2", "V1", "A1", "A2"], project.Tracks.Select(t => t.Name)); // old V3 is now V2
        Assert.Empty(project.TrackByName("V2").Clips);

        tl.UndoCommand.Execute(null);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], project.Tracks.Select(t => t.Name));
        Assert.Single(project.TrackByName("V2").Clips);
    }

    [Fact]
    public void The_last_video_or_audio_track_cannot_be_removed()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        tl.RemoveTrackCommand.Execute(project.TrackByName("V2"));
        tl.RemoveTrackCommand.Execute(project.TrackByName("V1"));  // the only one left: stays
        Assert.Equal(["V1", "A1", "A2"], project.Tracks.Select(t => t.Name));
    }

    [Fact]
    public void Extra_tracks_are_saved_and_opened_again()
    {
        var project = new Project();
        var tl = new TimelineViewModel(project);
        MediaItem video = Video();
        project.Media.Add(video);
        tl.AddVideoTrackCommand.Execute(null);
        tl.AddVideoTrackCommand.Execute(null);
        tl.AddAudioTrackCommand.Execute(null);
        tl.AddMedia(video, project.TrackByName("V4"), 0);

        string path = Path.Combine(Path.GetTempPath(), $"snit-tracks-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Project loaded = ProjectFile.Load(path);
            Assert.Equal(["V4", "V3", "V2", "V1", "A1", "A2", "A3"], loaded.Tracks.Select(t => t.Name));
            Assert.Single(loaded.TrackByName("V4").Clips);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Every_video_track_becomes_a_preview_layer()
    {
        var project = new Project();
        project.AddTrack(TrackKind.Video);
        Assert.Equal(3, PreviewPlan.BuildLayers(project).Count);
    }
}
