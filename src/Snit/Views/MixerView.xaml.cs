using System.Windows.Controls;
using System.Windows.Input;
using Snit.Core;

namespace Snit.Views;

public partial class MixerView : UserControl
{
    public MixerView() => InitializeComponent();

    private void Pan_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: Track track }) track.Pan = 0;
    }

    private void Volume_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: Track track }) track.VolumeDb = 0;
    }
}
