using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Maps an <see cref="ArrangementIssueSeverity" /> to its localized group
///     label for the FEHLER inspector's severity headers. Resolves through
///     <see cref="LocalizationService" /> at convert time; the issue list
///     re-materializes on every refresh so a language switch re-labels.
/// </summary>
public sealed class SeverityToLabelConverter : IValueConverter
{
    public static readonly SeverityToLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is ArrangementIssueSeverity.Important
            ? "Inspector_Issues_Important"
            : "Inspector_Issues_Warning";
        return LocalizationService.Instance[key];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
