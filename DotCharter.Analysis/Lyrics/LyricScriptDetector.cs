namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Decides whether a pasted sheet is ENGLISH, so the aligner can reach for an
///     English-only acoustic model instead of the multilingual one.
///     <para>
///         <b>The bar is deliberately high and the default is deliberately
///         "no".</b> MMS-FA aligns romanized text in 1,100+ languages; the English
///         wav2vec2 head aligns exactly one. So a wrong "yes" is much more
///         expensive than a wrong "no": it hands a German or Spanish sheet to a
///         model that has never seen the language, while a wrong "no" only forgoes
///         an improvement. Every rule below therefore has to agree before this
///         returns true.
///     </para>
///     <para>
///         <b>Measured on the real inputs</b> — the 40 official English vocal
///         arrangements that serve to evaluate the aligner and the owner's four
///         hand-synced romaji charts. The three phonotactic features
///         separate the two sets COMPLETELY, with no overlap at all:
///     </para>
///     <code>
///                              romaji (4)        English (40)
///       vowel-final share      0.962 - 0.985     0.217 - 0.457
///       mean syllable length   1.99  - 2.05      2.82  - 3.53
///       share with l/q/v/x     0.000             0.066 - 0.337
///     </code>
///     <para>
///         The thresholds sit in the empty bands between those ranges, so the
///         decision is nowhere near a boundary on real material. Hepburn romaji
///         has no <c>l</c>, <c>q</c>, <c>v</c> or <c>x</c> at all and every mora
///         ends in a vowel or the single nasal <c>n</c>, which is why the
///         separation is structural rather than statistical.
///     </para>
///     <para>
///         <b>The function-word test is what keeps other Latin-script languages
///         out.</b> German and French would pass all three phonotactic rules — they
///         have <c>l</c>, closed syllables and long words — and MMS-FA is the right
///         model for both. So English also has to be positively identified by words
///         that cannot be a romaji mora and are not shared with its neighbours.
///     </para>
/// </summary>
public static class LyricScriptDetector
{
    /// <summary>
    ///     Below this share of syllables ending in a vowel, the text is not
    ///     mora-timed. Romaji measures 0.96+; English 0.46 at most.
    /// </summary>
    private const double MaxVowelFinalShare = 0.70;

    /// <summary>Romaji morae average 2.0 characters; English syllables 2.8+.</summary>
    private const double MinMeanSyllableLength = 2.4;

    /// <summary>
    ///     Hepburn romaji contains none of <c>l/q/v/x</c>. Every English sheet
    ///     measured carries them on at least 6.6% of syllables, so this is a floor
    ///     well under the observed minimum rather than a tuned value.
    /// </summary>
    private const double MinRareLetterShare = 0.02;

    /// <summary>How many DISTINCT function words must appear.</summary>
    private const int MinFunctionWords = 2;

    /// <summary>
    ///     English function words that cannot be a romaji mora — each has a
    ///     consonant cluster, an <c>l</c>, or a final consonant other than
    ///     <c>n</c> — and that are not the corresponding function words of the
    ///     other Latin-script languages this might see.
    /// </summary>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "the", "and", "you", "your", "that", "this", "with", "was", "for",
        "all", "just", "like", "what", "when", "they", "she", "her", "his",
        "from", "have", "will", "there", "their", "would", "could", "should",
        "about", "than", "them", "these", "those", "where", "while", "which",
    };

    /// <summary>Features behind the decision, exposed so a probe can report them.</summary>
    /// <param name="SyllableCount">Syllables that contributed at least one letter.</param>
    /// <param name="VowelFinalShare">Share of syllables ending in a vowel.</param>
    /// <param name="MeanSyllableLength">Mean letters per syllable.</param>
    /// <param name="RareLetterShare">Share of syllables containing l, q, v or x.</param>
    /// <param name="FunctionWordCount">Distinct English function words found.</param>
    public readonly record struct ScriptFeatures(
        int SyllableCount,
        double VowelFinalShare,
        double MeanSyllableLength,
        double RareLetterShare,
        int FunctionWordCount);

    /// <summary>
    ///     True only when every rule agrees the sheet is English. Anything else —
    ///     romaji, another language, too little text to tell — returns false and
    ///     gets the multilingual model.
    /// </summary>
    public static bool IsLikelyEnglish(LyricSheet sheet) => IsLikelyEnglish(Measure(sheet));

    /// <inheritdoc cref="IsLikelyEnglish(LyricSheet)" />
    public static bool IsLikelyEnglish(ScriptFeatures features) =>
        // Too short to judge: a two-word sheet can look like anything.
        features.SyllableCount >= 12
        && features.VowelFinalShare < MaxVowelFinalShare
        && features.MeanSyllableLength >= MinMeanSyllableLength
        && features.RareLetterShare >= MinRareLetterShare
        && features.FunctionWordCount >= MinFunctionWords;

    /// <summary>Computes the features without deciding, for probes and tests.</summary>
    public static ScriptFeatures Measure(LyricSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var count = 0;
        var vowelFinal = 0;
        var letters = 0;
        var rare = 0;
        var found = new HashSet<string>(StringComparer.Ordinal);

        // Words, not syllables, for the function-word test: the sheet splits
        // "a-bout" across two syllables and neither half is a word.
        var word = new System.Text.StringBuilder();

        foreach (var syllable in sheet.Syllables)
        {
            var folded = FoldLetters(syllable.Text);
            word.Append(folded);

            if (!syllable.ContinuesWord)
            {
                if (word.Length > 0 && FunctionWords.Contains(word.ToString())) found.Add(word.ToString());
                word.Clear();
            }

            if (folded.Length == 0) continue;

            count++;
            letters += folded.Length;
            if (IsVowel(folded[^1])) vowelFinal++;
            foreach (var c in folded)
            {
                if (c is 'l' or 'q' or 'v' or 'x')
                {
                    rare++;
                    break;
                }
            }
        }

        if (count == 0) return new ScriptFeatures(0, 0, 0, 0, 0);

        return new ScriptFeatures(
            count,
            (double)vowelFinal / count,
            (double)letters / count,
            (double)rare / count,
            found.Count);
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    /// <summary>
    ///     Lower-case letters only, diacritics folded — the same reduction
    ///     <see cref="RomanizedLyricTokenizer" /> applies, so the decision is made
    ///     on the text the model will actually be given. The apostrophe is dropped
    ///     here (unlike the tokenizer) so <c>don't</c> measures as a
    ///     consonant-final syllable rather than an apostrophe-final one.
    /// </summary>
    private static string FoldLetters(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z') sb.Append(lower);
        }

        return sb.ToString();
    }
}
