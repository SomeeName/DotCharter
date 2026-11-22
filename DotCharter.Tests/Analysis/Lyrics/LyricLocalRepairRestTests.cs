using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>Rests inside a line and voice carried over from the previous line (#627).</summary>
public sealed class LyricLocalRepairRestTests
{
    [Fact]
    public void ConfidentSyllablesOnEitherSideOfAMidLineRestStayWhereTheyAre()
    {
        double[] starts = [1.0, 1.3, 2.0, 2.3];
        var result = Repair("a-b-c-d", starts, [0.9, 0.9, 0.9, 0.9], starts, [(1.0, 1.5), (1.9, 2.5)]);

        for (var i = 0; i < starts.Length; i++)
        {
            Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
            Assert.Equal(0.9, result[i].Confidence, 1e-9);
            Assert.Equal(LyricReviewReason.None, result[i].ReviewReasons);
        }
    }

    [Fact]
    public void AConfidentOnsetJustAheadOfItsOwnRegionStaysWhereItIsAndIsFlagged()
    {
        double[] starts = [1.0, 1.3, 1.85];
        var result = Repair("a-b-c", starts, [0.9, 0.9, 0.9], starts, [(1.0, 1.5), (2.0, 2.6)]);

        for (var i = 0; i < starts.Length; i++)
        {
            Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
            Assert.Equal(starts[i] + 0.02, result[i].EndSeconds, 1e-9);
            Assert.Equal(0.9, result[i].Confidence, 1e-9);
            Assert.False(result[i].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        }
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    [Fact]
    public void WithThreeVoicedRegionsOnlyTheOnsetOnUnvoicedFramesMoves()
    {
        double[] starts = [1.0, 1.3, 2.0, 2.3, 2.75, 3.2];
        var result = Repair("a-b-c-d-b-c", starts, [0.9, 0.9, 0.9, 0.9, 0.01, 0.9],
            [1.0, 1.3, 2.0, 2.3, 3.0, 3.2], [(1.0, 1.5), (1.9, 2.5), (2.9, 3.5)]);

        foreach (var i in new[] { 0, 1, 2, 3, 5 }) Assert.Equal(starts[i], result[i].StartSeconds, 1e-6);
        Assert.Equal(3.0, result[4].StartSeconds, 0.03);
        Assert.True(result[4].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.True(result[4].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        Assert.Equal(LyricReviewReason.None, result[0].ReviewReasons);
        Assert.Equal(LyricReviewReason.None, result[1].ReviewReasons);
    }

    [Fact]
    public void AHeldNoteRunningIntoTheNextLineDoesNotFlagThatLinesOpening()
    {
        double[] starts = [1.0, 1.6, 1.9];
        var result = Repair("a\nb-c", starts, [0.9, 0.9, 0.9], starts, [(1.0, 2.3)]);

        Assert.All(result, s => Assert.Equal(LyricReviewReason.None, s.ReviewReasons));
        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-9);
    }

    [Fact]
    public void ANoteHeldForOverASecondIsNotABlankGapButARestOfThatLengthIs()
    {
        double[] starts = [1.0, 2.5];
        var held = Repair("a-b", starts, [0.9, 0.9], starts, [(1.0, 3.0)]);
        var rested = Repair("a-b", starts, [0.9, 0.9], starts, [(1.0, 1.4), (2.4, 3.0)]);

        Assert.All(held, s => Assert.Equal(LyricReviewReason.None, s.ReviewReasons));
        Assert.All(rested, s => Assert.True(s.ReviewReasons.HasFlag(LyricReviewReason.Gap)));
        Assert.Equal(2.5, rested[1].StartSeconds, 1e-6);
    }

    [Fact]
    public void VoiceThatBeginsAfterAPauseWellBeforeTheFirstOnsetStillFlagsTheOpening()
    {
        double[] starts = [1.0, 2.0, 2.3];
        var result = Repair("a\nb-c", starts, [0.9, 0.9, 0.9], starts, [(1.0, 1.2), (1.6, 2.6)]);

        Assert.Equal(LyricReviewReason.None, result[0].ReviewReasons);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.Equal(2.0, result[1].StartSeconds, 1e-6);
    }

    [Fact]
    public void AnUnheardOpeningLeftOnThePreviousLinesHeldNoteRejoinsItsLine()
    {
        var result = Repair("a\nb-c-d", [1.0, 1.5, 7.3, 7.6], [0.9, 0.01, 0.9, 0.9], [1.0, 7.0, 7.3, 7.6], [(1.0, 2.0), (7.0, 8.0)]);

        Assert.Equal(7.0, result[1].StartSeconds, 0.03);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        Assert.Equal(7.3, result[2].StartSeconds, 1e-6);
    }

    [Fact]
    public void AHeardOpeningBeforeAnInstrumentalBreakIsNotPulledAcrossIt()
    {
        double[] starts = [1.0, 1.5, 7.3, 7.6];
        var result = Repair("a\nb-c-d", starts, [0.9, 0.9, 0.9, 0.9], starts, [(1.0, 2.0), (7.0, 8.0)]);

        for (var i = 0; i < starts.Length; i++) Assert.Equal(starts[i], result[i].StartSeconds, 1e-6);
        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    /// <summary>
    ///     Emissions peak for 120 ms from each entry of <paramref name="peaks" /> (one per syllable);
    ///     the stem is voiced inside <paramref name="voiced" /> and silent elsewhere.
    /// </summary>
    private static IReadOnlyList<AlignedSyllable> Repair(
        string text, double[] starts, double[] confidence, double[] peaks, (double From, double To)[] voiced)
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
            for (var column = 0; column < width; column++)
                probabilities[f * width + column] = (float)Math.Log(column == symbol ? 0.96 : 0.01);
        }

        var levels = Enumerable.Range(0, 1000)
            .Select(i => voiced.Any(v => i * 0.01 >= v.From - 1e-9 && i * 0.01 < v.To - 1e-9) ? 0.5f : 0f).ToArray();
        var aligned = starts.Select((s, i) => new AlignedSyllable(i, s, s + 0.02, confidence[i], false)).ToArray();
        return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, frames, width, 0.02),
            aligned, new VocalEnvelope(levels, 0.01), LyricAlignmentOptions.Default, TestContext.Current.CancellationToken);
    }
}
