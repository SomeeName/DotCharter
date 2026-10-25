using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The per-class onset shift in <see cref="LyricAligner" /> and the classifier
///     that picks the syllables it applies to. Pure — a spelled-out emission matrix.
/// </summary>
public sealed class LyricAlignerOnsetClassTests
{
    private static LyricVocabulary Vocab() => LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);

    /// <summary>One letter per five 20 ms frames: the letter wins the first frame, blank the rest.</summary>
    private static CtcEmissions Spell(string letters)
    {
        var vocab = Vocab();
        var width = vocab.WidthWithStar;
        var frames = letters.Length * 5;
        var data = new float[frames * width];

        for (var f = 0; f < frames; f++)
        {
            var winner = f % 5 == 0 && vocab.TryGetId(letters[f / 5], out var id) ? id : vocab.BlankId;
            for (var s = 0; s < width; s++)
                data[f * width + s] = (float)Math.Log(s == winner ? 0.9 : 0.1 / (width - 1));
        }

        return new CtcEmissions(data, frames, width, 0.02);
    }

    private static IReadOnlyList<AlignedSyllable> Align(string sheet, string letters, double vowelBias)
    {
        var result = LyricAligner.Align(LyricSheetParser.Parse(sheet), Vocab(), Spell(letters),
            new LyricAlignmentOptions { StarLogProb = -20.0, VowelInitialOnsetBiasSeconds = vowelBias });

        Assert.True(result.Succeeded, result.Failure);
        return result.Syllables;
    }

    [Fact]
    public void AVowelInitialSyllableMovesEarlierAndTheOthersDoNot()
    {
        var plain = Align("c a b", "cab", 0.0);
        var shifted = Align("c a b", "cab", -0.05);

        Assert.Equal(plain[0].StartSeconds, shifted[0].StartSeconds, 6);
        Assert.Equal(plain[1].StartSeconds - 0.05, shifted[1].StartSeconds, 6);
        Assert.Equal(plain[2].StartSeconds, shifted[2].StartSeconds, 6);
    }

    [Fact]
    public void TheShiftNeverCarriesAStartBeforeItsPredecessor()
    {
        var plain = Align("c a b", "cab", 0.0);
        var shifted = Align("c a b", "cab", -5.0);

        Assert.Equal(plain[0].StartSeconds, shifted[1].StartSeconds, 6);
        Assert.True(shifted[1].EndSeconds >= shifted[1].StartSeconds);
    }

    [Fact]
    public void AZeroShiftChangesNothing()
    {
        var plain = Align("c a b", "cab", 0.0);
        var again = Align("c a b", "cab", 0.0);

        Assert.Equal(plain, again);
    }

    [Theory]
    [InlineData("tsu", SyllableOnsetClass.Unvoiced)]
    [InlineData("chi", SyllableOnsetClass.Unvoiced)]
    [InlineData("shi", SyllableOnsetClass.Unvoiced)]
    [InlineData("Ka", SyllableOnsetClass.Unvoiced)]
    [InlineData("fu", SyllableOnsetClass.Unvoiced)]
    [InlineData("ma", SyllableOnsetClass.Voiced)]
    [InlineData("ra", SyllableOnsetClass.Voiced)]
    [InlineData("wa", SyllableOnsetClass.Voiced)]
    [InlineData("ga", SyllableOnsetClass.Voiced)]
    [InlineData("a", SyllableOnsetClass.Vowel)]
    [InlineData("O", SyllableOnsetClass.Vowel)]
    [InlineData("'e", SyllableOnsetClass.Vowel)]
    [InlineData("", SyllableOnsetClass.Vowel)]
    public void ASyllableIsClassifiedByItsFirstSound(string text, SyllableOnsetClass expected) =>
        Assert.Equal(expected, SyllableOnsetClassifier.Classify(text));
}
