using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Snit.ViewModels;

namespace Snit.Views;

public partial class ExportWindow : Window
{
    private readonly ExportViewModel viewModel;

    public ExportWindow(ExportViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += Close;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.ConfirmClose()) e.Cancel = true;
    }

    // The dialog has no Windows title bar, so you drag it by its own title.
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
