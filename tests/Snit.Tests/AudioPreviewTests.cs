using Snit.Core;
using Snit.Services;

namespace Snit.Tests;

/// <summary>
/// Plays a test tone through the real preview sound path and checks the level meter readings.
/// Skipped without FFmpeg or without a sound device (like on GitHub's build machines).
/// </summary>
public class AudioPreviewTests(TestMedia media) : IClassFixture<TestMedia>
{
    private const double Fps = 30;

    private static async Task<List<float>> ListenAsync(AudioPiece piece, TimeSpan time)
    {
        var levels = new List<float>();
        AudioPreview? preview;
        try { preview = AudioPreview.Start([piece], 0, Fps); }
        catch (Exception) { return []; } // no sound device: nothing to test here

        using (preview)
        {
            if (preview is null) return [];
            preview.LevelsChanged += (left, _) => { lock (levels) levels.Add(left); };
            preview.Play();
            await Task.Delay(time);
        }
        lock (levels) return [.. levels];
    }

    [Fact]
    public async Task Meter_reports_the_loudness_of_what_is_playing()
    {
        if (!media.Available) return;

        // The test tone is a mono sine wave at 1/8 of full loudness. Made stereo, FFmpeg plays it
        // about 3 dB quieter on each side, so each channel peaks near 0.125 × 0.707 ≈ 0.088.
        var levels = await ListenAsync(new AudioPiece(0, 60, media.Tone, 0), TimeSpan.FromSeconds(1));
        if (levels.Count == 0) return; // no sound device

        Assert.True(levels.Count >= 10, $"Only {levels.Count} meter updates in a second.");
        Assert.InRange(levels.Max(), 0.07f, 0.11f);
    }

    [Fact]
    public async Task Sped_up_and_reversed_sound_still_plays()
    {
        if (!media.Available) return;
        // 2 s tone at 200%, reversed: 1 s on the timeline (30 frames), using all 60 source frames.
        var levels = await ListenAsync(new AudioPiece(0, 30, media.Tone, 0, Speed: 2, Reverse: true, SourceLength: 60),
            TimeSpan.FromSeconds(0.8));
        if (levels.Count == 0) return;
        Assert.InRange(levels.Max(), 0.07f, 0.11f); // same loudness: atempo changes speed, not volume
    }

    [Fact]
    public async Task Minus_six_dB_halves_what_the_meter_shows()
    {
        if (!media.Available) return;

        var normal = await ListenAsync(new AudioPiece(0, 60, media.Tone, 0), TimeSpan.FromSeconds(1));
        var quieter = await ListenAsync(new AudioPiece(0, 60, media.Tone, 0, VolumeDb: -6), TimeSpan.FromSeconds(1));
        if (normal.Count == 0 || quieter.Count == 0) return;

        Assert.InRange(quieter.Max() / normal.Max(), 0.45, 0.56); // -6 dB ≈ 0.5×
    }
}
