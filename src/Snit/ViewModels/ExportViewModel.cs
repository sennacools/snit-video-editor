using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>One option in a dropdown. Options that aren't built yet are shown greyed out.</summary>
public sealed record Choice<T>(string Label, T Value, bool IsAvailable = true);

/// <summary>The export dialog: settings, a summary, and the export itself with progress.</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly Project project;
    private CancellationTokenSource? cancel;
    private string? gpuEncoder;
    private string? exportedPath;

    public ExportViewModel(Project project, ImageSource? preview)
    {
        this.project = project;
        Preview = preview;

        fileName = project.FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(project.FilePath);
        folder = project.FilePath is not null
            ? Path.GetDirectoryName(project.FilePath)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        selectedFormat = Formats[0];
        // Start with the project's own size and frame rate when they are in the list.
        selectedResolution = Resolutions.FirstOrDefault(r => r.Value.Width == project.Settings.Width && r.Value.Height == project.Settings.Height)
                             ?? Resolutions[0];
        selectedFrameRate = FrameRates.FirstOrDefault(f => Math.Abs(f.Value - project.Fps) < 0.01) ?? FrameRates[2];
    }

    /// <summary>The window closes itself when this fires.</summary>
    public event Action? CloseRequested;

    public ImageSource? Preview { get; }

    // ===================== Settings =====================

    [ObservableProperty]
    private string fileName;

    [ObservableProperty]
    private string folder;

    public IReadOnlyList<Choice<ExportFormat>> Formats { get; } =
    [
        new("MP4 · H.264", ExportFormat.Mp4H264),
        new("MP4 · H.265 (smaller files)", ExportFormat.Mp4H265),
        new("WebM · VP9 (for the web)", ExportFormat.WebmVp9),
        new("MOV · ProRes 422 HQ (for editing)", ExportFormat.MovProRes),
        new("GIF (no sound)", ExportFormat.Gif),
        new("Audio only · MP3", ExportFormat.Mp3),
    ];

    public IReadOnlyList<Choice<(int Width, int Height)>> Resolutions { get; } =
    [
        new("1920 × 1080 (Full HD)", (1920, 1080)),
        new("3840 × 2160 (4K)", (3840, 2160)),
        new("1280 × 720 (HD)", (1280, 720)),
        new("1080 × 1920 (vertical)", (1080, 1920)),
    ];

    public IReadOnlyList<Choice<double>> FrameRates { get; } =
    [
        new("24 fps", 24), new("25 fps", 25), new("30 fps", 30), new("60 fps", 60),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoSummary), nameof(AudioSummary), nameof(EstimatedSize),
        nameof(HasPicture), nameof(HasQuality), nameof(CanUseGpu), nameof(Extension))]
    private Choice<ExportFormat> selectedFormat;

    /// <summary>MP3 has no picture: resolution and frame rate don't matter then.</summary>
    public bool HasPicture => SelectedFormat.Value != ExportFormat.Mp3;

    /// <summary>The quality slider only works for H.264, H.265 and VP9 (ProRes, GIF and MP3 have fixed quality).</summary>
    public bool HasQuality => SelectedFormat.Value is ExportFormat.Mp4H264 or ExportFormat.Mp4H265 or ExportFormat.WebmVp9;

    /// <summary>The graphics card can make H.264 and H.265.</summary>
    public bool CanUseGpu => IsGpuAvailable && SelectedFormat.Value is ExportFormat.Mp4H264 or ExportFormat.Mp4H265;

    public string Extension => ExportCommandBuilder.Extension(SelectedFormat.Value);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BitrateMbps), nameof(BitrateText), nameof(VideoSummary), nameof(EstimatedSize))]
    private Choice<(int Width, int Height)> selectedResolution;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(BitrateMbps), nameof(BitrateText), nameof(VideoSummary), nameof(EstimatedSize))]
    private Choice<double> selectedFrameRate;

    /// <summary>True = the whole timeline; false = only between the in and out marks.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationText), nameof(EstimatedSize))]
    private bool wholeSequence = true;

    /// <summary>"In to out" can only be picked when both marks are set (I and O keys).</summary>
    public bool CanUseRange => project.InPoint is long i && project.OutPoint is long o && o > i;

    public string RangeToolTip => CanUseRange
        ? "Export only the part between the in and out marks"
        : "Set an in mark (I) and an out mark (O) on the timeline first";

    /// <summary>The quality slider, 0–100.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BitrateMbps), nameof(BitrateText), nameof(VideoSummary), nameof(EstimatedSize))]
    private double quality = 60;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoSummary))]
    private bool useGpu;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUseGpu))]
    private bool isGpuAvailable;

    [ObservableProperty]
    private string gpuStatus = "Checking your graphics card…";

    /// <summary>
    /// Video bitrate in megabits per second. For 1080p at 30 fps the slider goes from 4 to 32 Mbps;
    /// bigger pictures and higher frame rates get more (but not proportionally more).
    /// </summary>
    public double BitrateMbps
    {
        get
        {
            var (w, h) = SelectedResolution.Value;
            double sizeFactor = w * h * SelectedFrameRate.Value / (1920.0 * 1080 * 30);
            double mbps = (4 + 28 * Quality / 100) * Math.Pow(sizeFactor, 0.75);
            return Math.Round(mbps, mbps < 10 ? 1 : 0);
        }
    }

    public string BitrateText => $"{BitrateMbps:0.#} Mbps";

    // ===================== Summary =====================

    private long ExportFrames => WholeSequence || !CanUseRange
        ? project.DurationFrames
        : Math.Min(project.OutPoint!.Value, project.DurationFrames) - project.InPoint!.Value;

    private double DurationSeconds => ExportFrames / project.Fps;

    public string DurationText => Timecode.FromFrames(ExportFrames, project.Fps);

    private bool GpuInUse => UseGpu && CanUseGpu;

    public string VideoSummary => SelectedFormat.Value switch
    {
        ExportFormat.Mp4H264 => $"H.264 · {BitrateText}{(GpuInUse ? " · graphics card" : "")}",
        ExportFormat.Mp4H265 => $"H.265 · {BitrateText}{(GpuInUse ? " · graphics card" : "")}",
        ExportFormat.WebmVp9 => $"VP9 · {BitrateText}",
        ExportFormat.MovProRes => "ProRes 422 HQ",
        ExportFormat.Gif => "GIF · 256 colours",
        _ => "none",
    };

    public string AudioSummary => SelectedFormat.Value switch
    {
        ExportFormat.WebmVp9 => "Opus · 192 kbps · stereo",
        ExportFormat.MovProRes => "PCM (uncompressed) · stereo",
        ExportFormat.Gif => "none (GIFs have no sound)",
        ExportFormat.Mp3 => "MP3 · 320 kbps · stereo",
        _ => "AAC · 320 kbps · stereo",
    };

    /// <summary>A rough guess of the file size (megabits per second × seconds ÷ 8).</summary>
    public string EstimatedSize
    {
        get
        {
            var (w, h) = SelectedResolution.Value;
            double pixelsPerSecond = w * h * SelectedFrameRate.Value;
            double mbps = SelectedFormat.Value switch
            {
                ExportFormat.MovProRes => 220 * pixelsPerSecond / (1920.0 * 1080 * 30) + 1.5, // ProRes HQ ≈ 220 Mbps at 1080p30
                ExportFormat.Gif => 0.5 * pixelsPerSecond / 1e6,                                 // very rough: GIFs vary a lot
                ExportFormat.Mp3 => 0.32,
                ExportFormat.WebmVp9 => BitrateMbps + 0.19,
                _ => BitrateMbps + 0.32,
            };
            double megabytes = mbps * DurationSeconds / 8;
            return megabytes >= 1000 ? $"≈ {megabytes / 1000:0.0} GB" : $"≈ {Math.Max(1, megabytes):0} MB";
        }
    }

    // ===================== Progress =====================

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CancelButtonText), nameof(IsSettingsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool isExporting;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CancelButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool isDone;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private string timeLeftText = "";

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private bool isError;

    public bool IsSettingsEnabled => !IsExporting;

    public string CancelButtonText => IsExporting ? "Stop" : IsDone ? "Close" : "Cancel";

    // ===================== Actions =====================

    /// <summary>Called when the dialog opens: look for a graphics-card encoder in the background.</summary>
    public async Task InitializeAsync()
    {
        var gpu = await GpuEncoderDetector.DetectAsync();
        gpuEncoder = gpu?.Encoder;
        IsGpuAvailable = gpu is not null;
        UseGpu = IsGpuAvailable;
        GpuStatus = gpu is { } g ? $"{g.Name} found" : "No supported graphics card found; using the processor";
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Save the video in…", InitialDirectory = Folder };
        if (dialog.ShowDialog() == true) Folder = dialog.FolderName;
    }

    private bool CanExport() => !IsExporting && !IsDone;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        string name = FileName.Trim();
        if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^Extension.Length];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Please choose a file name without characters like \\ / : * ? \" < > |");
            return;
        }
        if (!Directory.Exists(Folder))
        {
            ShowError("The folder to save in doesn't exist.");
            return;
        }

        string output = Path.Combine(Folder, name + Extension);
        if (File.Exists(output) &&
            MessageBox.Show($"{name}{Extension} already exists. Replace it?", "Export", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var settings = new ExportSettings
        {
            OutputPath = output,
            Format = SelectedFormat.Value,
            Width = SelectedResolution.Value.Width,
            Height = SelectedResolution.Value.Height,
            FrameRate = SelectedFrameRate.Value,
            VideoBitrateMbps = BitrateMbps,
            VideoEncoder = GpuInUse && gpuEncoder is not null ? gpuEncoder : "libx264",
            RangeStart = WholeSequence || !CanUseRange ? null : project.InPoint,
            RangeEnd = WholeSequence || !CanUseRange ? null : project.OutPoint,
        };

        ExportPlan plan;
        try { plan = ExportCommandBuilder.Build(project, settings); }
        catch (Exception ex) { ShowError(ex.Message); return; }

        IsExporting = true;
        IsError = false;
        StatusText = "";
        ProgressValue = 0;
        ProgressText = $"Frame 0 of {plan.TotalFrames:N0}";
        TimeLeftText = "Starting…";
        cancel = new CancellationTokenSource();

        // Progress<T> made here reports back on this (the window's) thread.
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressValue = p.Fraction;
            ProgressText = $"Frame {p.Frame:N0} of {p.TotalFrames:N0}";
            TimeLeftText = p.Remaining is TimeSpan left ? $"About {FormatTime(left)} left" : "Working…";
        });

        try
        {
            await new Exporter().RunAsync(plan, progress, cancel.Token);
            exportedPath = output;
            IsDone = true;
            ProgressValue = 1;
            TimeLeftText = "";
            StatusText = $"Done! Saved as {name}{Extension}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export stopped.";
            ProgressValue = 0;
            ProgressText = TimeLeftText = "";
        }
        catch (ExportFailedException ex)
        {
            ShowError("FFmpeg couldn't make the video:\n" + ex.Message);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsExporting = false;
            cancel.Dispose();
            cancel = null;
        }
    }

    private void ShowError(string message)
    {
        IsError = true;
        StatusText = message;
        ProgressText = TimeLeftText = "";
    }

    /// <summary>Cancel / Stop / Close, depending on the moment.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsExporting) cancel?.Cancel();
        else CloseRequested?.Invoke();
    }

    /// <summary>Opens Explorer with the new video selected.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        if (exportedPath is not null)
            Process.Start("explorer.exe", $"/select,\"{exportedPath}\"");
    }

    /// <summary>Closing the window during an export asks first, then stops it.</summary>
    public bool ConfirmClose()
    {
        if (!IsExporting) return true;
        if (MessageBox.Show("Stop the export?", "Export", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        cancel?.Cancel();
        return true;
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.TotalMinutes >= 1 ? t.ToString(@"m\:ss") : $"{Math.Max(1, (int)t.TotalSeconds)} s";
}
