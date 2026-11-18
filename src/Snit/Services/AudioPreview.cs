using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Plays the timeline's sound during playback. ffmpeg mixes all audio clips together
/// (each delayed to its place on the timeline) and streams the result to us;
/// NAudio sends it to the speakers.
/// </summary>
public sealed class AudioPreview : IDisposable
{
    private const int SampleRate = 48000;

    /// <summary>Roughly how far the sound lags behind (the speaker buffer).</summary>
    public const double LatencySeconds = 0.1;

    private readonly Process process;
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOut output;
    private volatile bool stopped;

    private AudioPreview(Process process)
    {
        this.process = process;
        buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 2), TimeSpan.FromSeconds(4))
        {
            DiscardOnBufferOverflow = true,
        };
        // Two buffers of 50 ms each: about 0.1 s between "play" and hearing it.
        output = new WaveOut { BufferMilliseconds = (int)(LatencySeconds * 1000 / 2), NumberOfBuffers = 2 };
        output.Init(buffer);
        new Thread(ReadLoop) { IsBackground = true, Name = "Snit audio" }.Start();
    }

    /// <summary>
    /// Starts mixing from <paramref name="fromFrame"/> and waits until a little sound is ready.
    /// Returns null if there is nothing to hear.
    /// </summary>
    public static AudioPreview? Start(IReadOnlyList<AudioPiece> pieces, long fromFrame, double fps)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        var audible = pieces.Where(p => p.End > fromFrame).ToList();
        if (ffmpeg is null || audible.Count == 0) return null;

        double from = fromFrame / fps;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        var filters = new List<string>();

        for (int i = 0; i < audible.Count; i++)
        {
            AudioPiece p = audible[i];
            double start = p.Start / fps;
            double length = (p.End - p.Start) / fps;
            double skip = Math.Max(0, from - start);           // part of the clip already behind us
            int delayMs = (int)Math.Round(Math.Max(0, start - from) * 1000);

            // Read the whole clip and apply volume and fades first, then skip the part that is
            // already behind us; that way a fade sounds right even when you start halfway through it.
            string effects = AudioFilters.ClipEffects(p.VolumeDb, p.FadeInSeconds, p.FadeOutSeconds, length);
            string trim = skip > 0 ? $",atrim=start={ProcessRunner.Num(skip)},asetpts=PTS-STARTPTS" : "";

            args.AddRange(["-ss", ProcessRunner.Num(p.SourceStart / fps), "-t", ProcessRunner.Num(length), "-i", p.Path]);
            filters.Add($"[{i}:a:0]aresample={SampleRate},aformat=channel_layouts=stereo{effects}{trim},adelay={delayMs}:all=1[a{i}]");
        }

        string inputs = string.Concat(Enumerable.Range(0, audible.Count).Select(i => $"[a{i}]"));
        filters.Add($"{inputs}amix=inputs={audible.Count}:normalize=0:dropout_transition=0[out]");

        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[out]",
                       "-f", "s16le", "-ar", SampleRate.ToString(), "-ac", "2", "pipe:1"]);

        var preview = new AudioPreview(ProcessRunner.StartStreaming(ffmpeg, args));

        // Wait (at most 1.5 s) for the first bit of sound, so picture and sound start together.
        var wait = Stopwatch.StartNew();
        while (preview.buffer.BufferedDuration < TimeSpan.FromSeconds(0.2) && !preview.process.HasExited && wait.ElapsedMilliseconds < 1500)
            Thread.Sleep(5);
        return preview;
    }

    public void Play() => output.Play();

    private void ReadLoop()
    {
        var chunk = new byte[16384];
        try
        {
            Stream stream = process.StandardOutput.BaseStream;
            while (!stopped)
            {
                // Don't run too far ahead of what is being played.
                while (!stopped && buffer.BufferedDuration > TimeSpan.FromSeconds(2))
                    Thread.Sleep(20);

                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                buffer.AddSamples(chunk, 0, read);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        stopped = true;
        try { if (!process.HasExited) process.Kill(); } catch { /* already gone */ }
        process.Dispose();
        output.Stop();
        output.Dispose();
    }
}
