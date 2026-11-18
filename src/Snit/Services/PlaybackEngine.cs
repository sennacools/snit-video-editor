using System.Diagnostics;
using System.Windows.Threading;
using Snit.Core;

namespace Snit.Services;

/// <summary>
/// One track's picture at the current moment: the clip, and (for video and pictures) its
/// decoded pixels. Titles have no pixels; the window draws them itself.
/// </summary>
public sealed record LayerFrame(VideoSegment Segment, DecodedFrame? Picture);

/// <summary>
/// The preview player. It runs on its own thread, so decoding never freezes the window.
/// For every moment it decodes the clip under the playhead on each video track (skipping
/// tracks hidden behind a full-screen video) and hands the window one "bundle" with a
/// picture per track. The window stacks them with each clip's size, position and opacity.
///
/// The window talks to it with three calls:
///   Seek(frame)  – show this frame (scrubbing, stepping)
///   Play(frame)  – play from here, with sound
///   Stop()       – pause
/// and it reports back with FrameReady, PositionChanged, LevelsChanged and Stopped.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly Dispatcher ui;
    private readonly FrameSource frames;
    private readonly double fps;
    private readonly int frameWidth, frameHeight;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool disposed;

    // Requests from the window (written on the UI thread, read on the player thread).
    private long seekRequest = -1;
    private long playRequest = -1;
    private volatile bool stopRequest;
    private double playRate = 1;

    // A copy of the timeline for the player thread (replaced, never changed).
    private volatile IReadOnlyList<IReadOnlyList<VideoSegment>> layers = [];
    private volatile IReadOnlyList<AudioPiece> audioPieces = [];
    private long durationFrames;
    private volatile bool timelineChanged;

    // Handing pictures to the window: only the newest bundle waiting is kept.
    private LayerFrame?[]? waitingBundle;
    private int drawScheduled;
    private string? lastShownKey;

    /// <summary>The size of the preview area (the project's shape, 960 pixels wide).</summary>
    public int PreviewWidth { get; }
    public int PreviewHeight { get; }

    public bool IsPlaying { get; private set; }

    /// <summary>New pictures for the tracks, bottom track first (called on the UI thread).
    /// Call <see cref="Release"/> when done with the bundle.</summary>
    public event Action<LayerFrame?[]>? FrameReady;

    /// <summary>The playhead moved during playback (called on the UI thread).</summary>
    public event Action<long>? PositionChanged;

    /// <summary>Playback stopped by itself or by Stop() (called on the UI thread).</summary>
    public event Action? Stopped;

    /// <summary>Sound levels (loudest sample, 0–1) of left and right during playback (called on the UI thread).</summary>
    public event Action<float, float>? LevelsChanged;

    /// <param name="previewWidth">Preview width in pixels. Height follows the project's shape.</param>
    public PlaybackEngine(ProjectSettings settings, int previewWidth = 960)
    {
        ui = Dispatcher.CurrentDispatcher;
        fps = settings.FrameRate;
        frameWidth = settings.Width;
        frameHeight = settings.Height;
        PreviewWidth = previewWidth;
        PreviewHeight = (int)Math.Round(previewWidth * (double)settings.Height / settings.Width / 2) * 2; // even number
        frames = new FrameSource(PreviewWidth, PreviewHeight, fps);

        thread = new Thread(Run) { IsBackground = true, Name = "Snit playback" };
        thread.Start();
    }

    // ===================== Called from the window =====================

    /// <summary>Give the player a fresh copy of the timeline after an edit.</summary>
    public void UpdateTimeline(Project project)
    {
        layers = PreviewPlan.BuildLayers(project);
        audioPieces = PreviewPlan.BuildAudio(project);
        Interlocked.Exchange(ref durationFrames, project.DurationFrames);
        timelineChanged = true;
    }

    public void Seek(long frame)
    {
        Interlocked.Exchange(ref seekRequest, Math.Max(0, frame));
        wake.Set();
    }

    /// <param name="rate">1 = normal, 2 = twice as fast (fast-forward plays without sound).</param>
    public void Play(long fromFrame, double rate = 1)
    {
        playRate = Math.Max(0.1, rate);
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

    /// <summary>Gives the bundle's picture buffers back for reuse.</summary>
    public static void Release(LayerFrame?[] bundle)
    {
        foreach (LayerFrame? layer in bundle)
            if (layer?.Picture is DecodedFrame p) p.Pool.Return(p.Pixels);
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
        double rate = playRate;
        using AudioPreview? audio = rate == 1 ? SafeStartAudio(from) : null; // fast-forward is silent
        if (audio is not null) audio.LevelsChanged += ReportLevels;

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
            long frame = from + (long)(elapsed * fps * rate);

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
                PrepareNextClips(frame);
                lastFrame = frame;
            }

            // Sleep until the next frame is due.
            double nextDue = (lastFrame - from + 1) / (fps * rate) + delay;
            int sleepMs = (int)((nextDue - clock.Elapsed.TotalSeconds) * 1000);
            if (sleepMs > 0) wake.WaitOne(Math.Min(sleepMs, 15));
        }

        IsPlaying = false;
        stopRequest = false;
        if (audio is not null) audio.LevelsChanged -= ReportLevels;
        ReportLevels(0, 0); // meters fall back to silence
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
        lastShownKey = null; // redraw even if the frame number is the same
    }

    /// <summary>Shortly before a cut, start reading the next clip on each track so it's ready in time.</summary>
    private void PrepareNextClips(long frame)
    {
        var current = layers;
        for (int i = 0; i < current.Count; i++)
        {
            var next = current[i].FirstOrDefault(s => s.Start > frame);
            if (next is not null && next.Start - frame <= fps)
                frames.Prepare(next, i);
        }
    }

    /// <summary>Decode the pictures for a timeline frame and hand them to the window.</summary>
    private void Show(long frame)
    {
        var current = layers;
        var active = new VideoSegment?[current.Count];

        // From the top track down: stop once a clip hides everything below it.
        for (int i = current.Count - 1; i >= 0; i--)
        {
            active[i] = current[i].FirstOrDefault(s => s.Start <= frame && frame < s.End);
            if (active[i]?.CoversFrame(frameWidth, frameHeight) == true) break;
        }

        // Skip the work if exactly the same thing is already on screen.
        string key = string.Join("|", active.Select(s => s is null ? "-"
            : $"{s.ClipId}:{(s.Kind is MediaKind.Video ? s.SourceFrameAt(frame) : 0)}:{s.Look.GetHashCode()}:{s.Title?.Text}"));
        if (key == lastShownKey) return;

        var bundle = new LayerFrame?[current.Count];
        for (int i = 0; i < active.Length; i++)
        {
            VideoSegment? s = active[i];
            if (s is null) continue;
            if (s.Kind == MediaKind.Text)
            {
                bundle[i] = new LayerFrame(s, null); // the window draws titles
                continue;
            }
            DecodedFrame? picture = null;
            try { picture = frames.GetFrame(s, s.SourceFrameAt(frame), i); }
            catch (Exception ex) { Debug.WriteLine($"Decoding failed: {ex.Message}"); }
            if (picture is not null) bundle[i] = new LayerFrame(s, picture);
        }

        lastShownKey = key;
        HandToWindow(bundle);
    }

    private void HandToWindow(LayerFrame?[] bundle)
    {
        LayerFrame?[]? older = Interlocked.Exchange(ref waitingBundle, bundle);
        if (older is not null) Release(older); // the window never got to show it; that's fine

        if (Interlocked.Exchange(ref drawScheduled, 1) == 0)
            ui.BeginInvoke(DispatcherPriority.Render, DeliverWaitingBundle);
    }

    // Runs on the UI thread.
    private void DeliverWaitingBundle()
    {
        Interlocked.Exchange(ref drawScheduled, 0);
        LayerFrame?[]? bundle = Interlocked.Exchange(ref waitingBundle, null);
        if (bundle is null) return;
        if (FrameReady is null) Release(bundle);
        else FrameReady(bundle);
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

    private long pendingLevels = -1; // both levels packed in one number, so they travel together

    private void ReportLevels(float left, float right)
    {
        long packed = ((long)BitConverter.SingleToInt32Bits(left) << 32) | (uint)BitConverter.SingleToInt32Bits(right);
        if (Interlocked.Exchange(ref pendingLevels, packed) == -1)
            ui.BeginInvoke(() =>
            {
                long p = Interlocked.Exchange(ref pendingLevels, -1);
                if (p != -1)
                    LevelsChanged?.Invoke(BitConverter.Int32BitsToSingle((int)(p >> 32)), BitConverter.Int32BitsToSingle((int)p));
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
