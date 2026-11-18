using System.IO;
using Snit.Core;
using Snit.Services;

namespace Snit.Tests;

/// <summary>Recent projects and auto-save, in a temporary folder (never your real settings).</summary>
public class AutoSaveTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "snit-autosave-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string oldSettings = AppSettings.Folder, oldAutoSave = AutoSave.Folder;

    public AutoSaveTests()
    {
        Directory.CreateDirectory(folder);
        AppSettings.Folder = Path.Combine(folder, "settings");
        AutoSave.Folder = Path.Combine(folder, "autosave");
    }

    public void Dispose()
    {
        AppSettings.Folder = oldSettings;
        AutoSave.Folder = oldAutoSave;
        try { Directory.Delete(folder, true); } catch { }
    }

    private string MakeProjectFile(string name)
    {
        string path = Path.Combine(folder, name + ".snit");
        ProjectFile.Save(new Project(), path);
        return path;
    }

    [Fact]
    public void Recent_projects_keep_the_newest_first_without_doubles()
    {
        string a = MakeProjectFile("a"), b = MakeProjectFile("b");
        var settings = new AppSettings();
        settings.AddRecent(a);
        settings.AddRecent(b);
        settings.AddRecent(a);
        settings.Save();

        Assert.Equal([a, b], AppSettings.Load().RecentProjects);
    }

    [Fact]
    public void Auto_save_writes_a_copy_and_leaves_the_project_file_alone()
    {
        string path = MakeProjectFile("film");
        Project project = ProjectFile.Load(path);
        project.Markers.Add(new Marker(10, "unsaved change"));
        DateTime before = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);

        AutoSave.Write(project);

        Assert.Equal(path, project.FilePath);                       // still the same project
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));        // its file untouched
        string? copy = AutoSave.NewerCopyOf(path);
        Assert.NotNull(copy);
        Assert.Single(ProjectFile.Load(copy!).Markers);              // the copy has the change

        AutoSave.Delete(path);
        Assert.Null(AutoSave.NewerCopyOf(path));
    }

    [Fact]
    public void The_auto_save_name_is_the_same_every_time_Snit_starts()
    {
        string path = Path.Combine(folder, "same.snit");
        // Windows file names ignore capitals, so "same" and "SAME" must give the same copy.
        Assert.Equal(AutoSave.PathFor(path), AutoSave.PathFor(path.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("same (", Path.GetFileName(AutoSave.PathFor(path)));
        Assert.EndsWith(".autosave.snit", AutoSave.PathFor(null));
    }
}
