using System.IO;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Everything needed to run one export: the ffmpeg arguments, how many frames to expect,
/// and temporary files (title pictures) to delete afterwards.
/// </summary>
public sealed record ExportPlan(IReadOnlyList<string> Arguments, string FilterGraph, long TotalFrames, double DurationSeconds,
    IReadOnlyList<string> TempFiles);

/// <summary>
/// Turns the timeline into one ffmpeg command.
///
/// How it works:
/// 1. Every clip becomes an input, cut to the right part of its file ("-ss" start, "-t" length).
/// 2. Video: a black background the length of the timeline, with each clip resized to the
///    export size and laid on top at its place in time. Lower tracks first, so V2 covers V1.
///    Titles are drawn into see-through PNG pictures first and laid on top like images.
/// 3. Audio: every clip's sound is delayed to its place and all of it is mixed together.
/// The recipe for steps 2 and 3 is ffmpeg's "filter_complex" text.
/// </summary>
public static class ExportCommandBuilder
{
    private const int SampleRate = 48000;

    /// <param name="workFolder">Where to put title pictures (a new temporary folder when left out).</param>
    public static ExportPlan Build(Project project, ExportSettings settings, string? workFolder = null)
    {
        if (settings.Format != ExportFormat.Mp4H264)
            throw new NotSupportedException("Only MP4 (H.264) export is available so far.");

        double projectFps = project.Fps;
        if (project.DurationFrames <= 0)
            throw new InvalidOperationException("The timeline is empty.");

        // The part to export: the whole timeline, or the in/out range.
        long rangeStart = Math.Max(0, settings.RangeStart ?? 0);
        long rangeEnd = Math.Min(project.DurationFrames, settings.RangeEnd ?? project.DurationFrames);
        if (rangeEnd <= rangeStart)
            throw new InvalidOperationException("The in/out range is empty.");
        bool isRange = rangeStart > 0 || rangeEnd < project.DurationFrames;
        double timelineEnd = rangeEnd / projectFps;               // everything is built up to here...
        double duration = (rangeEnd - rangeStart) / projectFps;  // ...and this much is kept
        bool InRange(Clip c) => c.End > rangeStart && c.Start < rangeEnd;

        var missing = project.AllClips.Where(c => c.Media.IsOffline).Select(c => c.Media.Name).Distinct().ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Some media files are missing: " + string.Join(", ", missing));

        int w = settings.Width, h = settings.Height;
        string fps = ProcessRunner.Num(settings.FrameRate);
        string Sec(long frames) => ProcessRunner.Num(frames / projectFps);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var filters = new List<string>();
        var tempFiles = new List<string>();
        int input = 0;

        // Titles are drawn at the size the video frame will have inside the export,
        // so letters are sharp (also in 4K) and sit exactly where the preview shows them.
        double fit = Math.Min((double)w / project.Settings.Width, (double)h / project.Settings.Height);
        int titleW = (int)Math.Round(project.Settings.Width * fit), titleH = (int)Math.Round(project.Settings.Height * fit);

        // ---- Video ----
        filters.Add($"color=c=black:s={w}x{h}:r={fps}:d={ProcessRunner.Num(timelineEnd)}[base]");
        string current = "base";

        // Bottom track first, so higher tracks end up on top.
        var videoTracks = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsOff).Reverse();
        foreach (Clip clip in videoTracks.SelectMany(t => t.Clips.Where(InRange).OrderBy(c => c.Start)))
        {
            bool isStill = clip.Media.Kind is MediaKind.Image or MediaKind.Text;
            if (clip.Title is TitleStyle title)
            {
                workFolder ??= Directory.CreateTempSubdirectory("snit-export-").FullName;
                string png = Path.Combine(workFolder, $"title-{input}.png");
                TitleRenderer.SavePng(title, titleW, titleH, png);
                tempFiles.Add(png);
                args.AddRange(["-loop", "1", "-framerate", fps, "-t", Sec(clip.Length), "-i", png]);
            }
            else if (isStill)
                args.AddRange(["-loop", "1", "-framerate", fps, "-t", Sec(clip.Length), "-i", clip.Media.FilePath]);
            else
                // A sped-up clip reads more of the file: SourceLength frames, shown in Length frames.
                args.AddRange(["-ss", Sec(clip.In), "-t", Sec(clip.SourceLength), "-i", clip.Media.FilePath]);

            // 1. Speed and reverse (not for pictures and titles), then the export frame rate, cut to the exact length.
            string speed = isStill ? "" : VideoFilters.VideoSpeed(clip.Speed, clip.Reverse);
            // 2. Fit inside the frame (keeping its shape), then colour, then size/rotation/opacity.
            //    Pictures and titles keep their see-through parts.
            string label = $"v{input}";
            filters.Add(
                $"[{input}:v:0]{speed.TrimStart(',')}{(speed.Length > 0 ? "," : "")}fps={fps},trim=duration={Sec(clip.Length)}," +
                $"scale={w}:{h}:force_original_aspect_ratio=decrease,setsar=1" +
                $"{VideoFilters.Color(clip.Look)}{VideoFilters.Transform(clip.Look, keepTransparency: isStill)}," +
                $"setpts=PTS-STARTPTS+{Sec(clip.Start)}/TB[{label}]");

            // 3. Lay it on top, centred and then moved by the clip's position (measured in the
            //    project frame, so scaled to the export size). eof_action=pass: once the clip
            //    ends, the picture underneath shows again.
            string dx = ProcessRunner.Num(Math.Round(clip.Look.PositionX * fit, 2));
            string dy = ProcessRunner.Num(Math.Round(clip.Look.PositionY * fit, 2));
            string next = $"o{input}";
            filters.Add($"[{current}][{label}]overlay=x=(main_w-overlay_w)/2+({dx}):y=(main_h-overlay_h)/2+({dy})" +
                        $":eof_action=pass:format=yuv420[{next}]");
            current = next;
            input++;
        }
        // Keep only the chosen range (and start its timestamps at zero).
        string keepRange = isRange ? $"trim=start={Sec(rangeStart)}:end={Sec(rangeEnd)},setpts=PTS-STARTPTS," : "";
        filters.Add($"[{current}]{keepRange}format=yuv420p[vout]");

