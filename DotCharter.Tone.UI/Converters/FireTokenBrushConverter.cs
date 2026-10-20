using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Converters;

/// <summary>
///     Resolves a Fire TOKEN KEY (a string, e.g. <c>"FireString6E"</c>) to the
///     brush that key names in the app's resources.
///
///     <para>It exists so a per-item identity colour can reach markup without any
///     of the three things this design layer forbids: a literal hex in a view, a
///     brush stored on a view model, or a second table of colour values. The
///     view model says WHICH identity ("FireString4D"); the one dictionary still
///     says what that identity looks like, so a token correction moves every
///     consumer at once.</para>
///
///     <para>An unknown or unresolvable key returns <c>null</c>, which paints
///     nothing — visibly wrong rather than plausibly right, the same call
///     <c>FireUnitState</c> makes for a missing gear identity.</para>
/// </summary>
public sealed class FireTokenBrushConverter : IValueConverter
{
    public static readonly FireTokenBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Application.Current is not { } app) return null;
        // The Fire tokens live in STYLE resources (`FireTokens.axaml` is a
        // `<Styles>`), which `Application.Resources` alone does not reach —
        // `TryFindResource` walks the style scopes too. Getting that wrong
        // returns null for every key and paints six invisible chips.
        return app.TryFindResource(key, app.ActualThemeVariant, out var resource)
            ? resource as IBrush
            : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A Fire token brush is one-way — a colour is never edited back into a key.");
}
