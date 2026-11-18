using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Snit.Core;

// The project "model": plain data about media, tracks and clips.
// Times on the timeline are counted in whole frames (at the project frame rate),
// so cuts always land exactly on a frame and numbers never drift.

/// <summary>Text = a title made in Snit (no file).</summary>
public enum MediaKind { Video, Audio, Image, Text }

public enum TrackKind { Video, Audio }

public sealed class ProjectSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30;
}

/// <summary>A file in the media bin (a video, audio file or picture).</summary>
public partial class MediaItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FilePath { get; set; }
    public string Name => Path.GetFileName(FilePath);
    public MediaKind Kind { get; init; }

    /// <summary>Length of the file in seconds (0 for pictures).</summary>
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public bool HasAudio { get; init; }

    /// <summary>Small preview picture for the bin and the timeline (not saved in the project file).</summary>
    [ObservableProperty]
    private ImageSource? thumbnail;

    /// <summary>
    /// The sound's loudness for drawing the waveform: the loudest point of every 1/100 second,
    /// from 0 (silent) to 1 (as loud as possible). Not saved; made again when a project opens.
    /// </summary>
    [ObservableProperty]
    private float[]? waveform;

    public const int WaveformPointsPerSecond = 100;

    /// <summary>True when the file can't be found any more (moved or deleted).</summary>
    [ObservableProperty]
    private bool isOffline;

    public bool IsAudioOnly => Kind == MediaKind.Audio;

    /// <summary>The badge text in the bin: "0:12", "1:02:03" or "Still".</summary>
    public string DurationText
    {
        get
        {
            if (Kind == MediaKind.Image) return "Still";
            var t = TimeSpan.FromSeconds(DurationSeconds);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }

    /// <summary>
    /// The stand-in "file" that every title clip points to. Titles have no real file;
    /// their text and look are stored in the clip itself (<see cref="Clip.Title"/>).
    /// </summary>
    public static MediaItem Title { get; } = new() { Id = Guid.Empty, FilePath = "Title", Kind = MediaKind.Text };

    /// <summary>How many frames of this file can be used. Pictures and titles can be stretched to any length.</summary>
    public long LengthInFrames(double fps)
        => Kind is MediaKind.Image or MediaKind.Text ? long.MaxValue : Math.Max(1, (long)Math.Floor(DurationSeconds * fps + 1e-6));
}

/// <summary>A piece of a media file placed on a track.</summary>
public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required MediaItem Media { get; set; }

    /// <summary>Where the clip starts on the timeline (frame number).</summary>
    public long Start { get; set; }

    /// <summary>The first frame used from the source file ("in point").</summary>
    public long In { get; set; }

    /// <summary>How many frames long the clip is.</summary>
    public long Length { get; set; }

    /// <summary>Clips with the same LinkId belong together (a video and its own sound) and are edited as one.</summary>
    public Guid? LinkId { get; set; }

    // ---- Sound settings (used by clips on audio tracks) ----

    /// <summary>Louder or quieter, in decibels. 0 = unchanged, -6 ≈ half as loud, +6 ≈ twice as loud.</summary>
    public double VolumeDb { get; set; }

    /// <summary>Seconds over which the sound rises from silence at the start of the clip.</summary>
    public double FadeInSeconds { get; set; }

    /// <summary>Seconds over which the sound falls to silence at the end of the clip.</summary>
    public double FadeOutSeconds { get; set; }

    /// <summary>For title clips: the text and how it looks. Null for normal clips.</summary>
    public TitleStyle? Title { get; set; }

    // ---- Picture settings (clips on video tracks) ----

    /// <summary>Size, rotation, opacity, position and colour. Replaced as a whole (it never changes inside).</summary>
    public ClipLook Look { get; set; } = ClipLook.Default;

    /// <summary>Equaliser and noise reduction (for clips on audio tracks).</summary>
    public ClipAudio Audio { get; set; } = ClipAudio.Default;

    /// <summary>Effects (blur, crop, green screen…), applied top to bottom. Replaced as a whole when changed.</summary>
    public IReadOnlyList<ClipEffect> Effects { get; set; } = [];

    /// <summary>Animated transform settings (see <see cref="Core.Keyframes"/>). Replaced as a whole when changed.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Keyframe>> Keyframes { get; set; } = Core.Keyframes.None;

    /// <summary>The look at a timeline frame, with animation applied.</summary>
    public ClipLook LookAt(long timelineFrame) => Core.Keyframes.LookAt(Look, Keyframes, timelineFrame - Start);

    // ---- Transitions ----

    /// <summary>A transition at the start of the clip (from the clip before it, or a fade-in).</summary>
    public Transition? TransitionIn { get; set; }

    /// <summary>A transition at the end of the clip (a fade-out; at a cut, the next clip's start transition wins).</summary>
    public Transition? TransitionOut { get; set; }

    // ---- Speed ----

    /// <summary>100 = normal, 200 = twice as fast (half as long), 50 = slow motion.</summary>
    public double SpeedPercent { get; set; } = 100;

    /// <summary>Play the clip backwards.</summary>
    public bool Reverse { get; set; }

    public double Speed => SpeedPercent / 100;

    /// <summary>How many frames of the source file the clip uses (more than <see cref="Length"/> when sped up).</summary>
    public long SourceLength => Math.Max(1, (long)Math.Round(Length * Speed));

    /// <summary>Which source frame is shown at a timeline frame, taking speed and reverse into account.</summary>
    public long SourceFrameAt(long timelineFrame)
    {
        long step = (long)Math.Floor((timelineFrame - Start) * Speed + 1e-9);
        step = Math.Clamp(step, 0, SourceLength - 1);
        return Reverse ? In + SourceLength - 1 - step : In + step;
    }

    public long End => Start + Length;
    public bool IsTitle => Title is not null;
    public string Name => Title is { } t ? FirstLine(t.Text) : Media.Name;

    public Clip Copy()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Title = Title?.Copy(); // a separate copy, so undo keeps the old text
        return copy;
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? "Title" : line;
    }
}

