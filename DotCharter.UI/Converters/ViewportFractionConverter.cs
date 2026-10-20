using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     A FRACTION of a live container rectangle, for the ceiling of a
///     <c>Viewbox</c> that carries a figure screen: a fixed ceiling (<c>ratio</c> 0.25)
///     would draw the same postage stamp on a 1080p window and on a 4K one,
///     i.e. pixel-for-pixel identical at every resolution.
///     <para>
///         Bind a <c>Viewbox</c>'s <c>MaxWidth</c>/<c>MaxHeight</c> to the
///         hosting panel's <see cref="Visual.Bounds" /> through this, and the
///         figure's growth ceiling becomes a share of the WINDOW rather than a
///         literal. With <c>Stretch="Uniform"</c> and
///         <c>StretchDirection="UpOnly"</c> that is what makes a screen's
///         coverage the same fraction at 1920x1080 and at 3840x2160 —
///         <c>ratio</c> 1.0 instead of 0.25 — while never shrinking the design
///         below 1:1 on a small window.
///     </para>
///     <para>
///         <b>This is deliberately NOT
///         <see cref="DialogCardClampConverter" />.</b> That one answers
///         "available minus a gutter, optionally capped", which is the right
///         answer for a card that should fill the window; a figure that FILLS
///         the window is a worse screen, not a better one, so the ceiling here
///         is proportional and the fraction is the design decision.
///     </para>
/// </summary>
/// <remarks>
///     ConverterParameter: <c>"Width:0.42"</c> or <c>"Height:0.78"</c> — the
///     axis of the container rectangle to read, and the share of it the figure
///     may grow to.
/// </remarks>
public sealed class ViewportFractionConverter : IValueConverter
{
    /// <summary>The shared stateless instance; bind with <c>{x:Static}</c>.</summary>
    public static readonly ViewportFractionConverter Instance = new();

    /// <summary>
    ///     Converts a container <see cref="Rect" /> to the ceiling described by
    ///     <paramref name="parameter" />.
    /// </summary>
    /// <param name="value">The container's live <see cref="Rect" /> bounds.</param>
    /// <param name="targetType">Ignored; the result is always a <see cref="double" />.</param>
    /// <param name="parameter">
    ///     <c>"Width:&lt;fraction&gt;"</c> or <c>"Height:&lt;fraction&gt;"</c>.
    /// </param>
    /// <param name="culture">Ignored; the fraction is parsed invariantly.</param>
    /// <returns>
    ///     The ceiling in device-independent pixels, or
    ///     <see cref="double.PositiveInfinity" /> when there is nothing to take a
    ///     share of yet — a zero-sized container (first measure, a detached
    ///     prewarm) must not pin the figure at 0.
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Rect bounds || parameter is not string spec)
            return double.PositiveInfinity;

        var parts = spec.Split(':');
        var available = parts[0] switch
        {
            "Height" => bounds.Height,
            _ => bounds.Width
        };

        if (parts.Length < 2
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
            || !double.IsFinite(fraction) || fraction <= 0)
        {
            return double.PositiveInfinity;
        }

        var ceiling = available * fraction;
        return ceiling > 0 ? ceiling : double.PositiveInfinity;
    }

    /// <summary>Not supported — a ceiling cannot be turned back into a window.</summary>
    /// <param name="value">Unused.</param>
    /// <param name="targetType">Unused.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
