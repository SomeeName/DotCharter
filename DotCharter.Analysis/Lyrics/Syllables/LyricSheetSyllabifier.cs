namespace DotCharter.Analysis.Lyrics.Syllables;

/// <summary>
///     Splits the words a charter pasted WITHOUT hyphens into syllables, so a sheet typed as
///     plain text gets the same one-syllable-per-sung-note structure as a hand-hyphenated one.
///     A word the charter already hyphenated is never touched, and a sheet whose language is not
///     one of the four with patterns (romaji, anything unrecognised) comes back unchanged.
/// </summary>
public static class LyricSheetSyllabifier
{
    private const int MinFunctionWordHits = 3;
    private const int MinRomajiWords = 12;

    private static readonly Dictionary<SyllableLanguage, HashSet<string>> FunctionWords = new()
    {
        [SyllableLanguage.English] = ["the", "and", "you", "that", "with", "for", "your", "are", "this", "but", "not", "have", "all", "just", "when", "what", "will", "can", "from", "they"],
        [SyllableLanguage.German] = ["der", "die", "das", "und", "ich", "nicht", "ist", "ein", "eine", "mit", "auf", "dich", "mich", "wir", "sie", "den", "dem", "wenn", "aber", "nur"],
        [SyllableLanguage.French] = ["le", "la", "les", "et", "je", "tu", "est", "pas", "des", "une", "dans", "pour", "que", "qui", "mon", "ton", "nous", "vous", "avec", "sur"],
        [SyllableLanguage.Spanish] = ["el", "los", "las", "que", "una", "con", "por", "para", "pero", "como", "mas", "muy", "yo", "tu", "mi", "te", "no", "es", "del", "sin"]
    };

    /// <summary>The sheet's language, or null when it is not clearly English, German, French or Spanish.</summary>
    public static SyllableLanguage? DetectLanguage(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var counts = FunctionWords.Keys.ToDictionary(k => k, _ => 0);
        foreach (var word in Words(sheet))
        {
            var text = LyricApostrophes.Normalize(string.Concat(word.Select(s => s.Text))).Trim().ToLowerInvariant().Trim('\'', ',', '.', '!', '?', ';', ':', '"');
            foreach (var (language, words) in FunctionWords)
            {
                if (words.Contains(text)) counts[language]++;
            }
        }

        var ranked = counts.OrderByDescending(c => c.Value).ToList();
        return ranked[0].Value >= MinFunctionWordHits && ranked[0].Value >= 2 * ranked[1].Value ? ranked[0].Key : null;
    }

    /// <summary>
    ///     True when the charter already split ANY word into sung syllables (a word of two or more
    ///     parsed syllables). A lone dash is not a syllable, so punctuation dashes do not count.
    /// </summary>
    public static bool HasHandHyphenation(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        return Words(sheet).Any(w => w.Count > 1);
    }

    /// <summary>
    ///     Returns the sheet with every word of <paramref name="language" /> split into its syllables, or the
    ///     sheet itself when the charter hyphenated any word (a sheet split by hand is never touched).
    /// </summary>
    public static LyricSheet Apply(LyricSheet sheet, SyllableLanguage language)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (HasHandHyphenation(sheet)) return sheet;

        var result = new List<LyricSyllable>(sheet.Syllables.Count);
        foreach (var word in Words(sheet))
        {
            if (word.Count == 1) result.AddRange(SplitSyllable(word[0], language));
            else result.AddRange(word);
        }

        return result.Count == sheet.Syllables.Count ? sheet : new LyricSheet(result, sheet.LineCount);
    }

    /// <summary>True when the sheet is Japanese in romaji: no English/German/French/Spanish, and nearly every word splits into morae.</summary>
    public static bool IsRomajiSheet(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var words = Words(sheet).Select(w => Letters(string.Concat(w.Select(s => s.Text)))).Where(w => w.Length > 0).ToList();
        return words.Count >= MinRomajiWords && DetectLanguage(sheet) is null && words.Count(RomajiMoraSplitter.IsRomaji) >= words.Count * 0.95;
    }

    /// <summary>Returns the sheet with every unhyphenated kana word split into morae and, when the sheet is romaji, every romaji word too.</summary>
    public static LyricSheet ApplyJapanese(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (HasHandHyphenation(sheet)) return sheet;

        var romaji = IsRomajiSheet(sheet);
        var result = new List<LyricSyllable>(sheet.Syllables.Count);
        foreach (var word in Words(sheet))
        {
            if (word.Count != 1) result.AddRange(word);
            else result.AddRange(SplitSyllable(word[0], romaji ? RomajiOrKana : KanaOnly));
        }

        return result.Count == sheet.Syllables.Count ? sheet : new LyricSheet(result, sheet.LineCount);
    }

    private static IReadOnlyList<string> KanaOnly(string text) => KanaMoraSplitter.IsKana(text) ? KanaMoraSplitter.Split(text) : [text];

    private static IReadOnlyList<string> RomajiOrKana(string text) => KanaMoraSplitter.IsKana(text) ? KanaMoraSplitter.Split(text) : RomajiMoraSplitter.Split(text) ?? [text];

    private static string Letters(string text) => new([.. LyricApostrophes.Normalize(text).Where(c => char.IsLetter(c) || c == '\'')]);

    private static IEnumerable<LyricSyllable> SplitSyllable(LyricSyllable syllable, SyllableLanguage language) =>
        SplitSyllable(syllable, text => LyricSyllabifier.Split(text, language));

    private static IEnumerable<LyricSyllable> SplitSyllable(LyricSyllable syllable, Func<string, IReadOnlyList<string>> split)
    {
        var text = syllable.Text;
        var from = 0;
        while (from < text.Length && !char.IsLetter(text[from])) from++;
        var to = text.Length;
        while (to > from && !char.IsLetter(text[to - 1])) to--;

        var parts = split(text[from..to]);
        if (parts.Count < 2)
        {
            yield return syllable;
            yield break;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var last = i == parts.Count - 1;
            var piece = (i == 0 ? text[..from] : "") + parts[i] + (last ? text[to..] : "");
            // Only the word's last piece keeps the line end: `to- geth- er+`, never `to+ geth+ er+`.
            yield return syllable with { Text = piece, ContinuesWord = !last || syllable.ContinuesWord, EndsLine = last && syllable.EndsLine, SplitByRules = true };
        }
    }

    private static IEnumerable<List<LyricSyllable>> Words(LyricSheet sheet)
    {
        var word = new List<LyricSyllable>();
        foreach (var syllable in sheet.Syllables)
        {
            word.Add(syllable);
            if (syllable.ContinuesWord) continue;
            yield return word;
            word = [];
        }

        if (word.Count > 0) yield return word;
    }
}
