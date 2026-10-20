using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     The <see cref="IntEqualsConverter" /> shape for a bound <see cref="string" />:
///     maps one segmented option control to a string property by comparing
///     against the <c>ConverterParameter</c>.
///     <list type="bullet">
///         <item>Convert: <c>true</c> if <c>value == parameter</c>, ordinal.</item>
///         <item>ConvertBack on <c>true</c>: returns <c>parameter</c>.</item>
///         <item>ConvertBack on <c>false</c>: <see cref="BindingOperations.DoNothing" />,
///         so the other segments can drive the value without this one stomping it back.</item>
///     </list>
///     <para>
///         <c>null</c> and the empty string are the SAME value here — a chord
///         template stores "no finger on this string" as an absent entry, and
///         the editor round-trips it as <c>""</c>. Without that the "—" segment
///         would not light up for an unfingered string.
///     </para>
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string ?? string.Empty, parameter as string ?? string.Empty,
            StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter as string ?? string.Empty : BindingOperations.DoNothing;
}
