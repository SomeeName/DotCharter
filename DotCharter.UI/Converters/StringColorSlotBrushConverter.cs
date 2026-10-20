using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Converters;

/// <summary>
///     A string's colour SLOT (<see cref="DotCharter.Core.Utilities.StringColorSlots.For" />, which the
///     row computes from its string index and the arrangement's string count) → the brush the highway,
///     the lane gutter and the chord box draw that string in (<see cref="ChartTheme.StringBrushes" />).
///     Bound to the slot, never the raw string index: on a 7-string the index 0 is the low B, grey, and
/// the low E is index 1, red. The brush is the theme's own instance, so a palette switch
///     repaints the dot with everything else. The Sound tab's string chips and the analysis monitor's
/// confidence strip wear it.
/// </summary>
public sealed class StringColorSlotBrushConverter : IValueConverter
{
    public static readonly StringColorSlotBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brushes = ChartTheme.StringBrushes;
        return value is int slot && slot >= 0 && brushes.Count > 0 ? brushes[slot % brushes.Count] : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
