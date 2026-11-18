using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Snit.ViewModels;

namespace Snit.Views;

/// <summary>
/// Code behind the main window. Only window things live here (title bar buttons,
/// maximising, keyboard shortcuts, asking to save on close); everything else is in
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.ExportRequested += exportViewModel => new ExportWindow(exportViewModel) { Owner = this }.ShowDialog();
        viewModel.CapturePreview = () => Monitor.Capture();
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMaximizedLook();
        Closed += (_, _) => viewModel.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.CheckFfmpegCommand.ExecuteAsync(null);

        string[] args = Environment.GetCommandLineArgs();

        // Double-clicking a .snit file starts Snit with the file name.
        string? projectFile = args.Skip(1).FirstOrDefault(a => a.EndsWith(".snit", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (projectFile is not null) viewModel.OpenProjectFile(projectFile);

        // Developer helper: "Snit.exe --screenshot docs/screenshot.png" saves a picture
        // of the window and quits. Used to keep the README screenshot up to date.
        int i = Array.IndexOf(args, "--screenshot");
        if (i >= 0 && i + 1 < args.Length)
        {
            await Task.Delay(1500); // give thumbnails and the preview a moment
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ScreenshotHelper.SaveWindow(this, args[i + 1]);
            viewModel.IsDirty = false;
            Close();
            return;
        }

        // Helper: "Snit.exe project.snit --export video.mp4" exports the project as 1080p MP4
        // (the dialog's standard settings) and quits. Handy for checking and for scripts.
        int x = Array.IndexOf(args, "--export");
        if (x >= 0 && x + 1 < args.Length)
        {
            Environment.ExitCode = await viewModel.ExportWithoutDialogAsync(Path.GetFullPath(args[x + 1])) ? 0 : 1;
            viewModel.IsDirty = false;
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.ConfirmDiscardChanges()) e.Cancel = true;
    }

    /// <summary>
    /// Keyboard shortcuts. Handled here (and not as menu key bindings) so that typing
    /// in the search box still works: shortcuts are skipped while a text box has focus.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        TimelineViewModel tl = viewModel.Timeline;

        // Single keys without Ctrl: tools and the shuttle (J = back, K = stop, L = forward, faster each press).
        Action? action = (e.Key, ctrl, shift) switch
        {
            (Key.V, false, false) => () => tl.ActiveTool = "Select",
            (Key.C, false, false) => () => tl.ActiveTool = "Razor",
            (Key.B, false, false) => () => tl.ActiveTool = "Ripple",
            (Key.T, false, false) => () => tl.ActiveTool = "Text",
            (Key.H, false, false) => () => tl.ActiveTool = "Hand",
            (Key.Z, false, false) => () => tl.ActiveTool = "Zoom",
            (Key.S, false, false) => () => tl.SnappingEnabled = !tl.SnappingEnabled,
            (Key.J, false, false) => viewModel.ShuttleBackward,
            (Key.K, false, false) => viewModel.ShuttleStop,
            (Key.L, false, false) => viewModel.ShuttleForward,
            (Key.Up, false, false) => tl.GoToPreviousEdit,
            (Key.Down, false, false) => tl.GoToNextEdit,
            (Key.I, false, false) => tl.SetInPoint,
            (Key.O, false, false) => tl.SetOutPoint,
            (Key.X, true, true) => tl.ClearInOut,
            (Key.M, false, true) => tl.GoToNextMarker,
            (Key.M, true, true) => tl.GoToPreviousMarker,
            _ => null,
        };
        if (action is not null)
        {
            action();
            e.Handled = true;
            return;
        }

        ICommand? command = (e.Key, ctrl, shift) switch
        {
            (Key.Space, false, _) => viewModel.PlayPauseCommand,
            (Key.Left, false, _) => viewModel.StepBackCommand,
            (Key.Right, false, _) => viewModel.StepForwardCommand,
            (Key.Home, false, _) => viewModel.GoToStartCommand,
            (Key.End, false, _) => viewModel.GoToEndCommand,
            (Key.Delete, false, false) => tl.DeleteSelectionCommand,
            (Key.Back, false, false) => tl.DeleteSelectionCommand,
            (Key.Delete, false, true) => tl.RippleDeleteSelectionCommand,
            (Key.Z, true, false) => tl.UndoCommand,
            (Key.Z, true, true) => tl.RedoCommand,
            (Key.Y, true, false) => tl.RedoCommand,
            (Key.K, true, false) => tl.SplitAtPlayheadCommand,
            (Key.C, true, false) => tl.CopyCommand,
            (Key.X, true, false) => tl.CutCommand,
            (Key.V, true, false) => tl.PasteCommand,
            (Key.D, true, false) => tl.AddDissolveAtPlayheadCommand,
            (Key.N, true, false) => viewModel.NewProjectCommand,
            (Key.O, true, false) => viewModel.OpenProjectCommand,
            (Key.S, true, false) => viewModel.SaveCommand,
            (Key.S, true, true) => viewModel.SaveAsCommand,
            (Key.I, true, false) => viewModel.Bin.ImportCommand,
            (Key.M, true, false) => viewModel.ExportCommand,
            (Key.M, false, false) => tl.AddMarkerCommand,
            (Key.OemPlus or Key.Add, false, _) => tl.ZoomInCommand,
            (Key.OemMinus or Key.Subtract, false, _) => tl.ZoomOutCommand,
            _ => null,
        };
        if (command is null) return;

        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    // Menu items that call the same actions as their keys.
    private void PreviousEdit_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.GoToPreviousEdit();
    private void NextEdit_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.GoToNextEdit();
    private void MarkIn_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.SetInPoint();
    private void NextMarker_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.GoToNextMarker();
    private void PreviousMarker_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.GoToPreviousMarker();
    private void MarkOut_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.SetOutPoint();
    private void ClearInOut_Click(object sender, RoutedEventArgs e) => viewModel.Timeline.ClearInOut();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A window without the standard frame hangs a few pixels off the screen when maximised,
    /// so we add a small margin then, and swap the maximise icon for "restore".
    /// </summary>
    private void UpdateMaximizedLook()
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximise";
    }
}
