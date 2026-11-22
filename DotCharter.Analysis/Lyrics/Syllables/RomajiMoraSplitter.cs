namespace DotCharter.Analysis.Lyrics.Syllables;

/// <summary>
///     Splits one romaji word into morae, the romaji twin of <see cref="KanaMoraSplitter" />. A
///     consonant with its glide (kya, shu, cho, tsu) and its vowel is one mora; every vowel is
///     one, so "kyou" is kyo-u; an n or m that is not followed by a vowel or y (before a
///     consonant, or at the word end) is one mora, as is the first letter of a doubled consonant
///     (the k of "kk", the t of "tch"). A vowel with a macron or circumflex stays whole. Returns
///     null for a word that is not valid Hepburn romaji (an l, q, v or x, or a consonant that no
///     vowel follows), which the caller leaves alone.
/// </summary>
public static class RomajiMoraSplitter
{
    private const string Vowels = "aeiouāēīōūâêîôû";

    /// <summary>The morae of <paramref name="word" /> (letters and apostrophes only), or null when it is not romaji.</summary>
    public static IReadOnlyList<string>? Split(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var text = word.ToLowerInvariant();
        var morae = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var length = MoraLength(text, i);
            if (length == 0) return null;
            var end = i + length;
            while (end < text.Length && text[end] == '\'') end++;
            morae.Add(word[i..end]);
            i = end;
        }

        return morae;
    }

    /// <summary>True when every letter of the word is Hepburn romaji and it splits into morae.</summary>
    public static bool IsRomaji(string word) => word.Length > 0 && Split(word) is not null;

    private static bool IsVowel(string text, int at) => at < text.Length && Vowels.Contains(text[at]);

    private static int MoraLength(string text, int at)
    {
        var c = text[at];
        if (Vowels.Contains(c)) return 1;
        if (!char.IsAsciiLetterLower(c)) return 0;
        var next = at + 1 < text.Length ? text[at + 1] : '\0';

        if (c is 'n' or 'm' && !IsVowel(text, at + 1) && next != 'y') return 1;
        if (c is 'l' or 'q' or 'v' or 'x') return 0;
        if (c == next || c == 't' && next == 'c') return 1;

        var length = 1;
        if (c is 's' or 'c' && next == 'h' || c == 't' && next == 's' || c == 'd' && next == 'z') length++;
        if (at + length < text.Length && text[at + length] == 'y' && c != 'y') length++;
        return IsVowel(text, at + length) ? length + 1 : 0;
    }
}
