using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Saves and opens .snit project files. A .snit file is plain JSON text, so you can open it
/// in Notepad to see what's inside. It stores where the media files are, never the media itself.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".snit";
    public const string DialogFilter = "Snit project (*.snit)|*.snit";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // ---- What the file looks like ----

    private sealed record FileData(int Version, SettingsData Settings, List<MediaData> Media, List<TrackData> Tracks,
        long? InPoint = null, long? OutPoint = null, List<Marker>? Markers = null);
    private sealed record SettingsData(int Width, int Height, double FrameRate);
    private sealed record MediaData(Guid Id, string Path, string? RelativePath, MediaKind Kind,
        double Duration, int Width, int Height, double FrameRate, bool HasAudio);
    private sealed record TrackData(string Name, bool IsOff, bool IsLocked, List<ClipData> Clips);
    // A class (not a record) so that fields added later are simply 0 when opening older projects.
    private sealed class ClipData
    {
        public Guid Id { get; init; }
        public Guid MediaId { get; init; }
        public long Start { get; init; }
        public long In { get; init; }
        public long Length { get; init; }
        public Guid? LinkId { get; init; }
        public double VolumeDb { get; init; }
        public double FadeIn { get; init; }
        public double FadeOut { get; init; }
        public TitleData? Title { get; init; }
        public ClipLook? Look { get; init; }
        public double Speed { get; init; } = 100; // older files have no speed: normal
        public bool Reverse { get; init; }
        public Transition? TransitionIn { get; init; }
        public Transition? TransitionOut { get; init; }
        public Dictionary<string, List<Keyframe>>? Keyframes { get; init; }
        public List<ClipEffect>? Effects { get; init; }
    }

    /// <summary>The text and look of a title clip (titles have no media file).</summary>
    private sealed class TitleData
    {
        public string Text { get; init; } = "";
        public string FontFamily { get; init; } = "Segoe UI";
        public double FontSize { get; init; } = 96;
        public string Color { get; init; } = "#FFFFFF";
        public bool Bold { get; init; } = true;
        public bool Shadow { get; init; } = true;
        public double X { get; init; } = 0.5;
        public double Y { get; init; } = 0.5;
    }

    // ---- Save ----

    /// <param name="remember">False for copies (auto-save): the project keeps its own file name.</param>
    public static void Save(Project project, string path, bool remember = true)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var data = new FileData(
            CurrentVersion,
            new SettingsData(project.Settings.Width, project.Settings.Height, project.Settings.FrameRate),
            project.Media.Select(m => new MediaData(
                m.Id, m.FilePath, Path.GetRelativePath(folder, m.FilePath), m.Kind,
                m.DurationSeconds, m.Width, m.Height, m.FrameRate, m.HasAudio)).ToList(),
            project.Tracks.Select(t => new TrackData(
                t.Name, t.IsOff, t.IsLocked,
                t.Clips.OrderBy(c => c.Start)
                    .Select(c => new ClipData
                    {
                        Id = c.Id, MediaId = c.Media.Id, Start = c.Start, In = c.In, Length = c.Length, LinkId = c.LinkId,
                        VolumeDb = c.VolumeDb, FadeIn = c.FadeInSeconds, FadeOut = c.FadeOutSeconds,
                        Look = c.Look == ClipLook.Default ? null : c.Look, // only written when changed
                        Speed = c.SpeedPercent, Reverse = c.Reverse,
                        TransitionIn = c.TransitionIn, TransitionOut = c.TransitionOut,
                        Keyframes = c.Keyframes.Count == 0 ? null : c.Keyframes.ToDictionary(p => p.Key, p => p.Value.ToList()),
                        Effects = c.Effects.Count == 0 ? null : c.Effects.ToList(),
                        Title = c.Title is { } t ? new TitleData
                        {
                            Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, Color = t.Color,
                            Bold = t.Bold, Shadow = t.Shadow, X = t.X, Y = t.Y,
                        } : null,
                    }).ToList())).ToList(),
            project.InPoint, project.OutPoint, project.Markers);

        // Write to a temporary file first, so a crash halfway never leaves a broken project.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, path, overwrite: true);
        if (remember) project.FilePath = path;
    }

    // ---- Open ----

    public static Project Load(string path)
    {
        var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("The project file is empty.");
        if (data.Version > CurrentVersion)
            throw new InvalidDataException("This project was made with a newer version of Snit.");

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var project = new Project
        {
            FilePath = path,
            Settings = new ProjectSettings
            {
                Width = data.Settings.Width,
                Height = data.Settings.Height,
                FrameRate = data.Settings.FrameRate,
            },
        };

        project.InPoint = data.InPoint;
        project.OutPoint = data.OutPoint;
        project.Markers = (data.Markers ?? []).OrderBy(m => m.Frame).ToList();

        var mediaById = new Dictionary<Guid, MediaItem>();
        foreach (var m in data.Media)
        {
            // Prefer the original path; if the project folder was moved, try the path relative to it.
            string filePath = m.Path;
            if (!File.Exists(filePath) && m.RelativePath is not null)
            {
                string relative = Path.GetFullPath(Path.Combine(folder, m.RelativePath));
                if (File.Exists(relative)) filePath = relative;
            }

            var item = new MediaItem
            {
                Id = m.Id, FilePath = filePath, Kind = m.Kind, DurationSeconds = m.Duration,
                Width = m.Width, Height = m.Height, FrameRate = m.FrameRate, HasAudio = m.HasAudio,
                IsOffline = !File.Exists(filePath),
            };
            project.Media.Add(item);
            mediaById[item.Id] = item;
        }

        foreach (var t in data.Tracks)
        {
            if (t.Name.Length < 2 || (t.Name[0] != 'V' && t.Name[0] != 'A')) continue;
            Track track = project.EnsureTrack(t.Name); // adds V3, A3… when the project has them
            track.IsOff = t.IsOff;
            track.IsLocked = t.IsLocked;
            foreach (var c in t.Clips)
            {
                // Titles have no media file; they all use the stand-in "Title" item.
                MediaItem? media = c.Title is not null ? MediaItem.Title : mediaById.GetValueOrDefault(c.MediaId);
                if (media is null) continue;
                track.Clips.Add(new Clip
                {
                    Id = c.Id, Media = media, Start = c.Start, In = c.In, Length = c.Length, LinkId = c.LinkId,
                    VolumeDb = c.VolumeDb, FadeInSeconds = c.FadeIn, FadeOutSeconds = c.FadeOut,
                    Look = c.Look ?? ClipLook.Default,
                    SpeedPercent = c.Speed > 0 ? c.Speed : 100, Reverse = c.Reverse,
                    TransitionIn = c.TransitionIn, TransitionOut = c.TransitionOut,
                    Effects = c.Effects ?? [],
                    Keyframes = c.Keyframes is null ? Core.Keyframes.None
                        : c.Keyframes.ToDictionary(p => p.Key, p => (IReadOnlyList<Keyframe>)p.Value.OrderBy(k => k.Frame).ToList()),
                    Title = c.Title is { } d ? new TitleStyle
                    {
                        Text = d.Text, FontFamily = d.FontFamily, FontSize = d.FontSize, Color = d.Color,
                        Bold = d.Bold, Shadow = d.Shadow, X = d.X, Y = d.Y,
                    } : null,
                });
            }
        }
        return project;
    }
}
