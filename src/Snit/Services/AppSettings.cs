using System.IO;
using System.Text.Json;

namespace Snit.Services;

/// <summary>
/// Snit's own settings, kept between runs: the list of recent projects. Stored as a small JSON
/// file in %APPDATA%\Snit. If the file can't be read, Snit just starts with an empty list.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentProjects = 10;

    public List<string> RecentProjects { get; set; } = [];

    /// <summary>Where the settings live. Tests point this somewhere else.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snit");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* damaged file: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* not being able to remember recent files is not worth an error message */ }
    }

    /// <summary>Puts a project at the top of the recent list (and forgets files that no longer exist).</summary>
    public void AddRecent(string path)
    {
        string full = Path.GetFullPath(path);
        RecentProjects.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase) || !File.Exists(p));
        RecentProjects.Insert(0, full);
        if (RecentProjects.Count > MaxRecentProjects) RecentProjects.RemoveRange(MaxRecentProjects, RecentProjects.Count - MaxRecentProjects);
    }
}

/// <summary>
/// Auto-save: copies of unsaved projects in %LOCALAPPDATA%\Snit\AutoSave, one per project.
/// The project's own file is never touched.
/// </summary>
public static class AutoSave
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snit", "AutoSave");

    /// <summary>The auto-save copy for a project ("Untitled" when it was never saved).</summary>
    public static string PathFor(string? projectPath)
    {
        string name = projectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(projectPath);
        // A short code from the full path, so two projects with the same name don't share a copy.
        // (SHA-256, because .NET's own GetHashCode changes every time the program starts.)
        string code = projectPath is null ? "new" : Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToLowerInvariant())))[..8].ToLowerInvariant();
        return Path.Combine(Folder, $"{name} ({code}).autosave.snit");
    }

    public static void Write(Core.Project project)
    {
        Directory.CreateDirectory(Folder);
        ProjectFile.Save(project, PathFor(project.FilePath), remember: false);
    }

    /// <summary>The auto-save copy if it is newer than the project file itself, otherwise null.</summary>
    public static string? NewerCopyOf(string projectPath)
    {
        string copy = PathFor(projectPath);
        return File.Exists(copy) && File.GetLastWriteTimeUtc(copy) > File.GetLastWriteTimeUtc(projectPath) ? copy : null;
    }

    public static void Delete(string? projectPath)
    {
        try { File.Delete(PathFor(projectPath)); } catch (Exception) { }
    }
}
