using Snit.Core;

namespace Snit.Services;

/// <summary>A decoded preview picture and the pool its buffer belongs to (give it back when done).</summary>
public sealed record DecodedFrame(byte[] Pixels, FramePool Pool);

/// <summary>
/// Hands out preview pictures for clips. It keeps a few decoders open (one per track and
/// file/colour combination): asking for the next frame (playback, stepping forward) is instant,
/// a small jump forward skips ahead, the same frame again (slow motion) is remembered, and a
/// big jump starts a new decoder. Only the playback thread uses this class.
/// </summary>
public sealed class FrameSource(int previewWidth, int previewHeight, double fps) : IDisposable
{
    private const int MaxDecoders = 4;

    private sealed class OpenDecoder(string key, VideoDecoder decoder)
    {
        public string Key { get; } = key;
        public VideoDecoder Decoder { get; } = decoder;
        public long LastIndex { get; set; } = -1;
        public byte[]? LastPixels { get; set; }
    }

    // Most recently used first.
    private readonly List<OpenDecoder> decoders = [];
    private readonly Dictionary<string, byte[]?> stills = [];
    private readonly Dictionary<(int, int), FramePool> pools = [];

    /// <summary>
    /// The preview size for a clip: its own shape, fitted inside the preview area (e.g. a 4:3
    /// video becomes 720×540 inside 960×540). Unknown sizes use the whole area.
    /// </summary>
    public (int Width, int Height) SizeFor(VideoSegment segment)
    {
        if (segment.MediaWidth <= 0 || segment.MediaHeight <= 0) return (previewWidth, previewHeight);
        double fit = Math.Min((double)previewWidth / segment.MediaWidth, (double)previewHeight / segment.MediaHeight);
        int w = Math.Max(2, (int)Math.Round(segment.MediaWidth * fit / 2) * 2);
        int h = Math.Max(2, (int)Math.Round(segment.MediaHeight * fit / 2) * 2);
        return (w, h);
    }

    private FramePool PoolFor(VideoSegment segment)
    {
        var size = SizeFor(segment);
        if (!pools.TryGetValue(size, out FramePool? pool))
            pools[size] = pool = new FramePool(size.Width, size.Height);
        return pool;
    }

    /// <summary>The picture for a clip at a source frame, or null if it couldn't be read.</summary>
    public DecodedFrame? GetFrame(VideoSegment segment, long sourceFrame, int layer)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null) return null;
        FramePool pool = PoolFor(segment);
        string color = VideoFilters.Color(segment.Look);

        if (segment.Kind == MediaKind.Image)
            return GetStill(ffmpeg, segment, pool, color);

        string key = $"{layer}|{segment.DecoderKey}|{pool.Width}x{pool.Height}";
        int index = decoders.FindIndex(d => d.Key == key);
        OpenDecoder? open = index >= 0 ? decoders[index] : null;

        // The same frame as last time (slow motion, or redrawing after a change): reuse it.
        if (open?.LastPixels is byte[] last && open.LastIndex == sourceFrame)
        {
            Touch(index);
            return Copy(last, pool);
        }

        // Reuse the open decoder if the frame is just ahead (up to 2 seconds); otherwise start fresh.
        bool usable = open is not null && sourceFrame >= open.Decoder.NextFrame && sourceFrame - open.Decoder.NextFrame <= fps * 2;
        if (!usable)
        {
            if (index >= 0)
            {
                decoders[index].Decoder.Dispose();
                decoders.RemoveAt(index);
            }
            open = new OpenDecoder(key, new VideoDecoder(ffmpeg, segment.Path, sourceFrame, fps, pool, color));
            Add(open);
        }
        else
        {
            Touch(index);
        }

        // Skip frames we don't need (fast speeds skip some on purpose).
        while (open!.Decoder.NextFrame < sourceFrame)
        {
            byte[]? skipped = open.Decoder.ReadNext();
            if (skipped is null) return null;
            pool.Return(skipped);
        }

        byte[]? pixels = open.Decoder.ReadNext();
        if (pixels is null) return null;

        // Remember a copy for the next time the same frame is asked for.
        open.LastPixels ??= new byte[pixels.Length];
        Buffer.BlockCopy(pixels, 0, open.LastPixels, 0, pixels.Length);
        open.LastIndex = sourceFrame;
        return new DecodedFrame(pixels, pool);
    }

    /// <summary>Starts a decoder early (just before a cut) so the next clip is ready in time.</summary>
    public void Prepare(VideoSegment segment, int layer)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null || segment.Kind != MediaKind.Video) return;
        FramePool pool = PoolFor(segment);
        string key = $"{layer}|{segment.DecoderKey}|{pool.Width}x{pool.Height}";
        long first = segment.SourceFrameAt(segment.Start);
        if (decoders.Any(d => d.Key == key && d.Decoder.NextFrame <= first && first - d.Decoder.NextFrame <= fps * 2)) return;

        int existing = decoders.FindIndex(d => d.Key == key);
        if (existing >= 0)
        {
            decoders[existing].Decoder.Dispose();
            decoders.RemoveAt(existing);
        }
        Add(new OpenDecoder(key, new VideoDecoder(ffmpeg, segment.Path, first, fps, pool, VideoFilters.Color(segment.Look))), atBack: true);
    }

    private void Touch(int index)
    {
        if (index <= 0) return;
        var d = decoders[index];
        decoders.RemoveAt(index);
        decoders.Insert(0, d);
    }

    private void Add(OpenDecoder decoder, bool atBack = false)
    {
        if (atBack) decoders.Insert(Math.Min(decoders.Count, MaxDecoders - 1), decoder);
        else decoders.Insert(0, decoder);
        while (decoders.Count > MaxDecoders)
        {
            decoders[^1].Decoder.Dispose();
            decoders.RemoveAt(decoders.Count - 1);
        }
    }

    /// <summary>Pictures are decoded once (per size and colour) and remembered.</summary>
    private DecodedFrame? GetStill(string ffmpeg, VideoSegment segment, FramePool pool, string color)
    {
        string key = $"{segment.Path}|{pool.Width}x{pool.Height}|{color}";
        if (!stills.TryGetValue(key, out byte[]? pixels))
        {
            using var decoder = new VideoDecoder(ffmpeg, segment.Path, 0, fps, pool, color);
            byte[]? frame = decoder.ReadNext();
            if (frame is not null)
            {
                pixels = (byte[])frame.Clone();
                pool.Return(frame);
            }
            if (stills.Count > 40) stills.Clear();
            stills[key] = pixels;
        }
        return pixels is null ? null : Copy(pixels, pool);
    }

    private static DecodedFrame Copy(byte[] source, FramePool pool)
    {
        byte[] copy = pool.Rent();
        Buffer.BlockCopy(source, 0, copy, 0, Math.Min(source.Length, copy.Length));
        return new DecodedFrame(copy, pool);
    }

    /// <summary>Forget everything.</summary>
    public void Reset()
    {
        foreach (var d in decoders) d.Decoder.Dispose();
        decoders.Clear();
        stills.Clear();
    }

    public void Dispose() => Reset();
}
