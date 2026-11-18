using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Clip volume and fades: in the export command, when cutting, and in saved projects.</summary>
public class AudioSettingsTests
{
    private static MediaItem Song() => new()
    {
        FilePath = @"C:\fake\song.mp3", Kind = MediaKind.Audio, DurationSeconds = 10, HasAudio = true,
    };

    private static (Project, Clip) ProjectWithSong()
    {
        var project = new Project();
        MediaItem song = Song();
        project.Media.Add(song); // in the bin, like an imported file
        new TimelineViewModel(project).AddMedia(song, project.TrackByName("A1"), 0);
        return (project, project.TrackByName("A1").Clips[0]);
    }

    [Fact]
    public void No_settings_means_no_extra_filters()
        => Assert.Equal("", AudioFilters.ClipEffects(0, 0, 0, 10));

    [Fact]
    public void Volume_and_fades_become_ffmpeg_filters()
    {
        string effects = AudioFilters.ClipEffects(-6, 1.5, 2, 10);
        Assert.Equal(",volume=-6dB,afade=t=in:st=0:d=1.5,afade=t=out:st=8:d=2", effects);
    }

    [Fact]
    public void Fades_longer_than_the_clip_are_shortened()
        => Assert.Contains("afade=t=in:st=0:d=3", AudioFilters.ClipEffects(0, 5, 0, 3));

    [Fact]
    public void Export_uses_the_clip_settings()
    {
        var (project, clip) = ProjectWithSong();
        clip.VolumeDb = 3;
        clip.FadeOutSeconds = 1;

        var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = "o.mp4" });
        Assert.Contains("volume=3dB,afade=t=out:st=9:d=1", plan.FilterGraph);
    }

    [Fact]
    public void Cutting_keeps_the_fade_in_at_the_start_and_the_fade_out_at_the_end()
    {
        var (project, clip) = ProjectWithSong();
        clip.FadeInSeconds = 1;
        clip.FadeOutSeconds = 2;
        clip.VolumeDb = -3;

        Clip right = TimelineEdits.Split(project.TrackByName("A1"), clip, 150)!;

        Assert.Equal((1.0, 0.0), (clip.FadeInSeconds, clip.FadeOutSeconds));
        Assert.Equal((0.0, 2.0), (right.FadeInSeconds, right.FadeOutSeconds));
        Assert.Equal(-3, right.VolumeDb); // the volume stays on both halves
    }

    [Fact]
    public void Settings_are_saved_and_opened_again()
    {
        var (project, clip) = ProjectWithSong();
        clip.VolumeDb = -4.5;
        clip.FadeInSeconds = 0.5;
        clip.FadeOutSeconds = 1.25;

        string path = Path.Combine(Path.GetTempPath(), $"snit-audio-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Clip loaded = ProjectFile.Load(path).TrackByName("A1").Clips[0];
            Assert.Equal((-4.5, 0.5, 1.25), (loaded.VolumeDb, loaded.FadeInSeconds, loaded.FadeOutSeconds));
        }
        finally { File.Delete(path); }
    }
}
