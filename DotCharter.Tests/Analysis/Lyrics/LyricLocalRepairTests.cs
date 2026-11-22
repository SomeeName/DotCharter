using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

public sealed class LyricLocalRepairTests
{
    [Fact]
    public void AStrandedFirstSyllableRejoinsItsLine()
    {
        var result = Repair("a-b-c", [1, 7.3, 7.6], [7, 7.3, 7.6], [1, 2, 3], 7, 8);

        Assert.Equal(7, result[0].StartSeconds, 0.03);
        Assert.Equal(7.3, result[1].StartSeconds, 0.03);
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    [Fact]
    public void ADriftingTailIsRealignedWithinTheVoice()
    {
        var result = Repair("a-b-c-d", [1, 1.3, 2.5, 2.8], [1, 1.3, 1.6, 1.9], [1, 2, 3, 4], 1, 2.2);

        Assert.Equal(1.6, result[2].StartSeconds, 0.03);
        Assert.Equal(1.9, result[3].StartSeconds, 0.03);
    }

    [Fact]
    public void RepeatedLabelsKeepTheirMandatoryBlankAndDistinctOnsets()
    {
        var result = Repair("a-a-a", [1.46, 1.5, 1.54], [1, 1.3, 1.6], [1, 1, 1], 1, 1.9);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].StartSeconds, 0.03);
        Assert.Equal(1.3, result[1].StartSeconds, 0.03);
        Assert.Equal(1.6, result[2].StartSeconds, 0.03);
        Assert.True(result[1].StartSeconds > result[0].EndSeconds);
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.Compressed));
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.LowScore));
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }

    [Fact]
    public void ALateOpeningPairCanUseVoiceThatTheWildcardSwallowed()
    {
        var result = Repair("a-b-c", [1.2, 1.5, 1.6], [1, 1.3, 1.6], [1, 2, 3], 1, 1.9);

        Assert.Equal(1, result[0].StartSeconds, 0.03);
        Assert.Equal(1.3, result[1].StartSeconds, 0.03);
    }

    [Fact]
    public void AConfidentLateOpeningIsFlaggedButKeepsItsStart()
    {
        var result = Repair("a-b-c", [1.2, 1.5, 1.6], [1, 1.3, 1.6], [1, 2, 3], 1, 1.9, 0.9);

        Assert.Equal(1.2, result[0].StartSeconds, 1e-9);
        Assert.Equal(1.5, result[1].StartSeconds, 1e-9);
        Assert.True(result[0].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.All(result, s => Assert.False(s.ReviewReasons.HasFlag(LyricReviewReason.Repaired)));
    }

    [Fact]
    public void AConfidentTailOutsideTheVoiceIsFlaggedButKeepsItsStarts()
    {
        var result = Repair("a-b-c-d", [1, 1.3, 1.85, 2.15], [1, 1.3, 1.6, 1.9], [1, 2, 3, 4], 1, 2.08, 0.9);

        Assert.Equal(1.85, result[2].StartSeconds, 1e-9);
        Assert.Equal(2.15, result[3].StartSeconds, 1e-9);
        Assert.True(result[2].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        Assert.True(result[3].ReviewReasons.HasFlag(LyricReviewReason.Gap));
    }

    [Fact]
    public void LocalRepairPreservesTheModelsNegativeOnsetCorrections()
    {
        var options = LyricAlignmentOptions.Default with { OnsetBiasSeconds = -0.04, VowelInitialOnsetBiasSeconds = -0.02 };
        var result = Repair("a-b-c", [1.2, 1.5, 1.6], [1, 1.3, 1.6], [1, 2, 3], 1, 1.9, options: options);

        Assert.Equal(0.94, result[0].StartSeconds, 0.015);
        Assert.Equal(1.26, result[1].StartSeconds, 0.015);
    }

    [Fact]
    public void AdjacentLinesCannotReuseTheSameAcousticFramesAfterRepair()
    {
        var result = Repair("b a\na c", [1, 1.5, 2.5, 3], [1, 1.8, 1.8, 3], [2, 1, 1, 3], 1, 3.2);

        Assert.True(result[1].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
        Assert.True(result[1].EndSeconds <= result[2].StartSeconds,
            $"previous repaired end {result[1].EndSeconds} overlaps next start {result[2].StartSeconds}");
    }

    private static IReadOnlyList<AlignedSyllable> Repair(
        string text, double[] starts, double[] expected, int[] symbols, double voiceStart, double voiceEnd,
        double confidence = 0.01, LyricAlignmentOptions? options = null)
    {
        var sheet = LyricSheetParser.Parse(text);
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c", "d"]);
        const int width = 5;
        const int frames = 500;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var symbol = 0;
            for (var i = 0; i < expected.Length; i++)
                if (f * 0.02 >= expected[i] - 1e-9 && f * 0.02 < expected[i] + 0.12 - 1e-9) symbol = symbols[i];
            for (var column = 0; column < width; column++)
                probabilities[f * width + column] = (float)Math.Log(column == symbol ? 0.96 : 0.01);
        }

        var levels = Enumerable.Range(0, 1000)
            .Select(i => i * 0.01 >= voiceStart && i * 0.01 < voiceEnd ? 0.5f : 0f).ToArray();
        var aligned = starts.Select((s, i) => new AlignedSyllable(i, s, s + 0.02, confidence, false)).ToArray();
        return LyricLocalRepair.Repair(sheet, vocabulary, new CtcEmissions(probabilities, frames, width, 0.02),
            aligned, new VocalEnvelope(levels, 0.01), options ?? LyricAlignmentOptions.Default, TestContext.Current.CancellationToken);
    }
}
