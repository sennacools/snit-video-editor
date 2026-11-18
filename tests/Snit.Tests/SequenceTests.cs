using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Several sequences in one project, and nested sequences (a sequence used as a clip).</summary>
public class SequenceTests(TestMedia media) : IClassFixture<TestMedia>
{
    private static MediaItem FakeVideo() => new()
    {
        FilePath = @"C:\fake\v.mp4", Kind = MediaKind.Video, DurationSeconds = 10, FrameRate = 30, HasAudio = true, Width = 1920, Height = 1080,
    };

    [Fact]
    public void The_project_shows_the_open_sequence()
    {
        var project = new Project();
        var second = new Sequence { Name = "B-roll", Settings = new ProjectSettings { Width = 1080, Height = 1920 } };
        project.Sequences.Add(second);

        project.ActiveSequence = second;
        Assert.Equal(1080, project.Settings.Width);
        project.Markers = [new Marker(5, "x")];
        Assert.Same(second.Tracks, project.Tracks);

        int width = project.With(project.Sequences[0], () => project.Settings.Width);
        Assert.Equal(1920, width);
        Assert.Same(second, project.ActiveSequence);           // put back afterwards
        Assert.Empty(project.Sequences[0].Markers);
        Assert.Throws<ArgumentException>(() => project.ActiveSequence = new Sequence());
    }

