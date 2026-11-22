using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Syllables the letter model heard nothing of: placed by where their voice begins when the
///     letters prove the place, flagged and left where they are otherwise (#627).
/// </summary>
public sealed class LyricLocalRepairUnheardTests
{
    private const double Unheard = double.NaN;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnheardOpeningStartsWithItsVoiceAndTheSecondSyllableGetsItsLettersBack(bool consonantDip)
    {
        (double From, double To)[] voiced = consonantDip ? [(1.0, 1.4), (2.0, 2.55), (2.56, 3.5)] : [(1.0, 1.4), (2.0, 3.5)];
        var result = Repair("a\nb-c-d", [1.0, 2.6, 2.9, 3.0], [1.02, 2.62, 2.92, 3.02], [0.9, 0.01, 0.01, 0.9],
            [1.0, Unheard, 2.6, 3.0], voiced, residue: (1, 2.6));

        Assert.Equal(1.0, result[0].StartSeconds, 1e-9);
        Assert.Equal(2.0, result[1].StartSeconds, 0.011);
        Assert.Equal(2.6, result[2].StartSeconds, 0.03);
        Assert.Equal(3.0, result[3].StartSeconds, 0.03);
        Assert.Equal(result[2].StartSeconds, result[1].EndSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.Repaired | LyricReviewReason.LowScore));
    }

    [Fact]
    public void AHeardLowScoreOpeningBehindABreathStaysAndIsFlagged()
    {
        double[] starts = [1.0, 2.6, 2.9, 3.2];
        var result = Repair("a\nb-c-d", starts, [1.02, 2.62, 2.92, 3.22], [0.9, 0.03, 0.9, 0.9], starts, [(1.0, 1.4), (2.0, 3.5)]);

        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
    }

    [Fact]
    public void AHeardOpeningStaysWhenItsFaintRepeatAlignsOntoItsLetters()
    {
        double[] starts = [1.0, 2.6, 2.9, 3.2];
        var result = Repair("a\nb-b-c", starts, [1.02, 2.62, 2.92, 3.22], [0.9, 0.9, 0.03, 0.9], [1.0, 2.6, Unheard, 3.2], [(1.0, 1.4), (2.0, 3.5)],
            residue: (2, 2.9));

        Assert.Equal(2.6, result[1].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    [Fact]
    public void AnOpeningHeardWhereItStandsIsNotDisplacedWhenTheNextSyllableLandsThereToo()
    {
        var result = Repair("a\nb-c-d", [1.0, 2.6, 2.9, 3.0], [1.02, 2.62, 2.92, 3.02], [0.9, 0.01, 0.01, 0.9],
            [1.0, 2.6, 2.62, 3.0], [(1.0, 1.4), (2.0, 3.5)]);

        Assert.Equal(2.6, result[1].StartSeconds, 1e-9);
        Assert.Equal(2.62, result[1].EndSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
        Assert.False(result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Fact]
    public void AnOpeningThatSharesItsFirstLetterWithTheNextSyllableHasNoDisplacementProof()
    {
        double[] starts = [1.0, 2.6, 2.9, 3.2];
        var result = Repair("a\nb-b-c", starts, [1.02, 2.62, 2.92, 3.22], [0.9, 0.03, 0.01, 0.9], [1.0, 2.6, Unheard, 3.2],
            [(1.0, 1.4), (2.0, 3.5)]);

        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
        Assert.All(result, s => Assert.False(s.ReviewReasons.HasFlag(LyricReviewReason.Repaired)));
    }

    [Fact]
    public void AnUnheardSyllableOnUnvoicedFramesDoesNotMoveIntoTheNextLinesOpeningVoice()
    {
        double[] starts = [1.0, 1.8, 2.4];
        var result = Repair("a-b\nc", starts, [1.02, 1.82, 2.42], [0.9, 0.01, 0.9], [1.0, Unheard, 2.4], [(1.0, 1.5), (2.0, 2.9)]);

        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
        Assert.Equal(0.01, result[1].Confidence, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
        Assert.All(result, s => Assert.False(s.ReviewReasons.HasFlag(LyricReviewReason.Repaired)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneStrongLetterCannotCarryAMissingOne(bool vowelHeard)
    {
        var result = Repair("a-ba", [1.0, 1.3], [1.02, 2.1], [0.9, 0.01], [1.0, 2.0], [(1.0, 1.5), (2.0, 2.6)],
            letter: vowelHeard ? ('a', 2.06) : null);

        Assert.Equal(vowelHeard ? 2.0 : 1.3, result[1].StartSeconds, 1e-9);
        Assert.Equal(vowelHeard, result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Fact]
    public void ALineOfOneUnheardSyllableStaysAndIsFlagged()
    {
        var result = Repair("a\nb", [1.0, 2.6], [1.02, 3.1], [0.9, 0.01], [1.0, Unheard], [(1.0, 1.4), (2.0, 3.5)], residue: (1, 2.6));

        Assert.Equal(2.6, result[1].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.LowScore));
    }

    [Fact]
    public void WithTheSecondSyllablesLettersAtTheVoiceStartNothingMoves()
    {
        var result = Repair("a\nb-c", [1.0, 2.6, 2.9], [1.02, 2.62, 2.92], [0.9, 0.01, 0.01], [1.0, Unheard, 2.05], [(1.0, 1.4), (2.0, 3.5)]);

        Assert.Equal(2.6, result[1].StartSeconds, 1e-9);
        Assert.Equal(2.9, result[2].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    [Fact]
    public void ALowScoreSyllableStretchedAcrossARestStartsWhereTheVoiceResumesWhenItsLettersAreThere()
    {
        var result = Repair("a-b", [1.0, 1.3], [1.02, 2.1], [0.9, 0.01], [1.0, 2.0], [(1.0, 1.5), (2.0, 2.6)]);

        Assert.Equal(1.0, result[0].StartSeconds, 1e-9);
        Assert.Equal(2.0, result[1].StartSeconds, 1e-9);
        Assert.Equal(2.1, result[1].EndSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap | LyricReviewReason.Repaired));
    }

    [Fact]
    public void ALowScoreSyllableStretchedAcrossARestDoesNotJumpToVoiceThatHoldsNoneOfItsLetters()
    {
        double[] starts = [1.0, 1.3, 3.0];
        var result = Repair("a-b\nc", starts, [1.02, 2.1, 3.02], [0.9, 0.01, 0.9], [1.0, Unheard, 3.0], [(1.0, 1.5), (2.0, 2.4), (3.0, 3.5)]);

        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.LowScore));
        Assert.False(result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Theory]
    [InlineData("a-b", 0.9, 2.1)]
    [InlineData("a-b", 0.01, 1.9)]
    [InlineData("a-b-c", 0.01, 2.1)]
    public void ASyllableStaysBeforeTheRestWhenItIsHeardEndsBeforeTheVoiceResumesOrAnotherStartsThere(
        string text, double confidence, double end)
    {
        var third = text.Length > 3;
        var result = Repair(text, third ? [1.0, 1.3, 2.2] : [1.0, 1.3], third ? [1.02, end, 2.22] : [1.02, end],
            third ? [0.01, confidence, 0.9] : [0.01, confidence], third ? [1.0, 2.04, 2.2] : [1.0, 2.04], [(1.0, 1.5), (2.0, 2.6)]);

        Assert.True(result[1].StartSeconds < 1.5, $"moved across the rest to {result[1].StartSeconds}");
    }

    [Theory]
    [InlineData("a-b\nc", 2.0)]
    [InlineData("a-b\nb", 1.92)]
    public void ASpanReachingIntoTheNextLinesVoiceDoesNotMoveThereEvenWhenThatLineRepeatsTheSyllable(string text, double nextLinePeak)
    {
        var result = Repair(text, [1.0, 1.3, 2.0], [1.02, 2.0, 2.02], [0.9, 0.01, 0.9], [1.0, Unheard, nextLinePeak], [(1.0, 1.5), (1.9, 2.6)]);

        Assert.True(result[1].StartSeconds < 1.5, $"moved into the next line's voice at {result[1].StartSeconds}");
        Assert.Equal(2.0, result[2].StartSeconds, 1e-9);
    }

    /// <summary>
    ///     Emissions peak for 120 ms from each entry of <paramref name="peaks" /> (one per syllable,
    ///     <see cref="Unheard" /> for none); the stem is voiced inside <paramref name="voiced" />.
    ///     <paramref name="residue" /> leaves a faint trace of one syllable's letter on another's
    ///     peak, which is where a forced path puts a syllable it never heard. <paramref name="letter" />
    ///     adds one more heard letter, over whatever peak was there.
    /// </summary>
    private static IReadOnlyList<AlignedSyllable> Repair(
        string text, double[] starts, double[] ends, double[] confidence, double[] peaks, (double From, double To)[] voiced,
        (int Syllable, double At)? residue = null, (char Letter, double At)? letter = null)
    {
        var sheet = LyricSheetParser.Parse(text);
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);
        const int width = 5;
        const int frames = 500;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var symbol = 0;
            for (var i = 0; i < peaks.Length; i++)
                if (f * 0.02 >= peaks[i] - 1e-9 && f * 0.02 < peaks[i] + 0.12 - 1e-9) symbol = sheet.Syllables[i].Text[0] - 'a' + 1;
            if (letter is { } l && f * 0.02 >= l.At - 1e-9 && f * 0.02 < l.At + 0.12 - 1e-9) symbol = l.Letter - 'a' + 1;
            var faint = residue is { } r && f * 0.02 >= r.At - 1e-9 && f * 0.02 < r.At + 0.12 - 1e-9 ? sheet.Syllables[r.Syllable].Text[0] - 'a' + 1 : -1;
            for (var column = 0; column < width; column++)
                probabilities[f * width + column] = (float)Math.Log(column == symbol ? 0.94 : column == faint ? 0.03 : 0.01);
        }

        var levels = Enumerable.Range(0, 1000)
            .Select(i => voiced.Any(v => i * 0.01 >= v.From - 1e-9 && i * 0.01 < v.To - 1e-9) ? 0.5f : 0f).ToArray();
        var aligned = starts.Select((s, i) => new AlignedSyllable(i, s, ends[i], confidence[i], false)).ToArray();
        return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, frames, width, 0.02),
            aligned, new VocalEnvelope(levels, 0.01), LyricAlignmentOptions.Default, TestContext.Current.CancellationToken);
    }
}
