using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotCharter.UI.Converters;

/// <summary>
///     A contract's hex colour string (<c>#RRGGBB</c> / <c>#AARRGGBB</c>) as a fill,
///     for a swatch that paints chart DATA rather than a theme token. One way;
///     the two-way partner for a <c>ColorPicker</c> is <see cref="HexColorConverter" />.
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color)
            : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
