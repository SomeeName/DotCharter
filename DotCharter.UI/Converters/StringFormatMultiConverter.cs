using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Formats a (typically localized) template string — the first binding
///     value — with the remaining binding values as arguments. Lets XAML
///     combine a <c>{localization:Loc}</c> style resource containing
///     placeholders with runtime values, e.g.
///     ["Send selection to {0}", "Rhythm"] → "Send selection to Rhythm".
/// </summary>
public sealed class StringFormatMultiConverter : IMultiValueConverter
{
    public static readonly StringFormatMultiConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not string format) return null;
        if (values.Count == 1) return format;

        var args = new object?[values.Count - 1];
        for (var i = 1; i < values.Count; i++) args[i - 1] = values[i];

        try
        {
            return string.Format(culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
