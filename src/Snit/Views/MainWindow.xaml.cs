using System.Windows;
using Snit.ViewModels;

namespace Snit.Views;

/// <summary>
/// Code behind the main window. Only window things live here (title bar buttons,
/// maximising); everything else is in <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMaximizedLook();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.CheckFfmpegCommand.ExecuteAsync(null);

        // Developer helper: "Snit.exe --screenshot docs/screenshot.png" saves a picture
        // of the window and quits. Used to keep the README screenshot up to date.
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--screenshot");
        if (i >= 0 && i + 1 < args.Length)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ScreenshotHelper.SaveWindow(this, args[i + 1]);
            Close();
        }
    }

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
