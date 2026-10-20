using System.Globalization;
using Avalonia.Data.Converters;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.UI.Converters;

/// <summary>
///     <c>IsBypassed</c> → the opacity a bypassed unit's card drops to.
///
///     <para>
///         The value is <see cref="FireUnitState.BypassedOpacity" /> and it has
///         to come from there. The four custom-drawn Fire controls already
///         multiply everything they paint by that constant, so a style setter
///         with a literal <c>0.55</c> on the card would be a SECOND copy of one
///         number — and the failure mode is a card at one opacity with its own
///         knobs at another, which reads as a rendering bug rather than as a
///         drifted constant.
///     </para>
/// </summary>
public sealed class FireBypassOpacityConverter : IValueConverter
{
    public static readonly FireBypassOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FireUnitState.BypassedOpacity : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Opacity is a rendering of bypass, never a source of it.");
}
