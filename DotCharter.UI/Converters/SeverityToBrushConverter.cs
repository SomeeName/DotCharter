using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DotCharter.Tone.UI.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Maps an <see cref="ArrangementIssueSeverity"/> to a brush so the FEHLER
///     inspector can colour-code issues: <c>Important</c> reads as the danger
///     signal, everything else as the warning one.
///     <para>
///         The keys are the FIRE tokens, not the app's
///         <c>DangerColor</c>/<c>WarningColor</c>. Its one consumer is
///         <c>IssuesInspectorView</c>, and that tab is on the Fire layer — a
///         danger red two shades off everything around it is exactly the
///         half-converted surface the layer's ladder rule exists to prevent.
///     </para>
///     <para>
///         Resolution goes through <c>TryFindResource</c>, NOT
///         <c>TryGetResource</c>: <c>FireTokens.axaml</c> is a
///         <c>&lt;Styles&gt;</c> file, so its keys live in STYLE resources,
///         which <c>Application.Resources</c> alone does not reach. Getting that
///         wrong returns the grey fallback for every issue in the list — the
///         same trap <see cref="FireTokenBrushConverter" /> documents.
///     </para>
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    /// <summary>The shared instance markup binds to.</summary>
    public static readonly SeverityToBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is ArrangementIssueSeverity.Important ? "FireDanger" : "FireWarning";

        if (Application.Current is { } app &&
            app.TryFindResource(key, app.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
