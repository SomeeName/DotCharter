namespace DotCharter.Analysis.Lyrics;

/// <summary>How a syllable opens, which decides how far its CTC onset is trusted.</summary>
public enum SyllableOnsetClass
{
    /// <summary>
    ///     Opens on a vowel, or has no letters. CTC fires on the confident vowel
    ///     and, with no consonant to anchor the frame, starts the span late.
    /// </summary>
    Vowel,

    /// <summary>Opens on a voiced consonant (b d g z j m n r w y v l): voiced from the first frame.</summary>
    Voiced,

    /// <summary>Opens on an unvoiced consonant (s k t h f p c x q, and the digraphs ts ch sh).</summary>
    Unvoiced
}

/// <summary>Classifies romanized syllable text by its first sound.</summary>
public static class SyllableOnsetClassifier
{
    /// <summary>The class of <paramref name="syllableText" />'s first letter.</summary>
    public static SyllableOnsetClass Classify(string? syllableText)
    {
        if (string.IsNullOrEmpty(syllableText)) return SyllableOnsetClass.Vowel;

        foreach (var c in syllableText)
        {
            if (!char.IsLetter(c)) continue;

            return char.ToLowerInvariant(c) switch
            {
                's' or 'k' or 't' or 'h' or 'f' or 'p' or 'c' or 'x' or 'q' => SyllableOnsetClass.Unvoiced,
                'b' or 'd' or 'g' or 'z' or 'j' or 'm' or 'n' or 'r' or 'w' or 'y' or 'v' or 'l' => SyllableOnsetClass.Voiced,
                _ => SyllableOnsetClass.Vowel
            };
        }

        return SyllableOnsetClass.Vowel;
    }
}
