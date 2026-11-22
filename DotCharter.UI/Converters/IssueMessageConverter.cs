using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     Shows an <see cref="ArrangementIssue" />'s sentence in the UI language. Resolves through
///     <see cref="LocalizationService" /> at convert time; the issue list re-materializes on every
///     refresh so a language switch re-renders it.
/// </summary>
public sealed class IssueMessageConverter : IValueConverter
{
    public static readonly IssueMessageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ArrangementIssue issue ? ArrangementIssueText.Of(issue, LocalizationService.Instance) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
