using System.IO;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Nested sequences: a sequence used as a clip inside another sequence.
///
/// How it works: the nested sequence is rendered to a high-quality video file (in
/// %LOCALAPPDATA%\Snit\Nested), and the clip plays that file like any other video. The file name
/// contains a fingerprint of the sequence, so as soon as you change something inside it, the
/// name changes and Snit renders it again (when you go back to the outer sequence, and always
/// before an export). Nested sequences inside nested sequences are rendered deepest first.
/// </summary>
public static class NestedSequences
{
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snit", "Nested");

    /// <summary>The bin item that stands for a sequence (made when it doesn't exist yet), with its size and length up to date.</summary>
    public static MediaItem EnsureMediaItem(Project project, Sequence sequence)
    {
        MediaItem? item = project.MediaFor(sequence);
        if (item is null)
        {
            item = new MediaItem
            {
                FilePath = PathFor(project, sequence), Kind = MediaKind.Video, HasAudio = true,
                SequenceId = sequence.Id, IsOffline = true,
            };
            project.Media.Add(item);
        }
        item.DisplayName = sequence.Name;
        item.Width = sequence.Settings.Width;
        item.Height = sequence.Settings.Height;
        item.FrameRate = sequence.Settings.FrameRate;
        item.DurationSeconds = sequence.DurationFrames / sequence.Settings.FrameRate;
        return item;
    }

    /// <summary>Where the render of the sequence as it is now goes.</summary>
    public static string PathFor(Project project, Sequence sequence)
        => Path.Combine(Folder, $"{sequence.Id:N}-{ProjectFile.Fingerprint(project, sequence)}.mp4");

    /// <summary>The sequences used as clips directly inside a sequence.</summary>
    public static IEnumerable<Sequence> Children(Project project, Sequence sequence)
        => sequence.AllClips.Select(c => c.Media).Where(m => m.IsSequence).Distinct()
            .Select(project.SequenceOf).OfType<Sequence>();

    /// <summary>Would putting <paramref name="inner"/> inside <paramref name="outer"/> make a loop (a sequence inside itself)?</summary>
    public static bool WouldLoop(Project project, Sequence outer, Sequence inner, int depth = 0)
        => inner == outer || depth > 50 || Children(project, inner).Any(c => WouldLoop(project, outer, c, depth + 1));

    /// <summary>Whether a sequence is used inside another one.</summary>
    public static bool IsUsed(Project project, Sequence sequence)
        => project.Sequences.Any(s => s != sequence && Children(project, s).Contains(sequence));

    /// <summary>
    /// Renders the nested sequences used in <paramref name="root"/> that changed since their last
    /// render (deepest first), and points their bin items at the new files. Returns how many were rendered.
    /// </summary>
    public static async Task<int> EnsureRenderedAsync(Project project, Sequence root, IProgress<string>? status = null,
        CancellationToken cancel = default)
    {
        int rendered = 0;
        var done = new HashSet<Guid>();

        async Task Visit(Sequence sequence, int depth)
        {
            foreach (Sequence child in Children(project, sequence).ToList())
            {
                if (depth > 50 || !done.Add(child.Id)) continue;
                await Visit(child, depth + 1);

                MediaItem item = EnsureMediaItem(project, child);
                if (child.DurationFrames == 0)
                {
                    item.IsOffline = true; // nothing in it yet
                    continue;
                }
                string path = PathFor(project, child);
                if (!File.Exists(path))
                {
                    status?.Report($"Rendering “{child.Name}”…");
                    await RenderAsync(project, child, path, cancel);
                    rendered++;
                }
                item.FilePath = path;
                item.IsOffline = false;
            }
        }

        await Visit(root, 0);
        return rendered;
    }

    private static async Task RenderAsync(Project project, Sequence sequence, string path, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        string temp = path + ".part.mp4";
        ProjectSettings s = sequence.Settings;

        // High quality (it gets compressed once more in the final export).
        double megapixels = s.Width * (double)s.Height / 1_000_000;
        ExportPlan plan = project.With(sequence, () => ExportCommandBuilder.Build(project, new ExportSettings
        {
            OutputPath = temp, Width = s.Width, Height = s.Height, FrameRate = s.FrameRate,
            VideoBitrateMbps = Math.Clamp(megapixels * 20, 10, 150), VideoEncoder = "libx264",
        }));
        await new Exporter().RunAsync(plan, null, cancel); // (also cleans up its own temporary files)
        File.Move(temp, path, overwrite: true);

        // Older renders of this sequence are no longer needed.
        foreach (string old in Directory.GetFiles(Folder, $"{sequence.Id:N}-*.mp4"))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
