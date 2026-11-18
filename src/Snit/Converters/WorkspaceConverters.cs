using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Snit.Converters;

/// <summary>
/// Shows a panel only in one workspace.
/// Example: Visibility="{Binding Workspace, Converter={StaticResource WorkspaceVisibility}, ConverterParameter=Color}"
/// </summary>
public sealed class WorkspaceVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A track's balance as text: "L 40", "C" (centre) or "R 25".</summary>
public sealed class PanTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pan = value is double d ? d : 0;
        int amount = (int)Math.Round(Math.Abs(pan) * 100);
        return amount == 0 ? "C" : (pan < 0 ? "L " : "R ") + amount;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
