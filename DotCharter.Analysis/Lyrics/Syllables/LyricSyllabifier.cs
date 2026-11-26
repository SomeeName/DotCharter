namespace DotCharter.Analysis.Lyrics.Syllables;

/// <summary>Languages that have a hyphenation pattern file.</summary>
public enum SyllableLanguage
{
    English,
    German,
    French,
    Spanish
}

/// <summary>
///     Splits one written word into the syllables a singer gives it. The break points come
///     from the TeX/LibreOffice hyphenation patterns (typographic, so line-break oriented),
///     then three singing rules correct them: no syllable without a vowel (a cluster that
///     the patterns cut off joins its neighbour), a lone final <c>e</c> / <c>ed</c> / <c>es</c>
///     that English does not pronounce stays on the syllable before it, and a word the
///     patterns do not break stays one syllable.
/// </summary>
public static class LyricSyllabifier
{
    private static readonly Dictionary<SyllableLanguage, Lazy<HyphenationPatterns>> Patterns = new()
    {
        [SyllableLanguage.English] = new(() => HyphenationPatterns.LoadEmbedded("hyph_en_US.dic.gz")),
        [SyllableLanguage.German] = new(() => HyphenationPatterns.LoadEmbedded("hyph_de_DE.dic.gz")),
        [SyllableLanguage.French] = new(() => HyphenationPatterns.LoadEmbedded("hyph_fr.dic.gz")),
        [SyllableLanguage.Spanish] = new(() => HyphenationPatterns.LoadEmbedded("hyph_es.dic.gz"))
    };

    /// <summary>The syllables of <paramref name="word" />, concatenating back to it. A word with no letters is returned whole.</summary>
    public static IReadOnlyList<string> Split(string word, SyllableLanguage language)
    {
        ArgumentNullException.ThrowIfNull(word);
        var original = word;
        word = LyricApostrophes.Normalize(word);
        if (word.Length < 3 || !word.All(c => char.IsLetter(c) || c == '\'')) return [original];

        // The file's left/right margin keywords are not applied: they exist for line breaks,
        // and a singer sings "a-bout" and "pret-ty".
        var points = Patterns[language].Value.Breaks(word);
        AddOpeningVowelBreak(word, language, points);
        var parts = new List<string>();
        var start = 0;
        foreach (var point in points)
        {
            parts.Add(original[start..point]);
            start = point;
        }

        parts.Add(original[start..]);
        MergeVowelless(parts);
        if (language == SyllableLanguage.English) MergeSilentEnding(parts);
        return parts;
    }

    /// <summary>
    ///     The patterns never leave a one-letter opening ("a" of "a-bout") on a line, but it is
    ///     its own sung syllable: a vowel, one consonant and a vowel opens a new syllable after
    ///     the first vowel. English only does it for "a"; "open" and "even" stay with the patterns.
    ///     After an elision apostrophe (<c>l'amour</c>) the word proper starts after it.
    /// </summary>
    private static void AddOpeningVowelBreak(string word, SyllableLanguage language, List<int> points)
    {
        var apostrophe = word.IndexOf('\'');
        var start = apostrophe is >= 0 and < 3 ? apostrophe + 1 : 0;
        if (word.Length < start + 3) return;
        var first = char.ToLowerInvariant(word[start]);
        if (!IsVowel(first) || IsVowel(word[start + 1]) || !char.IsLetter(word[start + 1]) || !IsVowel(word[start + 2])) return;
        if (language == SyllableLanguage.English && (first != 'a' || start != 0)) return;
        if (points.Contains(start + 1)) return;
        points.Add(start + 1);
        points.Sort();
    }

    /// <summary>
    ///     Joins every part without a vowel NUCLEUS to a neighbour. A <c>y</c> directly before a vowel is
    ///     the consonant glide of "you" / "young" / "beyond", not a nucleus, so the patterns' <c>y-ou</c>
    ///     becomes one syllable again; the <c>y</c> of "hap-py" or "play" still counts as a vowel.
    /// </summary>
    private static void MergeVowelless(List<string> parts)
    {
        for (var i = 0; i < parts.Count && parts.Count > 1;)
        {
            if (HasNucleus(parts, i)) { i++; continue; }

            // An opening cluster joins the next syllable, anything else the previous one.
            if (i == 0) { parts[1] = parts[0] + parts[1]; parts.RemoveAt(0); }
            else { parts[i - 1] += parts[i]; parts.RemoveAt(i); i = Math.Max(i - 1, 0); }
        }
    }

    private static void MergeSilentEnding(List<string> parts)
    {
        if (parts.Count < 2) return;
        var last = parts[^1].ToLowerInvariant();
        var before = parts[^2].ToLowerInvariant();
        var endsInConsonant = !HasVowel(before[^1].ToString());
        var silent = last == "e"
            || last.Length == 2 && last[1] == 'e' && !HasVowel(last[..1])
            || last == "es" && endsInConsonant && !"sxzcgh".Contains(before[^1])
            || last == "ed" && endsInConsonant && !"td".Contains(before[^1]);
        if (!silent) return;
        parts[^2] += parts[^1];
        parts.RemoveAt(parts.Count - 1);
    }

    private static bool HasVowel(string text) => text.Any(IsVowel);

    private static bool HasNucleus(List<string> parts, int index)
    {
        var part = parts[index];
        for (var i = 0; i < part.Length; i++)
        {
            if (!IsVowel(part[i])) continue;
            if (char.ToLowerInvariant(part[i]) != 'y') return true;
            var next = i + 1 < part.Length ? part[i + 1] : index + 1 < parts.Count ? parts[index + 1][0] : '\0';
            if (!IsVowel(next) || char.ToLowerInvariant(next) == 'y') return true;
        }

        return false;
    }

    private static bool IsVowel(char c) => "aeiouyáéíóúàèìòùâêîôûäöüëïæœ".Contains(char.ToLowerInvariant(c));
}
