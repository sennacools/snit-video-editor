using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Snit.Core;

/// <summary>One subtitle: shown from Start to End.</summary>
public sealed record CaptionLine(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Reading and writing .srt subtitle files, the most common kind. An .srt file is plain text:
/// <code>
/// 1
/// 00:00:01,000 --> 00:00:03,500
/// Hello there
///
/// 2
/// ...
/// </code>
/// </summary>
public static partial class Srt
{
    [GeneratedRegex(@"(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex Formatting();

    public static List<CaptionLine> Parse(string text)
    {
        var result = new List<CaptionLine>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = TimeLine().Match(lines[i]);
            if (!m.Success) continue;
            TimeSpan Time(int g) => new(0,
                int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));

            var body = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !TimeLine().IsMatch(lines[i + 1]))
                body.Add(Formatting().Replace(lines[++i].Trim(), ""));
            // A number line right before the next time line belongs to the next caption.
            if (body.Count > 0 && i + 2 < lines.Length && TimeLine().IsMatch(lines[i + 1]) && int.TryParse(body[^1], out _))
                body.RemoveAt(body.Count - 1);

            TimeSpan start = Time(1), end = Time(5);
            if (end > start && body.Count > 0) result.Add(new CaptionLine(start, end, string.Join("\n", body)));
        }
        return result.OrderBy(c => c.Start).ToList();
    }

    public static string Write(IEnumerable<CaptionLine> captions)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (CaptionLine c in captions.OrderBy(c => c.Start))
        {
            if (c.Text.Trim().Length == 0) continue;
            sb.Append(n++).Append("\r\n")
              .Append(Format(c.Start)).Append(" --> ").Append(Format(c.End)).Append("\r\n")
              .Append(c.Text.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string Format(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
}

/// <summary>The captions track (C1, at the very top): each clip on it is one subtitle.</summary>
public static class Captions
{
    public const string TrackName = "C1";

    /// <summary>The captions track, adding it at the top when the project has none.</summary>
    public static Track EnsureTrack(Project project)
    {
        if (project.CaptionTrack is Track existing) return existing;
        var track = new Track { Name = TrackName, Kind = TrackKind.Caption };
        project.Tracks.Insert(0, track);
        return track;
    }

    public static Clip MakeClip(long start, long length, string text) => new()
    {
        Media = MediaItem.Title, Start = start, Length = Math.Max(1, length),
        Title = TitleTemplates.CaptionStyle(text),
    };

    /// <summary>Puts subtitles on the captions track (replacing whatever is in their way). Returns how many.</summary>
    public static int Import(Project project, IEnumerable<CaptionLine> lines)
    {
        Track track = EnsureTrack(project);
        int count = 0;
        foreach (CaptionLine line in lines)
        {
            long start = (long)Math.Round(line.Start.TotalSeconds * project.Fps);
            long end = Math.Max(start + 1, (long)Math.Round(line.End.TotalSeconds * project.Fps));
            Clip clip = MakeClip(start, end - start, line.Text);
            TimelineEdits.ClearRange(track, clip.Start, clip.End, []);
            track.Clips.Add(clip);
            count++;
        }
        return count;
    }

    /// <summary>The captions as subtitle lines, with times counted from <paramref name="fromFrame"/> (for part exports).</summary>
    public static List<CaptionLine> Lines(Project project, long fromFrame = 0, long? toFrame = null)
    {
        if (project.CaptionTrack is not Track track) return [];
        return track.Clips
            .Where(c => c.Title is not null && c.End > fromFrame && (toFrame is null || c.Start < toFrame))
            .OrderBy(c => c.Start)
            .Select(c => new CaptionLine(
                TimeSpan.FromSeconds(Math.Max(0, c.Start - fromFrame) / project.Fps),
                TimeSpan.FromSeconds(((toFrame is long t ? Math.Min(c.End, t) : c.End) - fromFrame) / project.Fps),
                c.Title!.Text))
            .ToList();
    }
}
