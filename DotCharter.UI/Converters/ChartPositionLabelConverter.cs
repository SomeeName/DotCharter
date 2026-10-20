using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     (<c>timeMs</c>, <c>beats</c>) → the location readout
///     <c>M19.3 · 02:13.16</c>, via the pure
///     <see cref="ChartPositionLabel" />.
///     <para>
///         It is a MULTI-value converter because a chart time only becomes a BAR
///         against the project's beat map, and an <c>ArrangementIssue</c> is a
///         Core record that carries a time and nothing else. Binding the beat map
///         in beside it is what lets the FEHLER rows print a real position without
///         a per-row view model — and without inventing one when the chart has no
///         measure markers, where <see cref="ChartPositionLabel" /> falls back to
///         the time alone.
///     </para>
/// </summary>
public sealed class ChartPositionLabelConverter : IMultiValueConverter
{
    /// <summary>The shared instance markup binds to.</summary>
    public static readonly ChartPositionLabelConverter Instance = new();

    /// <inheritdoc />
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var timeMs = values.Count > 0 ? values[0] as double? : null;
        var beats = values.Count > 1 ? values[1] as IReadOnlyList<BeatMarker> : null;
        return ChartPositionLabel.Format(beats, timeMs);
    }
}
