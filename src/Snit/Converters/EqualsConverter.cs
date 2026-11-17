using System.Globalization;
using System.Windows.Data;

namespace Snit.Converters;

/// <summary>
/// Lets a group of RadioButtons share one text property.
/// Example: IsChecked="{Binding ActiveTool, Converter={StaticResource EqualsConverter}, ConverterParameter=Razor}"
/// is true when ActiveTool is "Razor", and clicking the button sets ActiveTool to "Razor".
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
