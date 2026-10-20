using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace DotCharter.Tone.UI.Converters;

/// <summary>
///     Resolves a resource KEY a view model names (e.g. <c>"IconRigCatAmps"</c>)
///     to the resource itself — the icon twin of <see cref="FireTokenBrushConverter" />.
///     The view model says WHICH glyph; <c>Styles/Icons.axaml</c> stays the one
///     place that says what it looks like. An unknown key returns <c>null</c>,
///     which draws nothing.
/// </summary>
public sealed class FireResourceConverter : IValueConverter
{
    public static readonly FireResourceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Application.Current is not { } app) return null;
        return app.TryFindResource(key, app.ActualThemeVariant, out var resource) ? resource : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A resource is one-way — it is never edited back into a key.");
}
