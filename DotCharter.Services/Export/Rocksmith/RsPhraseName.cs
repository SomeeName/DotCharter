using System.Globalization;
using System.Text;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The ONE place a phrase name is made legal for Rocksmith, which only accepts
///     <c>[a-zA-Z0-9 _#]</c>. It runs at the export boundary — the RS-XML writer
///     and the SNG builder (whose names the psarc manifest copies) — and never on
///     the chart.
///     <para>
///         It must not run on the LIVE project (as in <c>ChartSpacingEnforcer</c>, on
///         every save and load): that would rewrite the charter's own names
///         (<c>Solo (fast)</c> → <c>Solo fast</c>), erase non-ASCII ones (a
///         Japanese <c>サビ</c> would become <c>""</c>), merge distinct phrases
///         (<c>Riff!</c> and <c>Riff?</c> → <c>Riff</c>) and orphan every
///         <c>Arrangement.PhraseInfos</c> entry (solo flag, max difficulty), which
///         is keyed by the ORIGINAL name.
///     </para>
///     <para>
///         A name that is already legal is returned UNCHANGED, so an imported
///         Rocksmith chart round-trips byte-identically. Otherwise accents are
///         transliterated (<c>Übergang</c> → <c>Ubergang</c>), dash and punctuation
///         runs become one space, whatever is left over is dropped, and a name
///         with nothing legal left gets <c>phrase{n}</c>. <see cref="SanitizeAll" />
///         also keeps the result UNIQUE, because two phrases that sanitize alike
///         would otherwise merge in game.
///     </para>
/// </summary>
public static class RsPhraseName
{
    /// <summary>Usable length of the SNG's 32-byte phrase-name field: the writer
    /// keeps one byte for the terminator, so a longer name is cut there.</summary>
    public const int SngMaxLength = 31;

    /// <summary>True when Rocksmith accepts <paramref name="name" /> as written.</summary>
    public static bool IsLegal(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var c in name)
        {
            if (!IsLegalChar(c)) return false;
        }

        return true;
    }

    /// <summary>
    ///     The legal spelling of one name. <paramref name="index" /> is the phrase's
    ///     position in its list and only feeds the <c>phrase{n}</c> fallback.
    ///     Does NOT de-duplicate — see <see cref="SanitizeAll" />.
    /// </summary>
    public static string Sanitize(string? name, int index)
    {
        if (IsLegal(name)) return name!;

        var sb = new StringBuilder(name?.Length ?? 0);
        var pendingSpace = false;
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            var mapped = Transliterate(c);
            if (mapped != null)
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(mapped);
                continue;
            }

            if (IsLegalChar(c) && c != ' ')
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
                continue;
            }

            // A space, a dash, a bracket or a list separator splits words: keep ONE
            // space, so "Verse 1 - Intro" reads "Verse 1 Intro" (not "Verse 1  Intro")
            // and "Solo (fast)" reads "Solo fast". Anything else — an apostrophe, a
            // "!", a letter of another script — is dropped: "Don't" reads "Dont".
            if (IsWordSeparator(c)) pendingSpace = true;
        }

        return sb.Length > 0 ? sb.ToString() : Fallback(index);
    }

    /// <summary>
    ///     Sanitizes a whole phrase list, index for index, so that every result is
    ///     legal and no REWRITTEN name equals any other result (ignoring case, as
    ///     phrase ids are resolved). Legal names keep their spelling untouched —
    ///     even a duplicate one, so an imported chart still round-trips — and claim
    ///     their spelling first; a rewritten name that collides gets a numeric
    ///     suffix. <paramref name="maxLength" /> is the target format's limit
    ///     (<see cref="SngMaxLength" /> for the SNG); a rewritten name is judged
    ///     unique on the cut spelling, which is what the game sees.
    /// </summary>
    public static IReadOnlyList<string> SanitizeAll(IReadOnlyList<string> names, int maxLength = int.MaxValue)
    {
        var result = new string?[names.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < names.Count; i++)
        {
            if (!IsLegal(names[i])) continue;
            result[i] = Cut(names[i], maxLength);
            taken.Add(result[i]!);
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (result[i] != null) continue;

            var candidate = Cut(Sanitize(names[i], i), maxLength);
            for (var n = 2; !taken.Add(candidate); n++)
            {
                var suffix = " " + n.ToString(CultureInfo.InvariantCulture);
                candidate = Cut(Sanitize(names[i], i), maxLength - suffix.Length).TrimEnd() + suffix;
            }

            result[i] = candidate;
        }

        return result!;
    }

    private static string Fallback(int index) => "phrase" + index.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string name, int maxLength) =>
        name.Length <= maxLength ? name : name[..Math.Max(1, maxLength)];

    private static bool IsWordSeparator(char c) =>
        char.IsWhiteSpace(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        || c is '/' or '\\' or '|' or ',' or ';' or ':' or '.' or '+' or '&' or '~' or '=';

    private static bool IsLegalChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '_' or '#';

    /// <summary>Letters that do not decompose into a base letter plus a mark.</summary>
    private static string? Transliterate(char c) => c switch
    {
        'ß' => "ss",
        'Æ' => "AE",
        'æ' => "ae",
        'Œ' => "OE",
        'œ' => "oe",
        'Ø' => "O",
        'ø' => "o",
        'Đ' => "D",
        'đ' => "d",
        'Ł' => "L",
        'ł' => "l",
        'Þ' => "Th",
        'þ' => "th",
        'ı' => "i",
        _ => null
    };
}
