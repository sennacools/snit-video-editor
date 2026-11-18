using System.IO;
using Snit.Core;
using Snit.Services;
using Snit.ViewModels;

namespace Snit.Tests;

/// <summary>Proxies: small copies for the preview; the export keeps using the originals.</summary>
public class ProxyTests(TestMedia media) : IClassFixture<TestMedia>
{
    [Fact]
    public void A_changed_file_gets_a_different_proxy_name()
    {
        string file = Path.Combine(Path.GetTempPath(), $"snit-proxyname-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(file, "one");
        try
        {
            string first = ProxyMaker.PathFor(file);
            Assert.Equal(first, ProxyMaker.PathFor(file));
            File.WriteAllText(file, "something longer");
            Assert.NotEqual(first, ProxyMaker.PathFor(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Making_a_proxy_switches_the_preview_but_not_the_export()
    {
        if (!media.Available) return;
        string oldFolder = ProxyMaker.Folder;
        ProxyMaker.Folder = Path.Combine(media.Folder, "proxies");
        try
        {
            var project = new Project();
            MediaItem video = await MediaImporter.ProbeAsync(media.Video30);
            project.Media.Add(video);
            var tl = new TimelineViewModel(project);
            tl.AddMedia(video, project.TrackByName("V1"), 0);
            var bin = new MediaBinViewModel(project, tl);
            int changes = 0;
            bin.ProxiesChanged += () => changes++;

            await bin.MakeProxiesAsync(project.Media.ToList());

            Assert.Equal(1, changes);
            Assert.True(video.HasProxy && File.Exists(video.ProxyPath));
            Assert.Equal(video.ProxyPath, ProxyMaker.Existing(video));   // found again next time

            // Same length as the original, so the preview stays in sync.
            MediaItem proxy = await MediaImporter.ProbeAsync(video.ProxyPath!);
            Assert.Equal(video.DurationSeconds, proxy.DurationSeconds, 1);

            Assert.Equal(video.ProxyPath, PreviewPlan.BuildLayers(project, useProxies: true)[0].Single().Path);
            Assert.Equal(video.FilePath, PreviewPlan.BuildLayers(project, useProxies: false)[0].Single().Path);

            var plan = ExportCommandBuilder.Build(project, new ExportSettings { OutputPath = Path.Combine(media.Folder, "proxy-export.mp4") });
            Assert.Contains(video.FilePath, plan.Arguments);
            Assert.DoesNotContain(video.ProxyPath, plan.Arguments);
        }
        finally { ProxyMaker.Folder = oldFolder; }
    }
}
