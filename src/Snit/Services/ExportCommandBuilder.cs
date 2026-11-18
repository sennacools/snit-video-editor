using Snit.Core;

namespace Snit.Services;

/// <summary>Everything needed to run one export: the ffmpeg arguments and how many frames to expect.</summary>
public sealed record ExportPlan(IReadOnlyList<string> Arguments, string FilterGraph, long TotalFrames, double DurationSeconds);

/// <summary>
/// Turns the timeline into one ffmpeg command.
///
/// How it works:
/// 1. Every clip becomes an input, cut to the right part of its file ("-ss" start, "-t" length).
/// 2. Video: a black background the length of the timeline, with each clip resized to the
///    export size and laid on top at its place in time. Lower tracks first, so V2 covers V1.
/// 3. Audio: every clip's sound is delayed to its place and all of it is mixed together.
/// The recipe for steps 2 and 3 is ffmpeg's "filter_complex" text.
/// </summary>
public static class ExportCommandBuilder
{
    private const int SampleRate = 48000;

    public static ExportPlan Build(Project project, ExportSettings settings)
    {
        if (settings.Format != ExportFormat.Mp4H264)
            throw new NotSupportedException("Only MP4 (H.264) export is available so far.");

        double projectFps = project.Fps;
        double duration = project.DurationFrames / projectFps;
        if (duration <= 0)
            throw new InvalidOperationException("The timeline is empty.");

        var missing = project.AllClips.Where(c => c.Media.IsOffline).Select(c => c.Media.Name).Distinct().ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("Some media files are missing: " + string.Join(", ", missing));

        int w = settings.Width, h = settings.Height;
        string fps = ProcessRunner.Num(settings.FrameRate);
        string Sec(long frames) => ProcessRunner.Num(frames / projectFps);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        var filters = new List<string>();
        int input = 0;

        // ---- Video ----
        filters.Add($"color=c=black:s={w}x{h}:r={fps}:d={ProcessRunner.Num(duration)}[base]");
        string current = "base";

        // Bottom track first, so higher tracks end up on top.
        var videoTracks = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsOff).Reverse();
        foreach (Clip clip in videoTracks.SelectMany(t => t.Clips.OrderBy(c => c.Start)))
        {
            if (clip.Media.Kind == MediaKind.Image)
                args.AddRange(["-loop", "1", "-framerate", fps, "-t", Sec(clip.Length), "-i", clip.Media.FilePath]);
            else
                args.AddRange(["-ss", Sec(clip.In), "-t", Sec(clip.Length), "-i", clip.Media.FilePath]);

            string label = $"v{input}";
            filters.Add(
                $"[{input}:v:0]fps={fps}," +
                $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1," +
                $"format=yuv420p,setpts=PTS-STARTPTS+{Sec(clip.Start)}/TB[{label}]");

            string next = $"o{input}";
            // eof_action=pass: once the clip ends, the picture underneath shows again.
            filters.Add($"[{current}][{label}]overlay=eof_action=pass:format=yuv420[{next}]");
            current = next;
            input++;
        }
        filters.Add($"[{current}]format=yuv420p[vout]");

        // ---- Audio ----
        var audioClips = project.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.IsOff)
            .SelectMany(t => t.Clips.OrderBy(c => c.Start)).ToList();
        var audioLabels = new List<string>();
        foreach (Clip clip in audioClips)
        {
            args.AddRange(["-ss", Sec(clip.In), "-t", Sec(clip.Length), "-i", clip.Media.FilePath]);
            long delayMs = (long)Math.Round(clip.Start / projectFps * 1000);
            string label = $"a{input}";
            string effects = AudioFilters.ClipEffects(clip.VolumeDb, clip.FadeInSeconds, clip.FadeOutSeconds, clip.Length / projectFps);
            filters.Add($"[{input}:a:0]aresample={SampleRate},aformat=channel_layouts=stereo{effects},adelay={delayMs}:all=1[{label}]");
            audioLabels.Add(label);
            input++;
        }

        string exactLength = $"apad,atrim=0:{ProcessRunner.Num(duration)}";
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
        return new ExportPlan(args, graph, totalFrames, duration);
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
