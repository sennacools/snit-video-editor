using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Snit.Core;

namespace Snit.Views;

public partial class SequenceSettingsWindow : Window
{
    private const string Custom = "Custom";
    private bool updating;

    private SequenceSettingsWindow(int width, int height, double fps)
    {
        InitializeComponent();
        Presets.ItemsSource = SequenceSettings.Presets.Cast<object>().Append(Custom).ToList();
        Rates.ItemsSource = SequenceSettings.FrameRates.Select(r => r.ToString("0.###", CultureInfo.InvariantCulture) + " fps").ToList();
        int rate = SequenceSettings.FrameRates.ToList().FindIndex(r => Math.Abs(r - fps) < 0.001);
        Rates.SelectedIndex = rate >= 0 ? rate : SequenceSettings.FrameRates.ToList().IndexOf(30);
        WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
        MatchPreset();
    }

    /// <summary>Picks the preset that matches the typed size (or "Custom").</summary>
    private void MatchPreset()
    {
        if (Presets is null || WidthBox is null || HeightBox is null) return;
        updating = true;
        int.TryParse(WidthBox.Text, out int w);
        int.TryParse(HeightBox.Text, out int h);
        object match = SequenceSettings.Presets.FirstOrDefault(p => p.Width == w && p.Height == h) as object ?? Custom;
        Presets.SelectedItem = match;
        updating = false;
    }

    private void Presets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Presets.SelectedItem is not FramePreset preset) return;
        updating = true;
        WidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
        updating = false;
    }

    private void Size_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updating) MatchPreset();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h) || w < 16 || h < 16 || w > 8192 || h > 8192)
        {
            Note.Text = "Type a width and height between 16 and 8192 pixels.";
            return;
        }
        DialogResult = true;
    }

    /// <summary>Asks for a size and frame rate. Returns null when you click Cancel.</summary>
    public static (int Width, int Height, double Fps)? Ask(Window owner, int width, int height, double fps)
    {
        var window = new SequenceSettingsWindow(width, height, fps) { Owner = owner };
        if (window.ShowDialog() != true) return null;
        return (SequenceSettings.CleanSize(int.Parse(window.WidthBox.Text)), SequenceSettings.CleanSize(int.Parse(window.HeightBox.Text)),
                SequenceSettings.FrameRates[Math.Max(0, window.Rates.SelectedIndex)]);
    }
}
