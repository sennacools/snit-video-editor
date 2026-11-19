using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Snit.Core;
using Snit.Services;

namespace Snit.ViewModels;

/// <summary>The media bin: the list of imported files, search, and importing.</summary>
public partial class MediaBinViewModel : ObservableObject
{
    private readonly TimelineViewModel timeline;
    private Project project = null!;

    public MediaBinViewModel(Project project, TimelineViewModel timeline)
    {
        this.timeline = timeline;
        Load(project);
    }

    /// <summary>The files shown in the bin (filtered by the search box).</summary>
    public ICollectionView Items { get; private set; } = null!;

    public bool IsEmpty => project.Media.Count == 0;

    /// <summary>"Media", "Effects", "Transitions" or "Text".</summary>
    [ObservableProperty]
    private string activeTab = "Media";

    [ObservableProperty]
    private string searchText = "";

    /// <summary>Shown while importing, e.g. "Importing 2 of 5â€¦". Empty when idle.</summary>
    [ObservableProperty]
    private string importStatus = "";

    [ObservableProperty]
    private MediaItem? selectedItem;

    partial void OnSearchTextChanged(string value) => Items.Refresh();

    // ===================== Effects and transitions tabs =====================

    /// <summary>Typed in the search box of the Effects and Transitions tabs.</summary>
    [ObservableProperty]
    private string effectSearch = "";

    partial void OnEffectSearchChanged(string value)
    {
        Effects.Refresh();
        OnPropertyChanged(nameof(TransitionGroups));
    }

