using System.Text;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Lyric sites salt their sheets with Cyrillic and Greek letters that LOOK Latin (a Cyrillic
///     <c>е</c> inside "the"), so a copied line reads normally and still fails a text match. The
///     aligner cannot read such a letter and the chart would keep it. A word that holds at least one
///     Basic Latin letter and whose every other letter is such a look-alike is folded to Latin;
///     a word with any letter that is genuinely Cyrillic or Greek (no Latin twin) is left
///     exactly as typed, so real Russian or Greek lyrics never change.
/// </summary>
public static class LyricHomoglyphs
{
    private static readonly Dictionary<char, char> ToLatin = new()
    {
        // Cyrillic lower case
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x',
        ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['ԁ'] = 'd', ['ԛ'] = 'q', ['ԝ'] = 'w', ['һ'] = 'h',
        ['ӏ'] = 'l', ['ё'] = 'ë', ['ї'] = 'ï',
        // Cyrillic upper case
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
        ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X', ['У'] = 'Y', ['І'] = 'I', ['Ј'] = 'J',
        ['Ѕ'] = 'S', ['Ԛ'] = 'Q', ['Ԝ'] = 'W', ['Ӏ'] = 'I',
        // Greek lower case
        ['ο'] = 'o', ['α'] = 'a', ['ν'] = 'v', ['ι'] = 'i', ['κ'] = 'k', ['ρ'] = 'p', ['υ'] = 'u',
        ['χ'] = 'x',
        // Greek upper case
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K',
        ['Μ'] = 'M', ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X'
    };

    /// <summary>
    ///     The text with every look-alike letter folded to Latin inside the words that qualify (see the
    ///     class summary); a word is a run between whitespace. <paramref name="foldedWords" /> counts
    ///     the words changed. Returns <paramref name="text" /> itself when nothing changes.
    /// </summary>
    public static string Fold(string text, out int foldedWords)
    {
        ArgumentNullException.ThrowIfNull(text);
        foldedWords = 0;
        if (!text.Any(ToLatin.ContainsKey)) return text;

        var result = new StringBuilder(text);
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            if (FoldWord(text, start, end, result)) foldedWords++;
            start = end;
        }

        return foldedWords == 0 ? text : result.ToString();
    }

    private static bool FoldWord(string text, int start, int end, StringBuilder result)
    {
        var hasLatin = false;
        var hasLookAlike = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') hasLatin = true;
            else if (ToLatin.ContainsKey(c)) hasLookAlike = true;
            else if (IsCyrillicOrGreek(c)) return false;
        }

        if (!hasLatin || !hasLookAlike) return false;
        for (var i = start; i < end; i++)
        {
            if (ToLatin.TryGetValue(text[i], out var latin)) result[i] = latin;
        }

        return true;
    }

    private static bool IsCyrillicOrGreek(char c) => c is >= 'Ͱ' and <= 'Ͽ' or >= 'Ѐ' and <= 'ԯ' or >= 'ἀ' and <= '῿';
}
