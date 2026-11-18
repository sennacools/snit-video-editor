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
        new("MP4 · H.265 (coming later)", ExportFormat.Mp4H265, false),
        new("WebM · VP9 (coming later)", ExportFormat.WebmVp9, false),
        new("MOV · ProRes (coming later)", ExportFormat.MovProRes, false),
        new("GIF (coming later)", ExportFormat.Gif, false),
        new("Audio only · MP3 (coming later)", ExportFormat.Mp3, false),
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

    [ObservableProperty, NotifyPropertyChangedFor(nameof(VideoSummary))]
    private Choice<ExportFormat> selectedFormat;

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

    [ObservableProperty]
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

    public string VideoSummary => $"H.264 · {BitrateText}{(UseGpu && IsGpuAvailable ? " · graphics card" : "")}";

    public string AudioSummary => "AAC · 320 kbps · stereo";

    public string EstimatedSize
    {
        get
        {
            double megabytes = (BitrateMbps + 0.32) * DurationSeconds / 8;
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
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
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

        string output = Path.Combine(Folder, name + ".mp4");
        if (File.Exists(output) &&
            MessageBox.Show($"{name}.mp4 already exists. Replace it?", "Export", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var settings = new ExportSettings
        {
            OutputPath = output,
            Format = SelectedFormat.Value,
            Width = SelectedResolution.Value.Width,
            Height = SelectedResolution.Value.Height,
            FrameRate = SelectedFrameRate.Value,
            VideoBitrateMbps = BitrateMbps,
            VideoEncoder = UseGpu && gpuEncoder is not null ? gpuEncoder : "libx264",
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
            StatusText = $"Done! Saved as {name}.mp4";
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
