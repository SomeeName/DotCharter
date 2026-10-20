using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotCharter.UI.Converters;

/// <summary>
///     Two-way between a contract's hex string (<c>#RRGGBB</c>, or <c>#AARRGGBB</c>
///     when not opaque) and the <see cref="Color" /> a <c>ColorPicker</c> binds,
///     so a Presentation contract never carries an Avalonia type. An unparsable
///     string converts to nothing rather than to black.
/// </summary>
public sealed class HexColorConverter : IValueConverter
{
    public static readonly HexColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color) ? color : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color color ? ToHex(color) : BindingOperations.DoNothing;

    public static string ToHex(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