/// <summary>
/// How a clip's picture is shown: transform (size, rotation, opacity, position) and colour.
/// A record: to change something, make a new one, e.g. <c>clip.Look = clip.Look with { Scale = 50 }</c>.
/// </summary>
public sealed record ClipLook
{
    /// <summary>Size in percent (100 = fits the frame).</summary>
    public double Scale { get; init; } = 100;

    /// <summary>Degrees, clockwise.</summary>
    public double Rotation { get; init; }

    /// <summary>0 = invisible, 100 = solid.</summary>
    public double Opacity { get; init; } = 100;

    /// <summary>Moves the picture this many pixels right (measured in the project frame, e.g. 1920 wide).</summary>
    public double PositionX { get; init; }

    /// <summary>Moves the picture this many pixels down.</summary>
    public double PositionY { get; init; }

    /// <summary>Brightness in "stops": +1 = twice as bright, -1 = half.</summary>
    public double Exposure { get; init; }

    /// <summary>-100 (flat) to +100 (punchy).</summary>
    public double Contrast { get; init; }

    /// <summary>Colour strength in percent: 0 = black and white, 100 = normal, 200 = very colourful.</summary>
    public double Saturation { get; init; } = 100;

    /// <summary>-100 (cooler, bluer) to +100 (warmer, more orange).</summary>
    public double Temperature { get; init; }

    /// <summary>-100 (greener) to +100 (more magenta).</summary>
    public double Tint { get; init; }

    // ---- Colour wheels (see ColorWheel): puck position -1..1 and lightness -1..1 ----

    public double ShadowsX { get; init; }
    public double ShadowsY { get; init; }
    public double ShadowsLight { get; init; }
    public double MidtonesX { get; init; }
    public double MidtonesY { get; init; }
    public double MidtonesLight { get; init; }
    public double HighlightsX { get; init; }
    public double HighlightsY { get; init; }
    public double HighlightsLight { get; init; }

    // ---- Curves (see Curves): "" = a straight line ----

    public string CurveMaster { get; init; } = "";
    public string CurveRed { get; init; } = "";
    public string CurveGreen { get; init; } = "";
    public string CurveBlue { get; init; } = "";

    public static ClipLook Default { get; } = new();

    public bool HasWheels => ShadowsX != 0 || ShadowsY != 0 || ShadowsLight != 0 || MidtonesX != 0 || MidtonesY != 0
                             || MidtonesLight != 0 || HighlightsX != 0 || HighlightsY != 0 || HighlightsLight != 0;
    public bool HasCurves => CurveMaster != "" || CurveRed != "" || CurveGreen != "" || CurveBlue != "";

