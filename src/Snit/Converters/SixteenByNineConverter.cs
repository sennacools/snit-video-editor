using System.Globalization;
using System.Windows.Data;

namespace Snit.Converters;

/// <summary>Turns a width into the height of a 16:9 box (used for thumbnails in the bin).</summary>
public sealed class SixteenByNineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width && width > 0 ? width * 9 / 16 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
