using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Clamps a <c>DialogCard</c> to its scrim: bound to the scrim's live
///     <see cref="Rect" /> bounds, it yields a <c>MaxWidth</c>/<c>MaxHeight</c>
///     that keeps the card (and its shadow's breathing room) inside the window.
///     A <c>Width</c> larger than <c>MaxWidth</c> loses — that is Avalonia's
///     min/max clamp — so the ladder's <c>Xl</c> card shrinks on a laptop
///     instead of centring itself off both screen edges.
/// </summary>
/// <remarks>
///     ConverterParameter: <c>"Width"</c> → scrim width minus the gutter;
///     <c>"Height:820"</c> → the smaller of the cap and scrim height minus the
///     gutter (the cap is the old absolute MaxHeight, kept so a tall dialog
///     does not become a tower on a 1440p window).
/// </remarks>
public sealed class DialogCardClampConverter : IValueConverter
{
    public static readonly DialogCardClampConverter Instance = new();

    /// <summary>Total horizontal/vertical breathing room around a clamped card.</summary>
    private const double Gutter = 64;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Rect bounds || parameter is not string spec)
            return double.PositiveInfinity;

        var parts = spec.Split(':');
        var available = parts[0] switch
        {
            "Height" => bounds.Height,
            _ => bounds.Width
        };
        var clamped = Math.Max(0, available - Gutter);

        if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cap))
            clamped = Math.Min(cap, clamped);

        // A zero-sized scrim (first measure, detached prewarm) must not pin the
        // card at 0 — unclamped is the safe default there.
        return clamped > 0 ? clamped : double.PositiveInfinity;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
