using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Snit.Core;

// The project "model": plain data about media, tracks and clips.
// Times on the timeline are counted in whole frames (at the project frame rate),
// so cuts always land exactly on a frame and numbers never drift.

public enum MediaKind { Video, Audio, Image }

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

    /// <summary>How many frames of this file can be used. Pictures can be stretched to any length.</summary>
    public long LengthInFrames(double fps)
        => Kind == MediaKind.Image ? long.MaxValue : Math.Max(1, (long)Math.Floor(DurationSeconds * fps + 1e-6));
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

    public long End => Start + Length;
    public string Name => Media.Name;

    public Clip Copy() => (Clip)MemberwiseClone();
}

/// <summary>One row on the timeline (V2, V1, A1, A2).</summary>
public partial class Track : ObservableObject
{
    public required string Name { get; init; }
    public TrackKind Kind { get; init; }
    public List<Clip> Clips { get; set; } = [];

    /// <summary>Hidden (video track) or muted (audio track).</summary>
    [ObservableProperty]
    private bool isOff;

    /// <summary>Locked tracks can't be edited.</summary>
    [ObservableProperty]
    private bool isLocked;
}

/// <summary>Everything in one .snit file.</summary>
public sealed class Project
{
    public ProjectSettings Settings { get; init; } = new();
    public ObservableCollection<MediaItem> Media { get; } = [];

    /// <summary>Tracks from top to bottom, as shown on screen.</summary>
    public IReadOnlyList<Track> Tracks { get; } =
    [
        new Track { Name = "V2", Kind = TrackKind.Video },
        new Track { Name = "V1", Kind = TrackKind.Video },
        new Track { Name = "A1", Kind = TrackKind.Audio },
        new Track { Name = "A2", Kind = TrackKind.Audio },
    ];

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

    /// <summary>The audio track that goes with a video track (V1 → A1, V2 → A2).</summary>
    public Track PartnerAudioTrack(Track videoTrack) => TrackByName(videoTrack.Name == "V2" ? "A2" : "A1");
}
