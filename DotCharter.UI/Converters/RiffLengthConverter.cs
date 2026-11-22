using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     A riff row's length stamp, "4 beats" / "1 beat": (<c>PhraseCatalogEntry.LengthInBeats</c>, the Riff library's
///     <see cref="ILocalizationService" />, its <c>Culture</c>). A MULTI-value converter so the row needs no view
///     model of its own: the third value only re-runs the stamp when the UI language changes. The number follows
///     the Windows region (<see cref="CultureInfo.CurrentCulture" />), the words the UI language.
/// </summary>
public sealed class RiffLengthConverter : IMultiValueConverter
{
    public static readonly RiffLengthConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 1 && values[0] is double beats && values[1] is ILocalizationService localization
            ? Format(beats, localization, CultureInfo.CurrentCulture)
            : null;

    /// <summary>The stamp's words; exactly one beat takes the singular.</summary>
    internal static string Format(double beats, ILocalizationService localization, IFormatProvider number) =>
        localization.PluralWith("PhraseCatalog_RowLengthOne", "PhraseCatalog_RowLengthFmt", beats == 1.0 ? 1 : 2,
            beats.ToString("0.##", number));
}
