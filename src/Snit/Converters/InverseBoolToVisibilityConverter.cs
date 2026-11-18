using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Snit.Converters;

/// <summary>true → hidden, false → visible (for "show this when there is nothing yet").</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
