using Snit.Core;

namespace Snit.Services;

/// <summary>
/// Hands out preview frames for any point on the timeline. It keeps a few decoders open:
/// asking for the next frame (playback, or stepping forward) is instant, a small jump
/// forward skips ahead in the open decoder, and a big jump starts a new one.
/// Only the playback thread uses this class.
/// </summary>
public sealed class FrameSource(FramePool pool, double fps) : IDisposable
{
    private const int MaxDecoders = 3;

    // Most recently used first.
    private readonly List<(VideoSegment Segment, VideoDecoder Decoder)> decoders = [];
    private readonly Dictionary<string, byte[]?> stills = [];

    public FramePool Pool => pool;

    /// <summary>Returns the frame (caller gives it back to the pool), or null if it couldn't be read.</summary>
    public byte[]? GetFrame(VideoSegment segment, long sourceFrame)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null) return null;

        if (segment.Kind == MediaKind.Image)
            return GetStill(ffmpeg, segment.Path);

        int index = decoders.FindIndex(d => d.Segment == segment);
        VideoDecoder? decoder = index >= 0 ? decoders[index].Decoder : null;

        // Reuse the open decoder if the frame is just ahead (up to 2 seconds); otherwise start fresh.
        bool usable = decoder is not null && sourceFrame >= decoder.NextFrame && sourceFrame - decoder.NextFrame <= fps * 2;
        if (!usable)
        {
            if (index >= 0)
            {
                decoder!.Dispose();
                decoders.RemoveAt(index);
            }
            decoder = new VideoDecoder(ffmpeg, segment.Path, sourceFrame, fps, pool);
            Remember(segment, decoder);
        }
        else
        {
            Remember(segment, decoder!, existingIndex: index);
        }

        // Skip frames we don't need.
        while (decoder!.NextFrame < sourceFrame)
        {
            byte[]? skipped = decoder.ReadNext();
            if (skipped is null) return null;
            pool.Return(skipped);
        }
        return decoder.ReadNext();
    }

    /// <summary>Starts a decoder early (just before a cut) so the next clip is ready in time.</summary>
    public void Prepare(VideoSegment segment)
    {
        string? ffmpeg = FFmpegLocator.Current.FfmpegPath;
        if (ffmpeg is null || segment.Kind == MediaKind.Image) return;
        if (decoders.Any(d => d.Segment == segment)) return;
        Remember(segment, new VideoDecoder(ffmpeg, segment.Path, segment.SourceStart, fps, pool), insertLast: true);
    }

    private void Remember(VideoSegment segment, VideoDecoder decoder, int existingIndex = -1, bool insertLast = false)
    {
        if (existingIndex >= 0) decoders.RemoveAt(existingIndex);
        if (insertLast && decoders.Count > 0) decoders.Insert(1, (segment, decoder));
        else decoders.Insert(0, (segment, decoder));

        while (decoders.Count > MaxDecoders)
        {
            decoders[^1].Decoder.Dispose();
            decoders.RemoveAt(decoders.Count - 1);
        }
    }

    /// <summary>Pictures are decoded once and remembered.</summary>
    private byte[]? GetStill(string ffmpeg, string path)
    {
        if (!stills.TryGetValue(path, out byte[]? pixels))
        {
            using var decoder = new VideoDecoder(ffmpeg, path, 0, fps, pool);
            byte[]? frame = decoder.ReadNext();
            if (frame is not null)
            {
                pixels = (byte[])frame.Clone();
                pool.Return(frame);
            }
            stills[path] = pixels;
        }
        if (pixels is null) return null;

        byte[] copy = pool.Rent();
        Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
        return copy;
    }

    /// <summary>Forget everything (after edits, so changed clips are read fresh).</summary>
    public void Reset()
    {
        foreach (var d in decoders) d.Decoder.Dispose();
        decoders.Clear();
    }

    public void Dispose() => Reset();
}
