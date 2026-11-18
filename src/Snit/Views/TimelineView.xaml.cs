using System.Windows;
using System.Windows.Controls;

namespace Snit.Views;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();

    /// <summary>Right-click on a marker in the ruler: rename or delete it.</summary>
    private void Ruler_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel main || sender is not FrameworkElement ruler) return;
        var tl = main.Timeline;
        if (tl.MarkerNear(e.GetPosition(ruler).X) is not Core.Marker marker) return;

        var rename = new MenuItem { Header = "Rename marker…" };
        rename.Click += (_, _) =>
        {
            if (TextPromptWindow.Ask(Window.GetWindow(this)!, "Marker name", marker.Name) is string name)
                tl.RenameMarker(marker, name);
        };
        var delete = new MenuItem { Header = "Delete marker" };
        delete.Click += (_, _) => tl.DeleteMarker(marker);

        var menu = new ContextMenu { Items = { rename, delete }, PlacementTarget = ruler };
        menu.IsOpen = true;
        e.Handled = true;
    }
}
