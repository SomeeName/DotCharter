using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     True when a bound <see cref="double" /> equals the
///     <c>ConverterParameter</c>, within a tolerance.
/// </summary>
/// <remarks>
///     <para>
///         For the Tempo popover's SPEED PRESETS, where the mockup draws the
///         CURRENT preset lit. The presets are one-shot commands rather than a
///         toggle group, so the lit state has to be derived from the live speed
///         instead of held — otherwise the chips and
///         <c>TransportViewModel.PlaybackSpeed</c> are two sources of truth and
///         dragging the slider leaves "100%" still lit at 0.62x.
///     </para>
///     <para>
///         The parameter is parsed INVARIANTLY: <c>CommandParameter="0.65"</c>
///         is 0.65 everywhere, and on a German locale
///         <c>double.Parse("0.65")</c> with the current culture is 65.
///     </para>
///     <para>
///         One-way on purpose. The chips run a command; there is nothing to
///         write back, and a <c>ConvertBack</c> would make an unlit chip fight
///         the lit one.
///     </para>
/// </remarks>
public sealed class DoubleEqualsConverter : IValueConverter
{
    /// <summary>Half a percent of playback speed — below what a chip could
    /// meaningfully claim and well above binary round-trip error.</summary>
    private const double Tolerance = 0.005;

    public static readonly DoubleEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double v) return false;
        if (parameter is not string s) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
               && Math.Abs(v - p) < Tolerance;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
