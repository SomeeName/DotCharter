using System.Globalization;
using System.Text;

namespace DotCharter.Services.Online.MusicBrainz;

/// <summary>
///     An official Latin-script artist / title / album for a song whose tags are
///     in another script. Any field may be empty and they are NOT equally
///     reliable — the artist is much the strongest (see
///     <see cref="MusicBrainzClient.FindLatinNamesAsync" />).
///     <para>
///     <b>Applying the ALBUM is lossy in a way the other two are not.</b> A song
///     keeps both its Latin and its original title and artist
///     (<c>ChartData.JapaneseTitle</c> / <c>JapaneseArtist</c>), but the
///     Rocksmith manifest has no Japanese ALBUM field, so there is nowhere to
///     keep the original one and taking the suggestion replaces it. That is a
///     consequence of the format, not an oversight — and one more reason
///     nothing is applied without a click.
///     </para>
/// </summary>
public sealed record LatinNameSuggestion(string Artist, string Title, string Album)
{
    public bool HasAnything =>
        !string.IsNullOrWhiteSpace(Artist) ||
        !string.IsNullOrWhiteSpace(Title) ||
        !string.IsNullOrWhiteSpace(Album);
}

/// <summary>
///     Whether a piece of metadata is written in the Latin script — the question
///     that decides whether a romanized name is worth looking up at all, and
///     whether a candidate is an improvement on what we already have.
/// </summary>
public static class ScriptDetection
{
    /// <summary>
    ///     True when the string's LETTERS are predominantly Latin.
    ///     <para>
    ///     Digits, spaces and punctuation are ignored entirely: they carry no
    ///     script, and counting them made "8月" read as half-Latin. Full-width
    ///     forms are folded first, so the full-width digits and Latin letters
    ///     Japanese titles use are not mistaken for CJK. A string with no letters
    ///     at all ("1985", "!!!") is Latin by convention — there is nothing to
    ///     romanize.
    ///     </para>
    /// </summary>
    public static bool IsLatin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var folded = value.Normalize(NormalizationForm.FormKC);
        int latin = 0, other = 0;

        foreach (var rune in folded.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (rune.Value < 0x0250 || IsLatinExtended(rune.Value)) latin++;
            else other++;
        }

        if (latin + other == 0) return true;
        return other * 2 <= latin; // a stray accented loanword must not flip it
    }

    /// <summary>The inverse, named for the question the import path asks.</summary>
    public static bool NeedsRomanization(string? value) => !IsLatin(value);

    private static bool IsLatinExtended(int codepoint) =>
        codepoint is >= 0x1E00 and <= 0x1EFF or   // Latin Extended Additional
        >= 0x2C60 and <= 0x2C7F or   // Latin Extended-C
        >= 0xA720 and <= 0xA7FF;     // Latin Extended-D

    /// <summary>Whether <paramref name="candidate" /> is worth OFFERING in place
    /// of <paramref name="current" />: it must be non-empty, actually Latin, and
    /// different from what the file already says.</summary>
    public static bool IsUsefulRomanization(string? current, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        IsLatin(candidate) &&
        !string.Equals(current?.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    internal static bool LooksLikeEnglishLocale(string? locale) =>
        !string.IsNullOrWhiteSpace(locale) &&
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    internal static string TitleCaseIfShouting(string value) =>
        value.Length > 3 && value == value.ToUpperInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant())
            : value;
}
