using System.Text.RegularExpressions;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Asserts on a UI sentence in whatever culture the run is in: the expected text is the resx
///     format itself, its placeholders matched (or pinned to a known value), never an English
///     literal in the test.
/// </summary>
internal static partial class LocalizedText
{
    /// <summary>The resx text of <paramref name="key" /> in the run's culture.</summary>
    public static string Of(string key) => LocalizationService.Instance[key];

    /// <summary>
    ///     True when <paramref name="text" /> is <paramref name="key" />'s format with every
    ///     placeholder filled: a placeholder named in <paramref name="known" /> must be that value,
    ///     any other matches any text.
    /// </summary>
    public static bool Matches(string key, string text, params (int Index, string Value)[] known)
    {
        var format = Of(key);
        var pattern = new System.Text.StringBuilder("^");
        var at = 0;
        foreach (var hole in Placeholder().Matches(format).Cast<Match>())
        {
            pattern.Append(Regex.Escape(format[at..hole.Index]));
            var index = int.Parse(hole.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var value = known.FirstOrDefault(k => k.Index == index).Value;
            pattern.Append(value is null ? "(?s:.+?)" : Regex.Escape(value));
            at = hole.Index + hole.Length;
        }

        pattern.Append(Regex.Escape(format[at..]));
        return Regex.IsMatch(text, pattern.Append('$').ToString(), RegexOptions.Singleline);
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