        // ---- Audio ----
        var audioClips = project.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.IsOff)
            .SelectMany(t => t.Clips.Where(InRange).OrderBy(c => c.Start)).ToList();
        var audioLabels = new List<string>();
        foreach (Clip clip in audioClips)
        {
            args.AddRange(["-ss", Sec(clip.In), "-t", Sec(clip.SourceLength), "-i", clip.Media.FilePath]);
            long delayMs = (long)Math.Round(clip.Start / projectFps * 1000);
            string label = $"a{input}";
            // Speed/reverse first (so fades are measured on the timeline), then volume and fades.
            string speed = VideoFilters.AudioSpeed(clip.Speed, clip.Reverse);
            string effects = AudioFilters.ClipEffects(clip.VolumeDb, clip.FadeInSeconds, clip.FadeOutSeconds, clip.Length / projectFps);
            filters.Add($"[{input}:a:0]aresample={SampleRate},aformat=channel_layouts=stereo{speed}{effects}," +
                        $"atrim=0:{Sec(clip.Length)},adelay={delayMs}:all=1[{label}]");
            audioLabels.Add(label);
            input++;
        }

        string exactLength = $"apad,atrim=0:{ProcessRunner.Num(timelineEnd)}" +
                             (isRange ? $",atrim=start={Sec(rangeStart)},asetpts=PTS-STARTPTS" : "");
        if (audioLabels.Count == 0)
            filters.Add($"anullsrc=r={SampleRate}:cl=stereo,atrim=0:{ProcessRunner.Num(duration)}[aout]");
        else
            filters.Add($"{string.Concat(audioLabels.Select(l => $"[{l}]"))}" +
                        $"amix=inputs={audioLabels.Count}:normalize=0:dropout_transition=0,{exactLength}[aout]");

        string graph = string.Join(";\n", filters);

        // ---- Output ----
        args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(VideoEncoderArguments(settings));
        args.AddRange(["-r", fps,
                       "-c:a", "aac", "-b:a", $"{settings.AudioBitrateKbps}k", "-ar", SampleRate.ToString(),
                       "-movflags", "+faststart",
                       "-progress", "pipe:1", "-nostats",
                       settings.OutputPath]);

        long totalFrames = (long)Math.Round(duration * settings.FrameRate);
        return new ExportPlan(args, graph, totalFrames, duration, tempFiles);
    }

    /// <summary>Settings for the chosen video encoder. The bitrate is the "quality" slider.</summary>
    private static IEnumerable<string> VideoEncoderArguments(ExportSettings s)
    {
        string rate = ProcessRunner.Num(s.VideoBitrateMbps) + "M";
        string max = ProcessRunner.Num(s.VideoBitrateMbps * 1.5) + "M";
        string buffer = ProcessRunner.Num(s.VideoBitrateMbps * 2) + "M";
        var common = new[] { "-b:v", rate, "-maxrate", max, "-bufsize", buffer };

        return s.VideoEncoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", .. common, "-pix_fmt", "yuv420p"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", .. common, "-pix_fmt", "nv12"],
            "h264_amf" => ["-c:v", "h264_amf", "-quality", "balanced", .. common, "-pix_fmt", "yuv420p"],
            _ => ["-c:v", "libx264", "-preset", "medium", "-profile:v", "high", .. common, "-pix_fmt", "yuv420p"],
        };
    }
}
