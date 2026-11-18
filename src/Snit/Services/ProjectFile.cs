using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Saves and opens .snit project files. A .snit file is plain JSON text, so you can open it
/// in Notepad to see what's inside. It stores where the media files are, never the media itself.
///
/// The main sequence is written at the top level (as in the first Snit versions, which can still
/// open it); all sequences are also listed under "sequences".
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

    private static readonly JsonSerializerOptions CompactOptions = new(JsonOptions) { WriteIndented = false };

    // ---- What the file looks like ----

    private sealed record FileData(int Version, SettingsData Settings, List<MediaData> Media, List<TrackData> Tracks,
        long? InPoint = null, long? OutPoint = null, List<Marker>? Markers = null,
        List<SequenceData>? Sequences = null, Guid? ActiveSequence = null);
    private sealed record SequenceData(Guid Id, string Name, SettingsData Settings, List<TrackData> Tracks,
        long? InPoint = null, long? OutPoint = null, List<Marker>? Markers = null);
    private sealed record SettingsData(int Width, int Height, double FrameRate);
    private sealed record MediaData(Guid Id, string Path, string? RelativePath, MediaKind Kind,
        double Duration, int Width, int Height, double FrameRate, bool HasAudio, Guid? SequenceId = null);
    private sealed record TrackData(string Name, bool IsOff, bool IsLocked, List<ClipData> Clips,
        double VolumeDb = 0, double Pan = 0, bool IsSolo = false);
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
        public ClipAudio? Audio { get; init; }
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
        public string Box { get; init; } = "";
        public double X { get; init; } = 0.5;
        public double Y { get; init; } = 0.5;
    }

    // ---- Save ----

    private static SettingsData Settings(ProjectSettings s) => new(s.Width, s.Height, s.FrameRate);

    private static List<TrackData> Tracks(Sequence sequence)
        => sequence.Tracks.Select(t => new TrackData(
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
                    Audio = c.Audio == ClipAudio.Default ? null : c.Audio,
                    Title = c.Title is { } t ? new TitleData
                    {
                        Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, Color = t.Color,
                        Bold = t.Bold, Shadow = t.Shadow, Box = t.Box, X = t.X, Y = t.Y,
                    } : null,
                }).ToList(), t.VolumeDb, t.Pan, t.IsSolo)).ToList();

    private static SequenceData Data(Sequence s)
        => new(s.Id, s.Name, Settings(s.Settings), Tracks(s), s.InPoint, s.OutPoint, s.Markers);

    /// <param name="remember">False for copies (auto-save): the project keeps its own file name.</param>
    public static void Save(Project project, string path, bool remember = true)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Sequence main = project.Sequences[0];

        var data = new FileData(
            CurrentVersion,
            Settings(main.Settings),
            project.Media.Select(m => new MediaData(
                m.Id, m.FilePath, m.IsSequence ? null : Path.GetRelativePath(folder, m.FilePath), m.Kind,
                m.DurationSeconds, m.Width, m.Height, m.FrameRate, m.HasAudio, m.SequenceId)).ToList(),
            Tracks(main),
            main.InPoint, main.OutPoint, main.Markers,
            project.Sequences.Select(Data).ToList(),
            project.ActiveSequence.Id);

        // Write to a temporary file first, so a crash halfway never leaves a broken project.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, path, overwrite: true);
        if (remember) project.FilePath = path;
    }

    /// <summary>
    /// A short code that changes whenever anything in the sequence changes (clips, settings, the
    /// files they use). Used to know when a nested sequence must be rendered again.
    /// </summary>
    public static string Fingerprint(Project project, Sequence sequence)
    {
        var files = sequence.AllClips.Select(c => c.Media).Distinct()
            .Where(m => m != MediaItem.Title)
            .Select(m =>
            {
                var info = new FileInfo(m.FilePath);
                return $"{m.Id}|{m.FilePath}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
            })
            .Order();
        SequenceData data = Data(sequence) with { Name = "", InPoint = null, OutPoint = null, Markers = null };
        string text = JsonSerializer.Serialize(data, CompactOptions) + string.Join("\n", files);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    // ---- Open ----

    public static Project Load(string path)
    {
        var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("The project file is empty.");
        if (data.Version > CurrentVersion)
            throw new InvalidDataException("This project was made with a newer version of Snit.");

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var project = new Project { FilePath = path };

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
                SequenceId = m.SequenceId,
                IsOffline = !File.Exists(filePath),
            };
            project.Media.Add(item);
            mediaById[item.Id] = item;
        }

        // Older files have one sequence, at the top level.
        var sequences = data.Sequences is { Count: > 0 }
            ? data.Sequences
            : [new SequenceData(Guid.NewGuid(), "Main sequence", data.Settings, data.Tracks, data.InPoint, data.OutPoint, data.Markers)];

        project.Sequences.Clear();
        foreach (SequenceData s in sequences)
        {
            var sequence = new Sequence
            {
                Id = s.Id, Name = s.Name,
                Settings = new ProjectSettings { Width = s.Settings.Width, Height = s.Settings.Height, FrameRate = s.Settings.FrameRate },
                InPoint = s.InPoint, OutPoint = s.OutPoint,
                Markers = (s.Markers ?? []).OrderBy(m => m.Frame).ToList(),
            };
            project.Sequences.Add(sequence);
            project.With(sequence, () => LoadTracks(project, s.Tracks, mediaById));
        }
        project.ActiveSequence = project.Sequences.FirstOrDefault(s => s.Id == data.ActiveSequence) ?? project.Sequences[0];

        // A bin item whose sequence is gone (shouldn't happen) is dropped.
        foreach (MediaItem orphan in project.Media.Where(m => m.IsSequence && project.SequenceOf(m) is null).ToList())
            project.Media.Remove(orphan);
        return project;
    }

    private static bool LoadTracks(Project project, List<TrackData> tracks, Dictionary<Guid, MediaItem> mediaById)
    {
        foreach (var t in tracks)
        {
            if (t.Name.Length < 2 || (t.Name[0] != 'V' && t.Name[0] != 'A' && t.Name[0] != 'C')) continue;
            Track track = t.Name[0] == 'C'
                ? Captions.EnsureTrack(project)
                : project.EnsureTrack(t.Name); // adds V3, A3… when the project has them
            track.IsOff = t.IsOff;
            track.IsLocked = t.IsLocked;
            track.VolumeDb = t.VolumeDb;
            track.Pan = t.Pan;
            track.IsSolo = t.IsSolo;
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
                    Audio = c.Audio ?? ClipAudio.Default,
                    Keyframes = c.Keyframes is null ? Core.Keyframes.None
                        : c.Keyframes.ToDictionary(p => p.Key, p => (IReadOnlyList<Keyframe>)p.Value.OrderBy(k => k.Frame).ToList()),
                    Title = c.Title is { } d ? new TitleStyle
                    {
                        Text = d.Text, FontFamily = d.FontFamily, FontSize = d.FontSize, Color = d.Color,
                        Bold = d.Bold, Shadow = d.Shadow, Box = d.Box ?? "", X = d.X, Y = d.Y,
                    } : null,
                });
            }
        }
        return true;
    }
}
