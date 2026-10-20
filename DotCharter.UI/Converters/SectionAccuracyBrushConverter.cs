using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.UI.Converters;

/// <summary>
///     A section's accuracy (0..1) → the brush its bar is drawn in, in the three
///     bands <c>Results.dc.html</c> uses: ≥ 90 % strong, 75–89 % fair, below
///     that weak.
///
///     <para>
///         A converter rather than three stacked bars or three style classes.
///         Stacked bars mean three controls per row where two are always
///         invisible; style classes would have to be declared in the one view's
///         own <c>Styles</c> and would not travel with the row template. The
///         bands themselves live in <see cref="ViewModels.PlayMode.SectionRow" />
///         so the bar and the row's own <c>IsStrong</c>/<c>IsFair</c>/
///         <c>IsWeak</c> flags cannot disagree — this only picks the paint.
///     </para>
///
///     <para>
///         Resolves through <c>DynamicResource</c>, so it follows a theme change
///         and cannot hard-code a colour past
///         <c>ViewHardCodedColourTests</c>.
///     </para>
/// </summary>
public sealed class SectionAccuracyBrushConverter : IValueConverter
{
    public static readonly SectionAccuracyBrushConverter Instance = new();

    /// <summary>The band thresholds, shared with <c>SectionRow</c>'s flags.</summary>
    public const double StrongThreshold = 0.90;

    /// <inheritdoc cref="StrongThreshold" />
    public const double FairThreshold = 0.75;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var accuracy = value switch
        {
            double d when !double.IsNaN(d) => d,
            float f when !float.IsNaN(f) => f,
            _ => 0.0
        };

        return Resolve(ResourceKeyFor(accuracy));
    }

    // Resolved ONCE per key per theme — this converter runs per visible row per
    // accuracy update in the Live Play section list; allocating a
    // DynamicResourceExtension and walking the application resources on every
    // evaluation would be wasted work. There are exactly three
    // keys. The cache is dropped on a theme-variant change, and it is keyed to
    // the Application instance so a test
    // session that rebuilds the app never sees a stale brush.
    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.Ordinal);
    private static Application? _hooked;

    private static IBrush Resolve(string key)
    {
        var app = Application.Current;
        if (app == null) return Brushes.Transparent;

        if (!ReferenceEquals(_hooked, app))
        {
            _hooked = app;
            Cache.Clear();
            // A variant change can swap the dictionary the key resolves in; a
            // runtime PALETTE switch does not need a hook — the three brushes are
            // `SolidColorBrush Color="{DynamicResource …Value}"` in ThemeSurfaces,
            // so the cached instance follows the palette by itself (and a static
            // ChartTheme subscription would root the cache for the process, which
            // ViewSubscriptionLeakGuardTests refuses).
            app.ActualThemeVariantChanged += (_, _) => Cache.Clear();
        }

        if (Cache.TryGetValue(key, out var cached)) return cached;
        var brush = app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        Cache[key] = brush;
        return brush;
    }

    /// <summary>Pure seam: which token an accuracy lands on. Tested directly —
    /// the brush lookup needs a running app, the BANDING does not, and the
    /// banding is the part that can be wrong.</summary>
    internal static string ResourceKeyFor(double accuracy) =>
        accuracy >= StrongThreshold ? "SuccessColor"
        : accuracy >= FairThreshold ? "WarningColor"
        : "DangerColor";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("SectionAccuracyBrushConverter is one-way.");
}
