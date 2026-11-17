using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Snit.Services;

/// <summary>
/// Reads the frames of one video file in order, starting at a given frame, by running
/// ffmpeg and taking its raw pixels from a pipe. ffmpeg also resizes the frames to the
/// preview size and converts them to the project frame rate, so every frame we get
/// is exactly one timeline frame.
///
/// A background thread reads a few frames ahead, so playback doesn't wait on ffmpeg.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    private readonly Process process;
    private readonly BlockingCollection<byte[]> readyFrames = new(boundedCapacity: 6);
    private readonly FramePool pool;
    private readonly CancellationTokenSource stop = new();

    /// <summary>The source frame number that the next <see cref="ReadNext"/> returns.</summary>
    public long NextFrame { get; private set; }

    public VideoDecoder(string ffmpegPath, string file, long startFrame, double fps, FramePool pool)
    {
        this.pool = pool;
        NextFrame = startFrame;

        int w = pool.Width, h = pool.Height;
        // Fit the picture inside the preview size and add black bars if the shape differs.
        string filter = $"fps={ProcessRunner.Num(fps)}," +
                        $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                        $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1";

        process = ProcessRunner.StartStreaming(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", ProcessRunner.Num(startFrame / fps),   // jump to the start (accurate in modern ffmpeg)
            "-i", file,
            "-an", "-sn",                                   // no sound or subtitles needed here
            "-vf", filter,
            "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        ]);

        new Thread(ReadLoop) { IsBackground = true, Name = "Snit video decoder" }.Start();
    }

    private void ReadLoop()
    {
        Stream output = process.StandardOutput.BaseStream;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                byte[] frame = pool.Rent();
                int read = output.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false);
                if (read < frame.Length)
                {
                    pool.Return(frame);
                    break; // end of the file
                }
                readyFrames.Add(frame, stop.Token); // waits if we are far enough ahead
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }          // process was stopped
        catch (ObjectDisposedException) { }
        finally
        {
            readyFrames.CompleteAdding();
        }
    }

    /// <summary>
    /// Returns the next frame (pixels in BGRA order), or null at the end of the file
    /// or if ffmpeg takes too long. Give the buffer back to the pool when done with it.
    /// </summary>
    public byte[]? ReadNext(int timeoutMs = 3000)
    {
        try
        {
            if (readyFrames.TryTake(out byte[]? frame, timeoutMs, stop.Token))
            {
                NextFrame++;
                return frame;
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // finished and empty
        return null;
    }

    public void Dispose()
    {
        stop.Cancel();
        try { if (!process.HasExited) process.Kill(); } catch { /* already gone */ }
        process.Dispose();
        while (readyFrames.TryTake(out byte[]? frame)) pool.Return(frame);
    }
}

/// <summary>
/// Re-uses frame buffers instead of making new ones 30 times a second
/// (each is a few megabytes, and constantly making new ones makes the app stutter).
/// </summary>
public sealed class FramePool(int width, int height)
{
    private readonly ConcurrentBag<byte[]> free = [];

    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;
    public int FrameBytes => Width * Height * 4;

    public byte[] Rent() => free.TryTake(out byte[]? b) ? b : new byte[FrameBytes];

    public byte[] RentBlack()
    {
        byte[] b = Rent();
        Array.Clear(b);
        return b;
    }

    public void Return(byte[] buffer)
    {
        if (buffer.Length == FrameBytes && free.Count < 24) free.Add(buffer);
    }
}
