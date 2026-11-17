using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>
/// Everything the main window shows and can do. The window (XAML) binds to these
/// properties; it holds no logic of its own. [ObservableProperty] generates a public
/// property (e.g. "projectName" → ProjectName) that tells the screen when it changes.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // ---- Project ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectInfo))]
    private string projectName = "Untitled.snit";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectInfo))]
    private int frameWidth = 1920;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectInfo))]
    private int frameHeight = 1080;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectInfo), nameof(CurrentTimecode), nameof(DurationTimecode))]
    private double frameRate = 30;

    /// <summary>The text in the middle of the title bar.</summary>
    public string ProjectInfo => $"{ProjectName} — {FrameWidth}×{FrameHeight} · {FrameRate:0.##} fps";

    // ---- Workspace and panels ----

    /// <summary>"Edit", "Color" or "Audio". Only Edit has its own layout so far.</summary>
    [ObservableProperty]
    private string workspace = "Edit";

    /// <summary>"Media", "Effects", "Transitions" or "Text" (tabs in the media bin).</summary>
    [ObservableProperty]
    private string mediaTab = "Media";

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private bool showSafeGuides = true;

    // ---- Playback and timeline ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentTimecode), nameof(PlayheadX))]
    private double playheadSeconds;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationTimecode))]
    private double durationSeconds;

    public string CurrentTimecode => Timecode.Format(PlayheadSeconds, FrameRate);
    public string DurationTimecode => Timecode.Format(DurationSeconds, FrameRate);

    /// <summary>Timeline zoom: how many pixels one second takes up.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayheadX))]
    private double pixelsPerSecond = 60;

    public const double MinPixelsPerSecond = 10;
    public const double MaxPixelsPerSecond = 300;

    /// <summary>Where the playhead line sits, in pixels from the start of the timeline.</summary>
    public double PlayheadX => PlayheadSeconds * PixelsPerSecond;

    [ObservableProperty]
    private bool snappingEnabled = true;

    /// <summary>"Select", "Razor", "Ripple", "Text", "Hand" or "Zoom".</summary>
    [ObservableProperty]
    private string activeTool = "Select";

    [RelayCommand]
    private void ZoomIn() => PixelsPerSecond = Math.Min(MaxPixelsPerSecond, PixelsPerSecond * 1.25);

    [RelayCommand]
    private void ZoomOut() => PixelsPerSecond = Math.Max(MinPixelsPerSecond, PixelsPerSecond / 1.25);

    // ---- FFmpeg check ----

    /// <summary>True when the "FFmpeg is missing" bar should show at the top of the window.</summary>
    [ObservableProperty]
    private bool isFfmpegBannerVisible;

    [ObservableProperty]
    private bool isCheckingFfmpeg;

    public string FfmpegInstallCommand => FFmpegLocator.InstallCommand;

    [RelayCommand]
    private async Task CheckFfmpegAsync()
    {
        IsCheckingFfmpeg = true;
        FFmpegStatus status = await FFmpegLocator.CheckAsync();
        IsCheckingFfmpeg = false;
        IsFfmpegBannerVisible = !status.IsAvailable;
    }

    /// <summary>Help → Check FFmpeg: checks again and always reports the result.</summary>
    [RelayCommand]
    private async Task ReportFfmpegStatusAsync()
    {
        await CheckFfmpegAsync();
        FFmpegStatus status = FFmpegLocator.Current;
        string message = status.IsAvailable
            ? $"FFmpeg is installed and working.\n\n{status.Version}\n{status.FfmpegPath}"
            : $"FFmpeg was not found.\n\nInstall it by running this in a terminal, then click \"Check again\":\n\n{FfmpegInstallCommand}";
        MessageBox.Show(message, "FFmpeg", MessageBoxButton.OK,
            status.IsAvailable ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void CopyFfmpegInstallCommand() => Clipboard.SetText(FfmpegInstallCommand);

    [RelayCommand]
    private void DismissFfmpegBanner() => IsFfmpegBannerVisible = false;

    // ---- Help / app ----

    [RelayCommand]
    private void ShowAbout()
    {
        string ffmpeg = FFmpegLocator.Current.Version ?? "FFmpeg not found";
        MessageBox.Show(
            $"Snit — a small video editor.\n(\"snit\" is Dutch for \"cut\".)\n\n{ffmpeg}",
            "About Snit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();
}