    public bool HasColor => Exposure != 0 || Contrast != 0 || Saturation != 100 || Temperature != 0 || Tint != 0
                            || HasWheels || HasCurves;

    /// <summary>The same picture position, size and animation, but every colour setting back to normal.</summary>
    public ClipLook WithoutColor() => Default with
    {
        Scale = Scale, Rotation = Rotation, Opacity = Opacity, PositionX = PositionX, PositionY = PositionY,
    };
    public bool HasTransform => Scale != 100 || Rotation != 0 || Opacity != 100 || PositionX != 0 || PositionY != 0;
}

/// <summary>
/// Sound tools for a clip: a three-band equaliser (dB, -12 to +12) and noise reduction
/// (0 = off, 100 = strongest).
/// </summary>
public sealed record ClipAudio
{
    /// <summary>Bass (around 100 Hz).</summary>
    public double Low { get; init; }

    /// <summary>Voices (around 1 kHz).</summary>
    public double Mid { get; init; }

    /// <summary>Treble (around 8 kHz).</summary>
    public double High { get; init; }

    public double NoiseReduction { get; init; }

    public static ClipAudio Default { get; } = new();
}

/// <summary>The text and look of a title clip.</summary>
public sealed class TitleStyle
{
    public string Text { get; set; } = "Your title";
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Letter size in pixels, measured in a 1080-pixel-high picture (it scales with the export size).</summary>
    public double FontSize { get; set; } = 96;

    /// <summary>Colour as #RRGGBB.</summary>
    public string Color { get; set; } = "#FFFFFF";
    public bool Bold { get; set; } = true;

    /// <summary>A soft dark shadow behind the letters, so they stay readable on bright video.</summary>
    public bool Shadow { get; set; } = true;

    /// <summary>Where the middle of the text sits: 0 = left/top edge, 0.5 = centre, 1 = right/bottom edge.</summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;

    public TitleStyle Copy() => (TitleStyle)MemberwiseClone();
}

/// <summary>One row on the timeline (V1, V2… for video, A1, A2… for sound).</summary>
public partial class Track : ObservableObject
{
    /// <summary>"V1", "A2"… Tracks are renumbered when one is removed, so this can change.</summary>
    [ObservableProperty]
    private string name = "";

    public TrackKind Kind { get; init; }
    public List<Clip> Clips { get; set; } = [];

    /// <summary>Hidden (video track) or muted (audio track).</summary>
    [ObservableProperty]
    private bool isOff;

    /// <summary>Locked tracks can't be edited.</summary>
    [ObservableProperty]
    private bool isLocked;

    // ---- The audio mixer (sound tracks only) ----

    /// <summary>The whole track louder or quieter, in dB (0 = unchanged).</summary>
    [ObservableProperty]
    private double volumeDb;

    /// <summary>-1 = only the left speaker, 0 = both, 1 = only the right.</summary>
    [ObservableProperty]
    private double pan;

    /// <summary>Solo: when any sound track is solo, only the solo tracks are heard.</summary>
    [ObservableProperty]
    private bool isSolo;
}

/// <summary>A marker on the timeline (M key): a remembered moment with a name.</summary>
public sealed record Marker(long Frame, string Name, string Color = "#E8C547");

/// <summary>Everything in one .snit file.</summary>
public sealed class Project
{
    public ProjectSettings Settings { get; init; } = new();
    public ObservableCollection<MediaItem> Media { get; } = [];

    /// <summary>
    /// Tracks from top to bottom, as shown on screen: video tracks (highest number on top),
    /// then sound tracks (A1 first). New projects start with V1, V2, A1 and A2.
    /// </summary>
    public ObservableCollection<Track> Tracks { get; } =
    [
        new Track { Name = "V2", Kind = TrackKind.Video },
        new Track { Name = "V1", Kind = TrackKind.Video },
        new Track { Name = "A1", Kind = TrackKind.Audio },
        new Track { Name = "A2", Kind = TrackKind.Audio },
    ];

    /// <summary>Whether a sound track is heard: not muted, and solo when any track is solo.</summary>
    public bool IsAudible(Track track)
        => track.Kind == TrackKind.Audio && !track.IsOff
           && (track.IsSolo || !Tracks.Any(t => t.Kind == TrackKind.Audio && t.IsSolo));

