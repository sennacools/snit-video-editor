using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Snit.Controls;
using Snit.Core;
using Snit.ViewModels;

namespace Snit.Views;

/// <summary>
/// Mouse handling for the media bin: dragging a tile to the timeline,
/// and dropping files from Windows Explorer into the bin.
/// </summary>
public partial class MediaBinView : UserControl
{
    private Point? pressPoint;

    public MediaBinView() => InitializeComponent();

    private MediaBinViewModel? Bin => (DataContext as MainViewModel)?.Bin;

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => pressPoint = e.GetPosition(MediaList);

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressPoint is not Point start) return;

        Point now = e.GetPosition(MediaList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Which tile was pressed?
        var element = MediaList.InputHitTest(start) as DependencyObject;
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        pressPoint = null;
        if (element is ListBoxItem { DataContext: MediaItem item } && !item.IsOffline)
        {
            var data = new DataObject(TimelineCanvas.MediaDragFormat, item);
            DragDrop.DoDragDrop(MediaList, data, DragDropEffects.Copy);
        }
    }

    /// <summary>Double-click a tile: watch it in the Source monitor.</summary>
    private void MediaList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MediaList.SelectedItem is MediaItem item && DataContext is MainViewModel main)
            main.OpenInSourceCommand.Execute(item);
    }

    private void MediaList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Bin?.SelectedItem is MediaItem item)
        {
            Bin.RemoveCommand.Execute(item);
            e.Handled = true;
        }
    }

    // ---- The title tile in the Text tab ----

    private void TitleTile_MouseDown(object sender, MouseButtonEventArgs e) => pressPoint = e.GetPosition(TitleTile);

    private void TitleTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressPoint is not Point start) return;
        Point now = e.GetPosition(TitleTile);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        pressPoint = null;
        DragDrop.DoDragDrop(TitleTile, new DataObject(TimelineCanvas.MediaDragFormat, MediaItem.Title), DragDropEffects.Copy);
    }

    // ---- Transition tiles in the Transitions tab ----

    private void TransitionTile_MouseDown(object sender, MouseButtonEventArgs e) => pressPoint = e.GetPosition((IInputElement)sender);

    private void TransitionTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || pressPoint is not Point start || sender is not FrameworkElement tile) return;
        Point now = e.GetPosition(tile);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        pressPoint = null;
        var kind = Enum.Parse<TransitionKind>((string)tile.Tag);
        DragDrop.DoDragDrop(tile, new DataObject(TimelineCanvas.TransitionDragFormat, kind), DragDropEffects.Copy);
    }

    // ---- Files dropped from Explorer ----

    private void MediaTab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MediaTab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && Bin is { } bin)
            await bin.ImportFilesAsync(files);
    }
}
