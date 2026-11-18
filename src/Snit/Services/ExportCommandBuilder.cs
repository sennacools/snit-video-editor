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

        // GIFs have no sound and MP3s have no picture: those parts are left out completely.
        bool wantVideo = settings.Format != ExportFormat.Mp3;
        bool wantAudio = settings.Format != ExportFormat.Gif;

        // ---- Video ----
        filters.Add($"color=c=black:s={w}x{h}:r={fps}:d={ProcessRunner.Num(timelineEnd)}[base]");
        string current = "base";

        // Bottom track first, so higher tracks end up on top.
        var videoTracks = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.IsOff).Reverse();
        // Clips as they really appear once transitions are applied (maybe a bit longer, with fades).
        var videoClips = videoTracks.SelectMany(t => TransitionPlan.Expand(t, projectFps)).Where(r => InRange(r.Clip));
        foreach (RenderClip rc in wantVideo ? videoClips : [])
        {
            Clip clip = rc.Clip;
            bool isStill = clip.Media.Kind is MediaKind.Image or MediaKind.Text;
            bool fadesThrough = rc.FadeInFrames > 0 || rc.FadeOutFrames > 0  // needs see-through edges
                                || EffectFilters.MakesTransparency(clip.Effects);
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
            // Size, rotation, opacity and position: fixed, or changing every frame (keyframes).
            string transform, x, y;
            if (clip.Keyframes.Count > 0)
            {
                (transform, x, y) = VideoFilters.AnimatedTransform(clip, w, h, fit, projectFps, clip.Start / projectFps);
                x = $"'{x}'";
                y = $"'{y}'";
            }
            else
            {
                transform = VideoFilters.Transform(clip.Look, keepTransparency: isStill || fadesThrough);
                x = $"(main_w-overlay_w)/2+({ProcessRunner.Num(Math.Round(clip.Look.PositionX * fit, 2))})";
                y = $"(main_h-overlay_h)/2+({ProcessRunner.Num(Math.Round(clip.Look.PositionY * fit, 2))})";
            }

            string label = $"v{input}";
            filters.Add(
                $"[{input}:v:0]{speed.TrimStart(',')}{(speed.Length > 0 ? "," : "")}fps={fps},trim=duration={Sec(clip.Length)}," +
                $"scale={w}:{h}:force_original_aspect_ratio=decrease,setsar=1" +
                $"{VideoFilters.Color(clip.Look)}{EffectFilters.Chain(clip.Effects)}" +
                $"{VideoFilters.Fade(rc.BlackInFrames, rc.BlackOutFrames, clip.Length, projectFps, alpha: false)}" +
                $"{transform}" +
                $"{VideoFilters.Fade(rc.FadeInFrames, rc.FadeOutFrames, clip.Length, projectFps, alpha: true)}," +
                $"setpts=PTS-STARTPTS+{Sec(clip.Start)}/TB[{label}]");

            // 3. Lay it on top, centred and then moved by the clip's position (measured in the
            //    project frame, so scaled to the export size). eof_action=pass: once the clip
            //    ends, the picture underneath shows again.
            string next = $"o{input}";
            filters.Add($"[{current}][{label}]overlay=x={x}:y={y}:eof_action=pass:format=yuv420[{next}]");
            current = next;
            input++;
        }
        // Keep only the chosen range (and start its timestamps at zero).
        string keepRange = isRange ? $"trim=start={Sec(rangeStart)}:end={Sec(rangeEnd)},setpts=PTS-STARTPTS," : "";
        filters.Add($"[{current}]{keepRange}format=yuv420p[vout]");
        if (settings.Format == ExportFormat.Gif)
        {
            // GIFs have at most 256 colours: first find the best 256 for this video, then use them.
            filters.Add("[vout]split[gifa][gifb];[gifa]palettegen=stats_mode=diff[palette];" +
                        "[gifb][palette]paletteuse=dither=bayer:bayer_scale=5[gif]");
        }
        if (!wantVideo) filters.Clear();

        // ---- Audio ----
        var audioClips = project.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.IsOff)
            .SelectMany(t => TransitionPlan.Expand(t, projectFps)).Where(r => InRange(r.Clip)).ToList();
        var audioLabels = new List<string>();
        foreach (RenderClip rc in wantAudio ? audioClips : [])
        {
            Clip clip = rc.Clip;
            args.AddRange(["-ss", Sec(clip.In), "-t", Sec(clip.SourceLength), "-i", clip.Media.FilePath]);
            long delayMs = (long)Math.Round(clip.Start / projectFps * 1000);
            string label = $"a{input}";
            // Speed/reverse first (so fades are measured on the timeline), then volume and fades.
            string speed = VideoFilters.AudioSpeed(clip.Speed, clip.Reverse);
            // Transitions fade the sound too (a cross-dissolve becomes a cross-fade).
            double fadeIn = Math.Max(clip.FadeInSeconds, (rc.FadeInFrames + rc.BlackInFrames) / projectFps);
            double fadeOut = Math.Max(clip.FadeOutSeconds, (rc.FadeOutFrames + rc.BlackOutFrames) / projectFps);
            string effects = AudioFilters.ClipEffects(clip.VolumeDb, fadeIn, fadeOut, clip.Length / projectFps);
            filters.Add($"[{input}:a:0]aresample={SampleRate},aformat=channel_layouts=stereo{speed}{effects}," +
                        $"atrim=0:{Sec(clip.Length)},adelay={delayMs}:all=1[{label}]");
            audioLabels.Add(label);
            input++;
        }

        string exactLength = $"apad,atrim=0:{ProcessRunner.Num(timelineEnd)}" +
                             (isRange ? $",atrim=start={Sec(rangeStart)},asetpts=PTS-STARTPTS" : "");
        if (!wantAudio) { }
        else if (audioLabels.Count == 0)
            filters.Add($"anullsrc=r={SampleRate}:cl=stereo,atrim=0:{ProcessRunner.Num(duration)}[aout]");
        else
            filters.Add($"{string.Concat(audioLabels.Select(l => $"[{l}]"))}" +
                        $"amix=inputs={audioLabels.Count}:normalize=0:dropout_transition=0,{exactLength}[aout]");

        string graph = string.Join(";\n", filters);

        // ---- Output ----
        args.AddRange(["-filter_complex", graph]);
        if (wantVideo) args.AddRange(["-map", settings.Format == ExportFormat.Gif ? "[gif]" : "[vout]"]);
        if (wantAudio) args.AddRange(["-map", "[aout]"]);
        args.AddRange(FormatArguments(settings));
        if (wantVideo) args.AddRange(["-r", fps]);
        args.AddRange(["-progress", "pipe:1", "-nostats", settings.OutputPath]);

        long totalFrames = (long)Math.Round(duration * settings.FrameRate);
        return new ExportPlan(args, graph, totalFrames, duration, tempFiles);
    }

    /// <summary>The usual file extension for each format.</summary>
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.WebmVp9 => ".webm",
        ExportFormat.MovProRes => ".mov",
        ExportFormat.Gif => ".gif",
        ExportFormat.Mp3 => ".mp3",
        _ => ".mp4",
    };

    /// <summary>Encoder settings per format. For H.264, H.265 and VP9 the bitrate is the "quality" slider.</summary>
    private static IEnumerable<string> FormatArguments(ExportSettings s)
    {
        string rate = ProcessRunner.Num(s.VideoBitrateMbps) + "M";
        string max = ProcessRunner.Num(s.VideoBitrateMbps * 1.5) + "M";
        string buffer = ProcessRunner.Num(s.VideoBitrateMbps * 2) + "M";
        string[] bitrate = ["-b:v", rate, "-maxrate", max, "-bufsize", buffer];
        string[] aac = ["-c:a", "aac", "-b:a", $"{s.AudioBitrateKbps}k", "-ar", SampleRate.ToString()];
        string[] mp4 = ["-movflags", "+faststart"]; // lets the video start playing before it has fully downloaded

        return s.Format switch
        {
            ExportFormat.Mp4H264 => [.. H264(s.VideoEncoder, bitrate), .. aac, .. mp4],
            // hvc1 tag: so Apple devices recognise the H.265 video.
            ExportFormat.Mp4H265 => [.. H265(s.VideoEncoder, bitrate), "-tag:v", "hvc1", .. aac, .. mp4],
            ExportFormat.WebmVp9 => ["-c:v", "libvpx-vp9", .. bitrate, "-row-mt", "1", "-deadline", "good", "-cpu-used", "2",
                                     "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "192k"],
            // ProRes 422 HQ: fixed high quality for further editing; uncompressed sound.
            ExportFormat.MovProRes => ["-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le",
                                       "-c:a", "pcm_s16le", "-ar", SampleRate.ToString()],
            ExportFormat.Gif => ["-loop", "0"], // play forever
            ExportFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", "320k", "-ar", "44100"],
            _ => throw new NotSupportedException(),
        };
    }

    private static string[] H264(string encoder, string[] bitrate) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", .. bitrate, "-pix_fmt", "yuv420p"],
        "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", .. bitrate, "-pix_fmt", "nv12"],
        "h264_amf" => ["-c:v", "h264_amf", "-quality", "balanced", .. bitrate, "-pix_fmt", "yuv420p"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-profile:v", "high", .. bitrate, "-pix_fmt", "yuv420p"],
    };

    /// <summary>H.265 on the same graphics card as found for H.264 (h264_nvenc → hevc_nvenc), or the processor.</summary>
    private static string[] H265(string encoder, string[] bitrate) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "hevc_nvenc", "-preset", "p5", .. bitrate, "-pix_fmt", "yuv420p"],
        "h264_qsv" => ["-c:v", "hevc_qsv", "-preset", "medium", .. bitrate, "-pix_fmt", "nv12"],
        "h264_amf" => ["-c:v", "hevc_amf", "-quality", "balanced", .. bitrate, "-pix_fmt", "yuv420p"],
        _ => ["-c:v", "libx265", "-preset", "medium", .. bitrate, "-pix_fmt", "yuv420p"],
    };
}