    /// <summary>The transitions that match the search, by category (for the tiles in the Transitions tab).</summary>
    public IReadOnlyList<TransitionGroup> TransitionGroups
        => TransitionCatalog.Categories
            .Select(c => new TransitionGroup(c, TransitionCatalog.All.Where(t => t.Category == c && Matches(t.Name, t.Description, t.Category)).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();

    private bool Matches(params string[] texts)
        => string.IsNullOrWhiteSpace(EffectSearch)
           || texts.Any(t => t.Contains(EffectSearch.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>All effects, grouped by category (like Premiere's Video Effects folders), filtered by the search.</summary>
    public ICollectionView Effects => effects ??= MakeEffectsView();
    private ICollectionView? effects;

    private ICollectionView MakeEffectsView()
    {
        var view = new ListCollectionView(EffectCatalog.All
            .OrderBy(e => EffectCatalog.Categories.ToList().IndexOf(e.Category)).ThenBy(e => e.Name).ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EffectDefinition.Category)));
        view.Filter = o => o is EffectDefinition e && Matches(e.Name, e.Description, e.Category);
        return view;
    }


    public void Load(Project newProject)
    {
        if (project is not null) project.Media.CollectionChanged -= OnMediaChanged;
        project = newProject;
        project.Media.CollectionChanged += OnMediaChanged;

        Items = CollectionViewSource.GetDefaultView(project.Media);
        Items.Filter = o => o is MediaItem m &&
                            (string.IsNullOrWhiteSpace(SearchText) || m.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(IsEmpty));

        // Thumbnails aren't saved in the project file, so make them again; proxies are found again.
        foreach (var item in project.Media)
        {
            _ = LoadThumbnailAsync(item);
            item.ProxyPath = ProxyMaker.Existing(item);
        }
    }

    private void OnMediaChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import media",
            Filter = MediaImporter.DialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
            await ImportFilesAsync(dialog.FileNames);
    }

    /// <summary>Adds files to the bin (from the Import button or dropped from Explorer).</summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        if (!FFmpegLocator.Current.IsAvailable)
        {
            MessageBox.Show("Snit needs FFmpeg to import media.\n\nInstall it with:\n" + FFmpegLocator.InstallCommand,
                "FFmpeg missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var problems = new List<string>();
        int done = 0;
        foreach (string path in paths)
        {
            ImportStatus = $"Importing {++done} of {paths.Count}â€¦";
            if (project.Media.Any(m => string.Equals(m.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                continue; // already in the bin

            try
            {
                MediaItem item = await MediaImporter.ProbeAsync(path);
                project.Media.Add(item);
                _ = LoadThumbnailAsync(item);
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        ImportStatus = "";

        if (problems.Count > 0)
            MessageBox.Show("These files couldn't be imported:\n\n" + string.Join("\n", problems),
                "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task LoadThumbnailAsync(MediaItem item)
    {
        try
        {
            item.Thumbnail = await MediaImporter.MakeThumbnailAsync(item);
            timeline.RequestRedraw(); // the timeline shows thumbnails too
        }
        catch (Exception)
        {
            // No thumbnail is not a big deal; the bin shows a placeholder.
        }

        try
        {
            item.Waveform = await MediaImporter.MakeWaveformAsync(item);
            timeline.RequestRedraw();
        }
        catch (Exception)
        {
            // No waveform: the clip just shows without one.
        }
    }

    /// <summary>Removes a file from the bin (and its clips from the timeline, after asking).</summary>
    // ===================== Proxies =====================

    /// <summary>Raised when a proxy is ready (the preview then reads from it).</summary>
    public event Action? ProxiesChanged;

    /// <summary>Right-click â†’ Make proxy: a small copy for a smooth preview.</summary>
    [RelayCommand]
    private Task CreateProxyAsync(MediaItem? item)
    {
        item ??= SelectedItem;
        return item is null ? Task.CompletedTask : MakeProxiesAsync([item]);
    }

    /// <summary>Clip â†’ Make proxies for all videos.</summary>
    [RelayCommand]
    private Task CreateAllProxiesAsync() => MakeProxiesAsync(project.Media.ToList());

    /// <summary>Makes the missing proxies one after the other (each shows "Making proxyâ€¦" on its tile).</summary>
    public async Task MakeProxiesAsync(IReadOnlyList<MediaItem> items)
    {
        var todo = items.Where(m => ProxyMaker.CanHaveProxy(m) && !m.HasProxy && !m.IsMakingProxy).ToList();
        foreach (var item in todo) item.IsMakingProxy = true;
        var failed = new List<string>();
        foreach (var item in todo)
        {
            try
            {
                item.ProxyPath = await ProxyMaker.CreateAsync(item);
                ProxiesChanged?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                failed.Add($"{item.Name}: {ex.Message}");
            }
            finally { item.IsMakingProxy = false; }
        }
        if (failed.Count > 0)
            System.Windows.MessageBox.Show("Some proxies could not be made:\n\n" + string.Join("\n", failed), "Proxies",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void Remove(MediaItem? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        if (item.IsSequence)
        {
            MessageBox.Show("To delete a sequence, open it (double-click) and choose Timeline â†’ Delete sequence.",
                "Remove media", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Clips in every sequence count, not only the open one.
        int uses = project.Sequences.Sum(s => s.AllClips.Count(c => c.Media == item));
        if (uses > 0)
        {
            var answer = MessageBox.Show(
                $"\"{item.Name}\" is used {uses} time(s) in your sequences. Remove those clips too?",
                "Remove media", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            timeline.RemoveClipsUsing(item);
            foreach (Sequence other in project.Sequences.Where(s => s != project.ActiveSequence))
                foreach (Track track in other.Tracks) track.Clips.RemoveAll(c => c.Media == item);
        }
        project.Media.Remove(item);
        timeline.NotifyContentChanged();
    }

    /// <summary>Makes the thumbnail again (after a nested sequence was rendered).</summary>
    public void RefreshThumbnail(MediaItem item) => _ = LoadThumbnailAsync(item);
}

/// <summary>One category of transitions in the Transitions tab.</summary>
public sealed record TransitionGroup(string Name, IReadOnlyList<TransitionDefinition> Items);