    [Fact]
    public void All_sequences_are_saved_and_older_files_still_open()
    {
        var project = new Project();
        project.Media.Add(FakeVideo());
        new TimelineViewModel(project).AddMedia(project.Media[0], project.TrackByName("V1"), 0);
        var second = new Sequence { Name = "Second", Settings = new ProjectSettings { FrameRate = 25 } };
        project.Sequences.Add(second);
        project.ActiveSequence = second;
        project.TrackByName("V2").Clips.Add(new Clip { Media = project.Media[0], Start = 10, Length = 20 });

        string path = Path.Combine(Path.GetTempPath(), $"snit-seq-{Guid.NewGuid():N}.snit");
        try
        {
            ProjectFile.Save(project, path);
            Project loaded = ProjectFile.Load(path);
            Assert.Equal(["Main sequence", "Second"], loaded.Sequences.Select(s => s.Name));
            Assert.Equal("Second", loaded.ActiveSequence.Name);
            Assert.Equal(25, loaded.Fps);
            Assert.Equal(10, loaded.TrackByName("V2").Clips.Single().Start);
            Assert.Equal(2, loaded.Sequences[0].AllClips.Count()); // video + its sound

            // A file from before sequences: no "sequences" list.
            string old = """
                { "version": 1, "settings": { "width": 1280, "height": 720, "frameRate": 24 }, "media": [],
                  "tracks": [ { "name": "V1", "isOff": false, "isLocked": false, "clips": [] } ], "markers": [ { "frame": 3, "name": "m" } ] }
                """;
            File.WriteAllText(path, old);
            Project older = ProjectFile.Load(path);
            Assert.Equal(("Main sequence", 1280, 24.0, 3L), (older.ActiveSequence.Name, older.Settings.Width, older.Fps, older.Markers[0].Frame));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Nesting_moves_the_selected_clips_into_a_new_sequence()
    {
        var project = new Project();
        MediaItem video = FakeVideo();
        project.Media.Add(video);
        var tl = new TimelineViewModel(project);
        tl.AddMedia(video, project.TrackByName("V1"), 30);    // 30–330, sound on A1
        tl.AddTitleAtPlayheadCommand.Execute(null);             // a title on V2 at 0–150
        Clip title = project.TrackByName("V2").Clips.Single();
        tl.Select(project.TrackByName("V1").Clips.Single(), add: false);
        tl.Select(title, add: true);

        Sequence inner = tl.NestSelection("Intro")!;

        // Inside: the same clips, starting from 0.
        Assert.Equal("Intro", inner.Name);
        Assert.Equal(330, inner.DurationFrames);
        Assert.Equal(30, inner.Tracks.Single(t => t.Name == "V1").Clips.Single().Start);
        Assert.Equal(0, inner.Tracks.Single(t => t.Name == "V2").Clips.Single().Start);

        // Outside: one nested clip (with linked sound) where they were.
        MediaItem item = project.MediaFor(inner)!;
        Assert.Equal("Intro", item.Name);
        Clip nested = project.TrackByName("V1").Clips.Single();
        Clip nestedSound = project.TrackByName("A1").Clips.Single();
        Assert.Equal((item, 0L, 330L), (nested.Media, nested.Start, nested.Length));
        Assert.Equal(nested.LinkId, nestedSound.LinkId);
        Assert.Empty(project.TrackByName("V2").Clips);

        // A sequence can't go inside itself.
        project.ActiveSequence = inner;
        Assert.False(tl.CanPlace(item));
        project.ActiveSequence = project.Sequences[0];
        Assert.True(tl.CanPlace(item));
    }

    [Fact]
    public void The_fingerprint_changes_when_the_sequence_changes()
    {
        var project = new Project();
        project.TrackByName("V1").Clips.Add(new Clip { Media = MediaItem.Title, Length = 30, Title = new TitleStyle() });
        string before = ProjectFile.Fingerprint(project, project.ActiveSequence);
        Assert.Equal(before, ProjectFile.Fingerprint(project, project.ActiveSequence));
        project.TrackByName("V1").Clips[0].Title!.Text = "Changed";
        Assert.NotEqual(before, ProjectFile.Fingerprint(project, project.ActiveSequence));
    }

    [Fact]
    public async Task A_nested_sequence_is_rendered_and_shows_up_in_the_export()
    {
        if (!media.Available) return;
        string oldFolder = NestedSequences.Folder;
        NestedSequences.Folder = Path.Combine(media.Folder, "nested");
        try
        {
            var project = new Project();
            project.Settings.Width = 320;
            project.Settings.Height = 180;
            MediaItem white = await MediaImporter.ProbeAsync(media.White);
            MediaItem red = await MediaImporter.ProbeAsync(media.Red);
            project.Media.Add(white);
            project.Media.Add(red);
            var tl = new TimelineViewModel(project);
            tl.AddMedia(white, project.TrackByName("V1"), 0);
            tl.Select(project.TrackByName("V1").Clips.Single(), add: false);
            Sequence inner = tl.NestSelection("White")!;
            MediaItem item = project.MediaFor(inner)!;
            Assert.True(item.IsOffline);                         // not rendered yet

            Assert.Equal(1, await NestedSequences.EnsureRenderedAsync(project, project.ActiveSequence));
            Assert.False(item.IsOffline);
            Assert.True(File.Exists(item.FilePath));
            Assert.Equal(0, await NestedSequences.EnsureRenderedAsync(project, project.ActiveSequence)); // up to date

            async Task<(int R, int G, int B)> ExportedCentre(string name)
            {
                string output = Path.Combine(media.Folder, name + ".mp4");
                var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = output, Width = 320, Height = 180 });
                await new Exporter().RunAsync(plan, null, CancellationToken.None);
                var result = await ProcessRunner.RunAsync(FFmpegLocator.Current.FfmpegPath!,
                    ["-v", "error", "-i", output, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
                int i = (90 * 320 + 160) * 3;
                return (result.Output[i], result.Output[i + 1], result.Output[i + 2]);
            }

            var (r, g, b) = await ExportedCentre("outer-white");
            Assert.True(r > 220 && g > 220 && b > 220, $"Expected the nested white, got {(r, g, b)}.");

            // Change the inside: it is rendered again, and the outside follows.
            string firstRender = item.FilePath;
            inner.Tracks.Single(t => t.Name == "V1").Clips[0].Media = red;
            Assert.Equal(1, await NestedSequences.EnsureRenderedAsync(project, project.ActiveSequence));
            Assert.NotEqual(firstRender, item.FilePath);
            Assert.False(File.Exists(firstRender));             // the old render is cleaned up
            (r, g, b) = await ExportedCentre("outer-red");
            Assert.True(r > 200 && g < 60 && b < 60, $"Expected the nested red, got {(r, g, b)}.");
        }
        finally { NestedSequences.Folder = oldFolder; }
    }
}
