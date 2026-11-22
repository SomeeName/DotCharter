namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Moves the start of a syllable the rules cut out of an unhyphenated word (hyphenation
///     patterns, mora rules) from its first letter to the first frame of its vowel. The cut is
///     typographic, the voice enters on the vowel. Only syllables that continue a word are moved,
///     so a word's own onset keeps the convention every measured number uses; a syllable with no
///     vowel letter, or one that already opens on its vowel, has no evidence to move it and keeps
///     the path's start.
/// </summary>
internal static class LyricSyllableVowelSnap
{
    /// <summary>The frame to start the syllable at, or null when its start stands.</summary>
    internal static int? VowelFrame(
        LyricSheet sheet, LyricTokenization tokenization, IReadOnlyList<TokenSpan> spans, HashSet<int> vowels, SyllableTokens syllable)
    {
        var index = syllable.SyllableIndex;
        if (index == 0 || !sheet.Syllables[index].SplitByRules || !sheet.Syllables[index - 1].ContinuesWord) return null;

        if (syllable.TokenCount == 0 || vowels.Contains(tokenization.Tokens[syllable.TokenStart])) return null;

        for (var t = 1; t < syllable.TokenCount; t++)
        {
            if (vowels.Contains(tokenization.Tokens[syllable.TokenStart + t])) return spans[syllable.TokenStart + t].StartFrame;
        }

        return null;
    }
}
