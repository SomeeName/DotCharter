using System.Globalization;
using Avalonia.Data.Converters;

namespace DotCharter.UI.Converters;

/// <summary>
///     <para>
///         Formats a bound value into a LOCALIZED composite string:
///         <c>{Binding X, Converter={x:Static conv:LocalizedFormatConverter.Instance},
///         ConverterParameter=Some_Key_Fmt}</c>.
///     </para>
///     <para>
///         It exists because <c>Binding.StringFormat</c> takes a <c>string</c>,
///         not a markup extension, so <c>{localization:Loc …}</c> cannot be put
///         there and the format is written in the markup — in English. Two
///         shipped that way: the chord-bend string picker read
///         <c>StringFormat=String \{0\}</c> (German for that word is "Saite",
///         and the resx already said so ten lines above), and the stem
///         regenerate dialog read <c>StringFormat='Model: {0}'</c>. Neither is
///         visible to
///         <c>LocalizationSweepTests.Views_HaveNoHardcodedUserFacingStrings</c>,
///         which skips any attribute value starting with <c>{</c> because that
///         is a binding.
///     </para>
///     <para>
///         Same shape as <see cref="SeverityToLabelConverter" />: it resolves
///         through <see cref="LocalizationService" /> at CONVERT time, so a
///         language switch re-labels as soon as the binding re-evaluates. The
///         key is the <c>ConverterParameter</c>, which
///         <c>AllXamlLocReferences_ResolveToADefinedKey</c> reads — a key
///         spelled only here would otherwise be outside every lint.
///     </para>
/// </summary>
public sealed class LocalizedFormatConverter : IValueConverter
{
    public static readonly LocalizedFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string key || string.IsNullOrWhiteSpace(key))
            return value?.ToString();

        return LocalizationService.Instance.Format(key, value ?? string.Empty);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
