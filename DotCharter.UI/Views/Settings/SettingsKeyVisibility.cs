using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     A settings row or group is visible unless the screen's search put its
///     key in <c>HiddenKeys</c>. The key rides as the converter parameter, so a
///     screen with thirty rows exposes ONE set instead of thirty flags, and a
///     row that forgets its key is simply always shown — never hidden by
///     accident.
/// </summary>
public sealed class SettingsKeyVisibility : IValueConverter
{
    public static readonly SettingsKeyVisibility Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not IReadOnlySet<string> hidden || parameter is not string key || !hidden.Contains(key);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Row visibility is read-only.");
}
