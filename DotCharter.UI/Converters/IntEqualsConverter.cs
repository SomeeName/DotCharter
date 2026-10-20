using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Two-way converter that maps an <see cref="int"/> bound property to a
///     <see cref="bool"/> on a single segmented option control (typically a
///     <see cref="Avalonia.Controls.Primitives.ToggleButton"/> or
///     <see cref="Avalonia.Controls.RadioButton"/>) by comparing against the
///     <c>ConverterParameter</c>:
///     <list type="bullet">
///         <item>Convert: <c>true</c> if <c>value == parameter</c>.</item>
///         <item>ConvertBack on <c>true</c>: returns <c>parameter</c>.</item>
///         <item>ConvertBack on <c>false</c>: returns <see cref="BindingOperations.DoNothing"/> so other buttons in
///         the same group can drive the value to their own parameter without this one stomping it back to <c>0</c>.</item>
///     </list>
///     This lets us replace <see cref="Avalonia.Controls.ComboBox"/>es with horizontal "chip strips" that show all
///     options at once — used in the compact note &amp; chord inspectors for Mute / HOPO / Harmonic / Picking.
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return false;
        var v = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var p = ParseParameter(parameter);
        return v == p;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return ParseParameter(parameter);
        return BindingOperations.DoNothing;
    }

    private static int ParseParameter(object? parameter)
    {
        return parameter switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) => pi,
            _ => 0,
        };
    }
}
