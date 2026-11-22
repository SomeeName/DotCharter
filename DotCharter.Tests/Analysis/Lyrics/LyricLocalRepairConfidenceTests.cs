using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

public sealed class LyricLocalRepairConfidenceTests
{
    [Fact]
    public void LettersUnderTheFloorFlagAContradictedOnsetButDoNotMoveIt()
    {
        // Half a second of silence before the voice: too short for a stranded opening (G3).
        var result = Repair([6.5, 7.3, 7.6], [0.01, 0.01, 0.01], [7, 7.3, 7.6]);

        Assert.Equal(6.5, result[0].StartSeconds, 1e-9);
        Assert.Equal(0.01, result[0].Confidence, 1e-9);
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.LowScore | LyricReviewReason.Gap));
        Assert.False(result[0].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Fact]
    public void LettersOverTheFloorMoveAContradictedOnsetEvenWhereBlankDominates()
    {
        var result = Repair([1, 7.3, 7.6], [0.01, 0.01, 0.01], [7, 7.3, 7.6], letter: 0.3);

        Assert.InRange(result[0].StartSeconds, 7, 7.12);
        Assert.True(result[0].Confidence >= LyricAlignmentReview.LowConfidenceThreshold, $"confidence {result[0].Confidence}");
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.LowScore | LyricReviewReason.Gap | LyricReviewReason.Repaired));
    }

    [Fact]
    public void WeakerLocalEvidenceCannotMoveAVoicedNeighbourOfAnUnvoicedOutlier()
    {
        var result = Repair([1, 7.3, 7.6], [0.049, 0.049, 0.01], [7, 7.1, 7.6]);

        Assert.Equal(7.3, result[1].StartSeconds, 1e-9);
        Assert.Equal(0.049, result[1].Confidence, 1e-9);
    }

    [Fact]
    public void AWeakerCandidateCannotMoveAnOpeningAlreadyInsideTheVoice()
    {
        var result = Repair([7.2, 7.3, 7.6], [0.049, 0.01, 0.01], [7, 7.3, 7.6]);

        Assert.Equal(7.2, result[0].StartSeconds, 1e-9);
    }

    /// <summary>Letters peak at <paramref name="letter" /> under a dominant blank, 0.04 by default (under the floor).</summary>
    private static IReadOnlyList<AlignedSyllable> Repair(double[] starts, double[] confidence, double[] peaks, double letter = 0.04)
    {
        var sheet = LyricSheetParser.Parse("a-b-c");
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c"]);
        const int frames = 500;
        const int width = 4;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var symbol = Array.FindIndex(peaks, p => f * 0.02 >= p - 1e-9 && f * 0.02 < p + 0.12 - 1e-9) + 1;
            for (var column = 0; column < width; column++)
            {
                var probability = column == 0 ? symbol == 0 ? 0.997 : 0.998 - letter : column == symbol ? letter : 0.001;
                probabilities[f * width + column] = (float)Math.Log(probability);
            }
        }

        var original = starts.Select((s, i) => new AlignedSyllable(i, s, s + 0.02, confidence[i], false)).ToArray();
        var envelope = new VocalEnvelope(Enumerable.Range(0, 1000).Select(f => f is >= 700 and < 790 ? 0.5f : 0f).ToArray(), 0.01);
        return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, frames, width, 0.02),
            original, envelope, LyricAlignmentOptions.Default, TestContext.Current.CancellationToken);
    }
}
