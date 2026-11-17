using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// The preview player. It owns the picture shown in the program monitor and runs on
/// its own thread, so decoding never freezes the window.
///
/// The window talks to it with three calls:
///   Seek(frame)  – show this frame (scrubbing, stepping)
///   Play(frame)  – play from here, with sound
///   Stop()       – pause
/// and it reports back with PositionChanged (during playback) and Stopped.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly Dispatcher ui;
    private readonly FramePool pool;
    private readonly FrameSource frames;
    private readonly double fps;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool disposed;

    // Requests from the window (written on the UI thread, read on the player thread).
    private long seekRequest = -1;
    private long playRequest = -1;
    private volatile bool stopRequest;

    // A copy of the timeline for the player thread (replaced, never changed).
    private volatile IReadOnlyList<VideoSegment> segments = [];
    private volatile IReadOnlyList<AudioPiece> audioPieces = [];
    private long durationFrames;
    private volatile bool timelineChanged;

    // Handing frames to the window: only the newest frame waiting is kept.
    private byte[]? waitingFrame;
    private int drawScheduled;
    private string? lastShownKey;

    /// <summary>The preview picture. The program monitor shows this.</summary>
    public WriteableBitmap Bitmap { get; }

    public bool IsPlaying { get; private set; }

    /// <summary>The playhead moved during playback (called on the UI thread).</summary>
    public event Action<long>? PositionChanged;

    /// <summary>Playback stopped by itself or by Stop() (called on the UI thread).</summary>
    public event Action? Stopped;

    /// <param name="previewWidth">Preview width in pixels. Height follows the project's shape.</param>
    public PlaybackEngine(ProjectSettings settings, int previewWidth = 960)
    {
        ui = Dispatcher.CurrentDispatcher;
        fps = settings.FrameRate;

        int width = previewWidth;
        int height = (int)Math.Round(previewWidth * (double)settings.Height / settings.Width / 2) * 2; // even number
        pool = new FramePool(width, height);
        frames = new FrameSource(pool, fps);
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);

        thread = new Thread(Run) { IsBackground = true, Name = "Snit playback" };
        thread.Start();
    }

    // ===================== Called from the window =====================

    /// <summary>Give the player a fresh copy of the timeline after an edit.</summary>
    public void UpdateTimeline(Project project)
    {
        segments = PreviewPlan.BuildVideo(project);
        audioPieces = PreviewPlan.BuildAudio(project);
        Interlocked.Exchange(ref durationFrames, project.DurationFrames);
        timelineChanged = true;
    }

    public void Seek(long frame)
    {
        Interlocked.Exchange(ref seekRequest, Math.Max(0, frame));
        wake.Set();
    }

    public void Play(long fromFrame)
    {
        stopRequest = false;
        Interlocked.Exchange(ref playRequest, Math.Max(0, fromFrame));
        wake.Set();
    }

    public void Stop()
    {
        stopRequest = true;
        Interlocked.Exchange(ref playRequest, -1);
        wake.Set();
    }

    // ===================== The player thread =====================

    private void Run()
    {
        while (!disposed)
        {
            long play = Interlocked.Exchange(ref playRequest, -1);
            if (play >= 0)
            {
                RunPlayback(play);
                continue;
            }

            long seek = Interlocked.Exchange(ref seekRequest, -1);
            if (seek >= 0)
            {
                ApplyTimelineChanges();
                Show(seek);
            }
            else
            {
                wake.WaitOne(200);
            }
        }
    }

    private void RunPlayback(long from)
    {
        ApplyTimelineChanges();
        long end = Interlocked.Read(ref durationFrames);
        if (from >= end) from = 0; // at the end: start again from the beginning

        Show(from);
        using AudioPreview? audio = SafeStartAudio(from);

        IsPlaying = true;
        var clock = Stopwatch.StartNew();
        audio?.Play();
        // Picture waits for the speaker buffer, so sound and picture line up.
        double delay = audio is null ? 0 : AudioPreview.LatencySeconds;
        long lastFrame = from;

        while (!disposed && !stopRequest && Interlocked.Read(ref playRequest) < 0)
        {
            if (timelineChanged)
            {
                // An edit happened while playing: restart from here with the new timeline.
                Interlocked.CompareExchange(ref playRequest, lastFrame, -1);
                break;
            }

            double elapsed = Math.Max(0, clock.Elapsed.TotalSeconds - delay);
            long frame = from + (long)(elapsed * fps);

            if (frame >= end)
            {
                Show(end);
                ReportPosition(end);
                break;
            }

            if (frame != lastFrame)
            {
                Show(frame);
                ReportPosition(frame);
                PrepareNextClip(frame);
                lastFrame = frame;
            }

            // Sleep until the next frame is due.
            double nextDue = (lastFrame - from + 1) / fps + delay;
            int sleepMs = (int)((nextDue - clock.Elapsed.TotalSeconds) * 1000);
            if (sleepMs > 0) wake.WaitOne(Math.Min(sleepMs, 15));
        }

        IsPlaying = false;
        stopRequest = false;
        if (Interlocked.Read(ref playRequest) < 0)
            ui.BeginInvoke(() => Stopped?.Invoke());
    }

    private AudioPreview? SafeStartAudio(long from)
    {
        try { return AudioPreview.Start(audioPieces, from, fps); }
        catch (Exception ex)
        {
            // No sound device, for example. Play the picture anyway.
            Debug.WriteLine($"Audio preview failed: {ex.Message}");
            return null;
        }
    }

    private void ApplyTimelineChanges()
    {
        if (!timelineChanged) return;
        timelineChanged = false;
        frames.Reset();
        lastShownKey = null;
    }

    /// <summary>Shortly before a cut, start reading the next clip so it's ready in time.</summary>
    private void PrepareNextClip(long frame)
    {
        var next = segments.FirstOrDefault(s => s.Start > frame);
        if (next is not null && next.Start - frame <= fps)
            frames.Prepare(next);
    }

    /// <summary>Decode the picture for a timeline frame and hand it to the window.</summary>
    private void Show(long frame)
    {
        VideoSegment? segment = segments.FirstOrDefault(s => s.Start <= frame && frame < s.End);
        string key = segment is null ? "black" : $"{segment.Path}|{segment.SourceFrameAt(frame)}";
        if (key == lastShownKey) return; // already on screen

        byte[]? pixels = null;
        if (segment is not null)
        {
            try { pixels = frames.GetFrame(segment, segment.SourceFrameAt(frame)); }
            catch (Exception ex) { Debug.WriteLine($"Decoding failed: {ex.Message}"); }
        }
        pixels ??= pool.RentBlack();

        lastShownKey = key;
        HandToWindow(pixels);
    }

    private void HandToWindow(byte[] pixels)
    {
        byte[]? older = Interlocked.Exchange(ref waitingFrame, pixels);
        if (older is not null) pool.Return(older); // the window never got to show it; that's fine

        if (Interlocked.Exchange(ref drawScheduled, 1) == 0)
            ui.BeginInvoke(DispatcherPriority.Render, DrawWaitingFrame);
    }

    // Runs on the UI thread.
    private void DrawWaitingFrame()
    {
        Interlocked.Exchange(ref drawScheduled, 0);
        byte[]? pixels = Interlocked.Exchange(ref waitingFrame, null);
        if (pixels is null) return;
        Bitmap.WritePixels(new Int32Rect(0, 0, pool.Width, pool.Height), pixels, pool.Stride, 0);
        pool.Return(pixels);
    }

    private long reportedFrame = -1;

    private void ReportPosition(long frame)
    {
        // Only one position update waits at a time; the newest one wins.
        if (Interlocked.Exchange(ref reportedFrame, frame) == -1)
            ui.BeginInvoke(() =>
            {
                long f = Interlocked.Exchange(ref reportedFrame, -1);
                if (f >= 0) PositionChanged?.Invoke(f);
            });
    }

    public void Dispose()
    {
        disposed = true;
        wake.Set();
        thread.Join(1000);
        frames.Dispose();
    }
}
