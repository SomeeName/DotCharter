using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     A resource KEY carried by a view model (<c>IconKey = "IconKeyboard"</c>)
///     to the app resource it names, for a data-driven list whose glyphs are
///     data. Resolves against the application's dictionaries only, so it never
///     throws where there is no application (a platform-free construction) —
///     the glyph is simply absent there.
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 && Application.Current is { } app && app.TryFindResource(key, out var resource)
            ? resource
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