    public int VideoTrackCount => Tracks.Count(t => t.Kind == TrackKind.Video);
    public int AudioTrackCount => Tracks.Count(t => t.Kind == TrackKind.Audio);

    /// <summary>Adds a video track on top (V3, V4…) or a sound track at the bottom (A3, A4…).</summary>
    public Track AddTrack(TrackKind kind)
    {
        if (kind == TrackKind.Video)
        {
            var track = new Track { Name = $"V{VideoTrackCount + 1}", Kind = TrackKind.Video };
            Tracks.Insert(0, track);
            return track;
        }
        var audio = new Track { Name = $"A{AudioTrackCount + 1}", Kind = TrackKind.Audio };
        Tracks.Add(audio);
        return audio;
    }

    /// <summary>Removes a track (there is always at least one video and one sound track) and renumbers the rest.</summary>
    public bool RemoveTrack(Track track)
    {
        int sameKind = track.Kind == TrackKind.Video ? VideoTrackCount : AudioTrackCount;
        if (sameKind <= 1 || !Tracks.Remove(track)) return false;
        RenumberTracks();
        return true;
    }

    /// <summary>Gives the tracks their names again from their order: V1 at the bottom of the video tracks, A1 at the top of the sound tracks.</summary>
    public void RenumberTracks()
    {
        var video = Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        for (int i = 0; i < video.Count; i++) video[i].Name = $"V{video.Count - i}";
        var audio = Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        for (int i = 0; i < audio.Count; i++) audio[i].Name = $"A{i + 1}";
    }

    /// <summary>The track with this name, adding tracks until it exists (used when opening projects with more tracks).</summary>
    public Track EnsureTrack(string name)
    {
        if (Tracks.FirstOrDefault(t => t.Name == name) is Track existing) return existing;
        TrackKind kind = name.StartsWith('A') ? TrackKind.Audio : TrackKind.Video;
        int number = int.TryParse(name.AsSpan(1), out int n) ? n : 0;
        Track added;
        do added = AddTrack(kind);
        while (added.Name != name && (kind == TrackKind.Video ? VideoTrackCount : AudioTrackCount) < Math.Max(number, 1) && number < 100);
        return Tracks.FirstOrDefault(t => t.Name == name) ?? added;
    }

    /// <summary>The "in" and "out" marks (I and O keys): a part of the timeline, e.g. to export only that.</summary>
    public long? InPoint { get; set; }
    public long? OutPoint { get; set; }

    /// <summary>Markers on the timeline, in time order.</summary>
    public List<Marker> Markers { get; set; } = [];

    /// <summary>Where the project is saved, or null if it never was.</summary>
    public string? FilePath { get; set; }

    public double Fps => Settings.FrameRate;

    public IEnumerable<Clip> AllClips => Tracks.SelectMany(t => t.Clips);

    /// <summary>The end of the last clip, in frames.</summary>
    public long DurationFrames => AllClips.Select(c => c.End).DefaultIfEmpty(0).Max();

    public Track TrackOf(Clip clip) => Tracks.First(t => t.Clips.Contains(clip));

    public Track TrackByName(string name) => Tracks.First(t => t.Name == name);

    /// <summary>The clip plus any clips linked to it.</summary>
    public IEnumerable<Clip> LinkedGroup(Clip clip)
        => clip.LinkId is null ? [clip] : AllClips.Where(c => c.LinkId == clip.LinkId);

    /// <summary>The sound track that goes with a video track (V1 → A1, V2 → A2…; A1 when there is no match).</summary>
    public Track PartnerAudioTrack(Track videoTrack)
        => Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio && t.Name == "A" + videoTrack.Name[1..])
           ?? Tracks.First(t => t.Kind == TrackKind.Audio);

    /// <summary>The lowest video / sound track (V1 / A1), the usual place for new clips.</summary>
    public Track FirstVideoTrack => Tracks.Last(t => t.Kind == TrackKind.Video);
    public Track FirstAudioTrack => Tracks.First(t => t.Kind == TrackKind.Audio);

    /// <summary>The second video track (V2) if there is one, for titles; otherwise V1.</summary>
    public Track TitleTrack => Tracks.FirstOrDefault(t => t.Name == "V2") ?? FirstVideoTrack;
}
