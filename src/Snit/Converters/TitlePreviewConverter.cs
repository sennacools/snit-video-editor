using System.Globalization;
using System.Windows.Data;
using Snit.Core;
using Snit.Services;

namespace Snit.Converters;

/// <summary>A small picture of a title template (for the tiles in the Text tab).</summary>
public sealed class TitlePreviewConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TitleStyle style) return null;
        // The tile is tiny, so the letters are drawn bigger than they really are (twice as big),
        // otherwise most titles would be unreadable specks.
        TitleStyle big = style.Copy();
        big.FontSize = style.FontSize * 2;
        return TitleRenderer.Render(big, 320, 180);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
