using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The router in front of the two acoustic models. MMS-FA covers 1,100+
///     languages and the English head covers one, so a wrong "English" is far more
///     expensive than a wrong "not English" — every test here is really about that
///     asymmetry.
/// </summary>
public sealed class LyricScriptDetectorTests
{
    private static bool English(string paste) =>
        LyricScriptDetector.IsLikelyEnglish(LyricSheetParser.Parse(paste));

    /// <summary>Real romaji, hyphenated per mora exactly as the owner charts it.</summary>
    private const string Romaji = """
        Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de
        Ki-mi wa wa-ra-tte i-ta na-tsu no hi
        So-ra wo mi-a-ge-te i-ta da-ke
        Ka-ze ga fu-i-te ha-na ga chi-ru
        """;

    private const string EnglishLyrics = """
        And I know that you don't care
        All the things that we could have been
        This is what they told me when
        Just a lit-tle while long-er now
        """;

    [Fact]
    public void RomajiIsNotEnglish() => Assert.False(English(Romaji));

    [Fact]
    public void EnglishIsEnglish() => Assert.True(English(EnglishLyrics));

    /// <summary>
    ///     The three phonotactic features separated the two real corpora with NO
    ///     overlap. These pin the measured ranges so a future threshold change has
    ///     to face them: romaji 0.962-0.985 vowel-final, 1.99-2.05 mean length,
    ///     zero l/q/v/x; English 0.217-0.457, 2.82-3.53, 0.066-0.337.
    /// </summary>
    [Fact]
    public void TheMeasuredFeaturesLookLikeTheCorpusTheyCameFrom()
    {
        var romaji = LyricScriptDetector.Measure(LyricSheetParser.Parse(Romaji));
        Assert.True(romaji.VowelFinalShare > 0.90, $"vowel-final was {romaji.VowelFinalShare:0.000}");
        Assert.True(romaji.MeanSyllableLength < 2.4, $"mean length was {romaji.MeanSyllableLength:0.00}");
        Assert.Equal(0.0, romaji.RareLetterShare);

        var english = LyricScriptDetector.Measure(LyricSheetParser.Parse(EnglishLyrics));
        Assert.True(english.VowelFinalShare < 0.70, $"vowel-final was {english.VowelFinalShare:0.000}");
        Assert.True(english.MeanSyllableLength >= 2.4, $"mean length was {english.MeanSyllableLength:0.00}");
        Assert.True(english.RareLetterShare >= 0.02, $"l/q/v/x share was {english.RareLetterShare:0.000}");
    }

    /// <summary>
    ///     German passes every PHONOTACTIC rule — it has l, closed syllables and long
    ///     words — and MMS-FA is the right model for it. The function-word test is
    ///     the only thing standing between it and the English head, so this is the
    ///     load-bearing case for that rule.
    /// </summary>
    [Fact]
    public void GermanIsNotRoutedToTheEnglishModel()
    {
        const string german = """
            Und ich weiss dass du nicht bleibst
            Al-le Din-ge die wir wa-ren sind vor-bei
            Das ist was sie mir er-zaehlt ha-ben
            Nur noch ein biss-chen laen-ger jetzt
            """;

        Assert.False(English(german));
    }

    [Fact]
    public void SpanishIsNotRoutedToTheEnglishModel()
    {
        const string spanish = """
            Y yo se que no te im-por-ta
            To-das las co-sas que pu-di-mos ser
            Es-to es lo que me di-je-ron
            So-lo un po-co mas de tiem-po
            """;

        Assert.False(English(spanish));
    }

    /// <summary>
    ///     A sheet too short to measure must fall back to the multilingual model
    ///     rather than guess from a handful of syllables.
    /// </summary>
    [Fact]
    public void AVeryShortSheetIsNeverClaimedAsEnglish() => Assert.False(English("All the little things"));

    [Fact]
    public void AnEmptySheetIsNotEnglishAndDoesNotThrow()
    {
        Assert.False(LyricScriptDetector.IsLikelyEnglish(LyricSheet.Empty));
        Assert.Equal(0, LyricScriptDetector.Measure(LyricSheet.Empty).SyllableCount);
    }

    /// <summary>
    ///     The function-word test reads WORDS, not syllables: the sheet splits
    ///     "a-bout" in two and neither half is a word. If it read syllables, a sheet
    ///     of hyphenated English would score zero function words and be misrouted.
    /// </summary>
    [Fact]
    public void FunctionWordsAreReassembledFromTheirSyllables()
    {
        var features = LyricScriptDetector.Measure(LyricSheetParser.Parse("a-bout the way that you look"));

        // "the", "that" and "you" are whole words here; "a-bout" is not one.
        Assert.True(features.FunctionWordCount >= 3, $"found {features.FunctionWordCount}");
    }

    /// <summary>Diacritics fold, so an accented sheet measures like its plain form.</summary>
    [Fact]
    public void AccentsDoNotChangeTheDecision()
    {
        Assert.Equal(
            English(Romaji),
            English(Romaji.Replace("So-ra", "Sō-ra", StringComparison.Ordinal)));
    }
}
