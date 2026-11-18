using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>
/// The main window's "brain". It owns the project and connects the pieces:
/// the media bin (<see cref="Bin"/>), the timeline (<see cref="Timeline"/>) and the
/// preview player. It also handles the File menu (new, open, save) and playback buttons.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private Project project;
    private PlaybackEngine playback;
    private bool positionFromPlayer; // true while the player itself moves the playhead

    public MainViewModel()
    {
        project = new Project();
        Timeline = new TimelineViewModel(project);
        Bin = new MediaBinViewModel(project, Timeline);
        Bin.ProxiesChanged += RefreshPreviewSources;
        Inspector = new InspectorViewModel(Timeline);
        Source = new SourceMonitorViewModel(() => Timeline);
        playback = CreatePlayer(project);

        Timeline.ContentChanged += OnTimelineContentChanged;
        Timeline.PropertyChanged += OnTimelinePropertyChanged;

        settings = AppSettings.Load();
        foreach (string recent in settings.RecentProjects.Where(File.Exists)) RecentProjects.Add(recent);

        // Auto-save every two minutes while there are unsaved changes.
        autoSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        autoSaveTimer.Tick += (_, _) => AutoSaveNow();
        autoSaveTimer.Start();
    }

    public TimelineViewModel Timeline { get; }
    public MediaBinViewModel Bin { get; }
    public InspectorViewModel Inspector { get; }

    // ===================== Recent projects and auto-save =====================

    private readonly AppSettings settings;
    private readonly System.Windows.Threading.DispatcherTimer autoSaveTimer;

    /// <summary>File → Open recent: the last projects you opened or saved (newest first).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>"Auto-saved at 14:32", shown in the title bar's tooltip.</summary>
    [ObservableProperty]
    private string autoSaveStatus = "Not auto-saved yet";

    private void RememberRecent(string path)
    {
        settings.AddRecent(path);
        settings.Save();
        RecentProjects.Clear();
        foreach (string recent in settings.RecentProjects) RecentProjects.Add(recent);
    }

    [RelayCommand]
    private void OpenRecent(string? path)
    {
        if (path is null || !ConfirmDiscardChanges()) return;
        if (!File.Exists(path))
        {
            MessageBox.Show($"This project can't be found any more:\n{path}", "Open recent", MessageBoxButton.OK, MessageBoxImage.Warning);
            settings.RecentProjects.Remove(path);
            settings.Save();
            RecentProjects.Remove(path);
            return;
        }
        OpenProjectFile(path);
    }

    /// <summary>Writes the auto-save copy now (if there is anything unsaved).</summary>
    public void AutoSaveNow()
    {
        if (!IsDirty) return;
        try
        {
            AutoSave.Write(project);
            AutoSaveStatus = $"Auto-saved at {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "Auto-save failed: " + ex.Message;
        }
    }
    public SourceMonitorViewModel Source { get; }

    /// <summary>"Program" (the timeline) or "Source" (one file from the bin).</summary>
    [ObservableProperty]
    private string monitorTab = "Program";

    partial void OnMonitorTabChanged(string value)
    {
        // Only one monitor plays at a time.
        if (value == "Source" && IsPlaying) PlayPause();
        if (value == "Program") Source.Stop();
    }

    /// <summary>Double-click in the bin: watch the file in the Source monitor.</summary>
    [RelayCommand]
    private void OpenInSource(MediaItem? item)
    {
        if (item is null || item.IsOffline || !FFmpegLocator.Current.IsAvailable) return;
        Source.Open(item, project.Settings);
        MonitorTab = "Source";
    }

    // ===================== Title bar =====================

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProjectInfo), nameof(WindowTitle))]
    private bool isDirty;

    public string ProjectName => project.FilePath is null ? "Untitled.snit" : Path.GetFileName(project.FilePath);

    /// <summary>The text in the middle of the title bar. A dot means "not saved yet".</summary>
    public string ProjectInfo =>
        $"{ProjectName}{(IsDirty ? " •" : "")} — {project.Settings.Width}×{project.Settings.Height} · {project.Settings.FrameRate:0.##} fps";

    public string WindowTitle => $"{ProjectName}{(IsDirty ? " •" : "")} — Snit";

    /// <summary>"Edit", "Color" or "Audio". Only Edit has its own layout so far.</summary>
    [ObservableProperty]
    private string workspace = "Edit";

    [ObservableProperty]
    private bool showSafeGuides = true;

    // ===================== Preview and playback =====================

    /// <summary>The pictures in the program monitor, one per video track (V1 first, so it is at the bottom).</summary>
    public System.Collections.ObjectModel.ObservableCollection<PreviewLayer> PreviewLayers { get; } = [];

    /// <summary>The project frame size; the monitor works in these pixels.</summary>
    public int FrameWidth => project.Settings.Width;
    public int FrameHeight => project.Settings.Height;

    public string PreviewQualityText => $"Preview quality: {playback.PreviewWidth}×{playback.PreviewHeight} · Fit"
                                        + (UseProxies && project.Media.Any(m => m.HasProxy) ? " · Proxies" : "");

    /// <summary>View → Use proxies: preview big videos from their small copies (the export always uses the originals).</summary>
    [ObservableProperty]
    private bool useProxies = true;

    partial void OnUseProxiesChanged(bool value) => RefreshPreviewSources();

    /// <summary>Proxies were made or switched on/off: read the video from the right files again.</summary>
    private void RefreshPreviewSources()
    {
        playback.UseProxies = UseProxies;
        playback.UpdateTimeline(project);
        if (!IsPlaying) playback.Seek(Timeline.PlayheadFrame);
        OnPropertyChanged(nameof(PreviewQualityText));
    }

    /// <summary>Set by the window: takes a small picture of the monitor (for the export dialog).</summary>
    public Func<ImageSource?>? CapturePreview { get; set; }

    /// <summary>A small copy of the program picture (width in pixels), for the scopes.</summary>
    public Func<int, System.Windows.Media.Imaging.BitmapSource?>? CaptureFrame { get; set; }

    [ObservableProperty]
    private bool isPlaying;

    /// <summary>Sound level of the left channel for the meters (loudest sample, 0–1).</summary>
    [ObservableProperty]
    private double levelLeft;

    /// <summary>Sound level of the right channel for the meters (loudest sample, 0–1).</summary>
    [ObservableProperty]
    private double levelRight;

    private PlaybackEngine CreatePlayer(Project p)
    {
        var engine = new PlaybackEngine(p.Settings) { UseProxies = UseProxies };
        engine.PositionChanged += frame =>
        {
            positionFromPlayer = true;
            Timeline.PlayheadFrame = frame;
            positionFromPlayer = false;
            Timeline.KeepPlayheadVisible();
        };
        engine.Stopped += () =>
        {
            IsPlaying = false;
            if (shuttleSpeed > 0) shuttleSpeed = 0;
        };
        engine.LevelsChanged += (left, right) =>
        {
            LevelLeft = left;
            LevelRight = right;
        };
        engine.FrameReady += OnFrameReady;
        engine.UpdateTimeline(p);
        engine.Seek(Timeline.PlayheadFrame);
        return engine;
    }

    // ===================== Showing the player's pictures =====================

    private readonly Dictionary<string, ImageSource> titlePictures = [];

    /// <summary>The player has new pictures: put each track's picture (or title) on its layer.</summary>
    private void OnFrameReady(LayerFrame?[] bundle)
        => PreviewLayer.ApplyBundle(PreviewLayers, bundle, project.Settings, TitlePicture);

    /// <summary>Titles are drawn once (per text and look) and then remembered.</summary>
    private ImageSource TitlePicture(TitleStyle s)
    {
        string key = $"{s.Text}|{s.FontFamily}|{s.FontSize}|{s.Color}|{s.Bold}|{s.Shadow}|{s.Box}|{s.X}|{s.Y}";
        if (!titlePictures.TryGetValue(key, out ImageSource? picture))
        {
            if (titlePictures.Count > 50) titlePictures.Clear();
            titlePictures[key] = picture = TitleRenderer.Render(s, project.Settings.Width, project.Settings.Height);
        }
        return picture;
    }

    private void OnTimelineContentChanged()
    {
        IsDirty = true;
        // A sequence that was just dropped in (or nested) is shown once it has been rendered.
        if (project.AllClips.Any(c => c.Media.IsSequence && c.Media.IsOffline)) _ = RefreshNestedAsync();
        ExportCommand.NotifyCanExecuteChanged(); // Export is only possible with something on the timeline
        playback.UpdateTimeline(project);
        if (!IsPlaying) playback.Seek(Timeline.PlayheadFrame);
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimelineViewModel.PlayheadFrame)) return;
        if (positionFromPlayer) return;

        // You moved the playhead (ruler, arrow keys...): show that frame, or keep playing from there.
        if (IsPlaying) playback.Play(Timeline.PlayheadFrame, Math.Max(1, shuttleSpeed));
        else playback.Seek(Timeline.PlayheadFrame);
    }

    [RelayCommand]
    private void PlayPause()
    {
        StopShuttle();
        if (IsPlaying)
        {
            playback.Stop();
            IsPlaying = false;
        }
        else
        {
            if (Timeline.DurationFrames == 0) return;
            if (Timeline.PlayheadFrame >= Timeline.DurationFrames) Timeline.PlayheadFrame = 0;
            IsPlaying = true;
            playback.Play(Timeline.PlayheadFrame);
        }
    }

    // ---- Shuttle: J (backwards), K (stop), L (forwards); pressing again goes faster ----

    private int shuttleSpeed; // 0 = off; 1, 2, 4 = forwards; -1, -2, -4 = backwards
    private System.Windows.Threading.DispatcherTimer? backwardsTimer;

    public void ShuttleForward() => StartShuttle(shuttleSpeed <= 0 ? 1 : Math.Min(4, shuttleSpeed * 2));

    public void ShuttleBackward() => StartShuttle(shuttleSpeed >= 0 ? -1 : Math.Max(-4, shuttleSpeed * 2));

    public void ShuttleStop()
    {
        StopShuttle();
        if (IsPlaying)
        {
            playback.Stop();
            IsPlaying = false;
        }
    }

    private void StartShuttle(int speed)
    {
        if (Timeline.DurationFrames == 0) return;
        StopShuttle();
        shuttleSpeed = speed;

        if (speed > 0)
        {
            // Forwards: the player plays faster (without sound above normal speed).
            if (Timeline.PlayheadFrame >= Timeline.DurationFrames) Timeline.PlayheadFrame = 0;
            IsPlaying = true;
            playback.Play(Timeline.PlayheadFrame, speed);
            return;
        }

        // Backwards: the player can only read forwards, so we move the playhead back step by
        // step and show each frame (a bit choppy, like reversed clips).
        if (IsPlaying)
        {
            playback.Stop();
            IsPlaying = false;
        }
        long startFrame = Timeline.PlayheadFrame;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        backwardsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / project.Fps) };
        backwardsTimer.Tick += (_, _) =>
        {
            long frame = startFrame - (long)(clock.Elapsed.TotalSeconds * project.Fps * -shuttleSpeed);
            Timeline.PlayheadFrame = Math.Max(0, frame);
            if (frame <= 0) StopShuttle();
        };
        backwardsTimer.Start();
    }

    private void StopShuttle()
    {
        shuttleSpeed = 0;
        backwardsTimer?.Stop();
        backwardsTimer = null;
    }

    [RelayCommand]
    private void GoToStart() => Timeline.PlayheadFrame = 0;

    [RelayCommand]
    private void GoToEnd() => Timeline.PlayheadFrame = Timeline.DurationFrames;

    [RelayCommand]
    private void StepBack()
    {
        if (IsPlaying) PlayPause();
        Timeline.PlayheadFrame = Math.Max(0, Timeline.PlayheadFrame - 1);
    }

    [RelayCommand]
    private void StepForward()
    {
        if (IsPlaying) PlayPause();
        Timeline.PlayheadFrame++;
    }

    // ===================== File menu =====================

    [RelayCommand]
    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        SwitchTo(new Project());
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (!ConfirmDiscardChanges()) return;
        var dialog = new OpenFileDialog { Title = "Open project", Filter = ProjectFile.DialogFilter };
        if (dialog.ShowDialog() != true) return;
        OpenProjectFile(dialog.FileName);
    }

    /// <summary>Opens a .snit file (also used when Snit is started by double-clicking one).</summary>
    public void OpenProjectFile(string path)
    {
        try
        {
            // Is there an auto-saved copy that is newer (for example after a crash)?
            string openPath = path;
            if (AutoSave.NewerCopyOf(path) is string copy &&
                MessageBox.Show($"Snit found an auto-saved version of this project from {File.GetLastWriteTime(copy):d MMM HH:mm}, " +
                                "newer than the saved one. Open the auto-saved version?",
                    "Recover project", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                openPath = copy;

            Project loaded = ProjectFile.Load(openPath);
            loaded.FilePath = path; // it stays this project, even when opened from the auto-save copy
            RememberRecent(path);
            SwitchTo(loaded);
            if (openPath != path) IsDirty = true; // recovered changes are not in the project file yet

            var missing = loaded.Media.Where(m => m.IsOffline && !m.IsSequence).Select(m => m.FilePath).ToList(); // sequences are rendered by Snit itself
            if (missing.Count > 0)
                MessageBox.Show("Some media files can't be found (moved or deleted?):\n\n" + string.Join("\n", missing),
                    "Missing media", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the project.\n\n{ex.Message}", "Open project",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save() => TrySave();

    /// <summary>Saves (asking for a file name the first time). Returns false if it didn't happen.</summary>
    private bool TrySave() => project.FilePath is null ? SaveAsCore() : SaveTo(project.FilePath);

    [RelayCommand]
    private void SaveAs() => SaveAsCore();

    private bool SaveAsCore()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save project",
            Filter = ProjectFile.DialogFilter,
            DefaultExt = ProjectFile.Extension,
            FileName = Path.GetFileNameWithoutExtension(ProjectName),
        };
        return dialog.ShowDialog() == true && SaveTo(dialog.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            ProjectFile.Save(project, path);
            AutoSave.Delete(path);  // the real file is up to date now
            AutoSave.Delete(null);  // and an unsaved "Untitled" copy is not needed any more
            RememberRecent(path);
            IsDirty = false;
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectInfo));
            OnPropertyChanged(nameof(WindowTitle));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save the project.\n\n{ex.Message}", "Save project",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>If there are unsaved changes, asks whether to save them. Returns false if you clicked Cancel.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show($"Save changes to {ProjectName}?", "Snit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => TrySave(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    public int SequenceWidth => project.Settings.Width;
    public int SequenceHeight => project.Settings.Height;
    public double SequenceFps => project.Settings.FrameRate;

    /// <summary>Timeline → Sequence settings: a new size and frame rate (clips are converted).</summary>
    public void ApplySequenceSettings(int width, int height, double fps)
    {
        if (width == project.Settings.Width && height == project.Settings.Height && Math.Abs(fps - project.Settings.FrameRate) < 1e-9)
            return;
        if (IsPlaying) PlayPause();
        long playhead = Timeline.PlayheadFrame;
        double oldFps = project.Settings.FrameRate;
        SequenceSettings.Apply(project, width, height, fps);
        SwitchTo(project, dirty: true);
        Timeline.PlayheadFrame = (long)Math.Round(playhead * fps / oldFps);
    }

    private void SwitchTo(Project newProject, bool dirty = false)
    {
        if (IsPlaying) PlayPause();
        project = newProject;
        Bin.Load(project);
        ReloadSequence();
        IsDirty = dirty;
        OnPropertyChanged(nameof(Sequences));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WindowTitle));
        _ = RefreshNestedAsync();
    }

    /// <summary>Shows the open sequence: the timeline, and a fresh player (its size may differ).</summary>
    private void ReloadSequence()
    {
        Timeline.Load(project);
        playback.Dispose();
        playback = CreatePlayer(project);
        ExportCommand.NotifyCanExecuteChanged();
        titlePictures.Clear();
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
        OnPropertyChanged(nameof(PreviewQualityText));
        OnPropertyChanged(nameof(ProjectInfo));
        OnPropertyChanged(nameof(ActiveSequence));
    }

    // ===================== Sequences =====================

    /// <summary>The project's sequences (the dropdown above the timeline).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Sequence> Sequences => project.Sequences;

    /// <summary>The sequence in the timeline. Choosing another one in the dropdown opens it.</summary>
    public Sequence ActiveSequence
    {
        get => project.ActiveSequence;
        set { if (value is not null) OpenSequence(value); }
    }

    /// <summary>Work going on in the background, e.g. "Rendering “Intro”…" (shown above the timeline).</summary>
    [ObservableProperty]
    private string backgroundStatus = "";

    /// <summary>Opens another sequence in the timeline (its undo history starts fresh).</summary>
    public void OpenSequence(Sequence sequence)
    {
        if (sequence == project.ActiveSequence || !project.Sequences.Contains(sequence)) return;
        if (IsPlaying) PlayPause();
        // The sequence we leave may be used elsewhere: its bin item gets its new length.
        if (project.MediaFor(project.ActiveSequence) is not null) NestedSequences.EnsureMediaItem(project, project.ActiveSequence);
        project.ActiveSequence = sequence;
        ReloadSequence();
        _ = RefreshNestedAsync();
    }

    /// <summary>Double-click a sequence in the bin.</summary>
    public void OpenSequenceFor(MediaItem item)
    {
        if (project.SequenceOf(item) is Sequence sequence) OpenSequence(sequence);
    }

    /// <summary>Timeline → New sequence: an empty sequence with the same size and frame rate.</summary>
    public void NewSequence(string name)
    {
        // Every sequence gets a bin item, so it can be dragged into another one.
        foreach (Sequence s in project.Sequences) NestedSequences.EnsureMediaItem(project, s);
        var sequence = new Sequence
        {
            Name = name,
            Settings = new ProjectSettings { Width = project.Settings.Width, Height = project.Settings.Height, FrameRate = project.Settings.FrameRate },
        };
        project.Sequences.Add(sequence);
        NestedSequences.EnsureMediaItem(project, sequence);
        IsDirty = true;
        OpenSequence(sequence);
    }

    public void RenameSequence(string name)
    {
        project.ActiveSequence.Name = name;
        if (project.MediaFor(project.ActiveSequence) is MediaItem item) item.DisplayName = name;
        IsDirty = true;
    }

    /// <summary>Timeline → Delete sequence. Returns why it can't be deleted, or null when it was.</summary>
    public string? DeleteSequence()
    {
        Sequence sequence = project.ActiveSequence;
        if (project.Sequences.Count == 1) return "A project needs at least one sequence.";
        if (NestedSequences.IsUsed(project, sequence))
            return $"“{sequence.Name}” is used inside another sequence. Remove it from there first.";

        if (IsPlaying) PlayPause();
        int index = project.Sequences.IndexOf(sequence);
        project.ActiveSequence = project.Sequences[index == 0 ? 1 : index - 1];
        project.Sequences.Remove(sequence);
        if (project.MediaFor(sequence) is MediaItem item) project.Media.Remove(item);
        IsDirty = true;
        ReloadSequence();
        _ = RefreshNestedAsync();
        return null;
    }

    /// <summary>Clip → Nest: the selected clips become one clip that holds a new sequence.</summary>
    public bool NestSelection(string name)
    {
        if (Timeline.NestSelection(name) is null) return false;
        _ = RefreshNestedAsync();
        return true;
    }

    private bool refreshingNested, refreshNestedAgain;

    /// <summary>Renders nested sequences that changed (in the background), then shows the new renders.</summary>
    private async Task RefreshNestedAsync()
    {
        if (refreshingNested) { refreshNestedAgain = true; return; }
        refreshingNested = true;
        Project working = project;
        try
        {
            do
            {
                refreshNestedAgain = false;
                var status = new Progress<string>(text => BackgroundStatus = text);
                int rendered = await NestedSequences.EnsureRenderedAsync(working, working.ActiveSequence, status);
                if (working != project) return;
                if (rendered > 0)
                    foreach (MediaItem item in project.Media.Where(m => m.IsSequence && !m.IsOffline)) Bin.RefreshThumbnail(item);
                RefreshPreviewSources();
            }
            while (refreshNestedAgain);
            BackgroundStatus = "";
        }
        catch (Exception ex) when (ex is ExportFailedException or IOException or InvalidOperationException)
        {
            BackgroundStatus = "A nested sequence couldn't be rendered: " + ex.Message;
        }
        finally { refreshingNested = false; }
    }

    // ===================== Export =====================

    /// <summary>The window listens to this and opens the export dialog.</summary>
    public event Action<ExportViewModel>? ExportRequested;

    private bool CanExport() => Timeline.DurationFrames > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (!FFmpegLocator.Current.IsAvailable)
        {
            MessageBox.Show("Snit needs FFmpeg to export.\n\nInstall it with:\n" + FFmpegLocator.InstallCommand,
                "FFmpeg missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsPlaying) PlayPause();
        ExportRequested?.Invoke(new ExportViewModel(project, CapturePreview?.Invoke()));
    }

    /// <summary>Exports with standard settings (1080p MP4, 30 fps project rate, 16 Mbps). Used by "--export".</summary>
    public async Task<bool> ExportWithoutDialogAsync(string outputPath)
    {
        try
        {
            var settings = new ExportSettings
            {
                OutputPath = outputPath,
                Width = project.Settings.Width,
                Height = project.Settings.Height,
                FrameRate = project.Fps,
            };
            await NestedSequences.EnsureRenderedAsync(project, project.ActiveSequence);
            await new Exporter().RunAsync(ExportCommandBuilder.Build(project, settings), null, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Export failed: " + ex.Message);
            return false;
        }
    }

    // ===================== FFmpeg check =====================

    [ObservableProperty]
    private bool isFfmpegBannerVisible;

    public string FfmpegInstallCommand => FFmpegLocator.InstallCommand;

    [RelayCommand]
    private async Task CheckFfmpegAsync()
    {
        FFmpegStatus status = await FFmpegLocator.CheckAsync();
        IsFfmpegBannerVisible = !status.IsAvailable;
        if (status.IsAvailable)
        {
            // FFmpeg might have been installed just now: refresh thumbnails and preview.
            Bin.Load(project);
            playback.UpdateTimeline(project);
            playback.Seek(Timeline.PlayheadFrame);
        }
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

    // ===================== Help / app =====================

    [RelayCommand]
    private void ShowAbout()
    {
        string ffmpeg = FFmpegLocator.Current.Version ?? "FFmpeg not found";
        MessageBox.Show(
            $"Snit — a small video editor.\n(\"snit\" is Dutch for \"cut\".)\n\n{ffmpeg}",
            "About Snit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>File → Exit. Closing the window asks about unsaved changes.</summary>
    [RelayCommand]
    private void Exit() => Application.Current.MainWindow?.Close();

    public void Dispose()
    {
        playback.Dispose();
        Source.Dispose();
    }
}
