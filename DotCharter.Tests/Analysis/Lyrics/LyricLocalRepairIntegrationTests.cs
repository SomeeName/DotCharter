using DotCharter.Analysis.Lyrics;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Analysis.Lyrics;

public sealed class LyricLocalRepairIntegrationTests
{
    [Fact]
    public void ThePublicAlignerFlagsVoiceSwallowedByTheFreeWildcardWithoutMovingAHeardStart()
    {
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "b", "c"]);
        var sheet = LyricSheetParser.Parse("a-b-c");
        const int frames = 180;
        const int width = 4;
        var probabilities = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            var time = f * 0.02;
            var symbol = time switch
            {
                >= 1 and < 1.4 => 1,
                >= 1.4 and < 1.8 => 2,
                >= 1.8 and < 2.2 => 3,
                _ => 0
            };
            for (var column = 0; column < width; column++)
                probabilities[f * width + column] = (float)Math.Log(column == symbol ? 0.97 : 0.01);
        }
        var emissions = new CtcEmissions(probabilities, frames, width, 0.02);
        var envelope = new VocalEnvelope(Enumerable.Range(0, 360)
            .Select(f => f is >= 100 and < 220 ? 0.5f : 0f).ToArray(), 0.01);

        var raw = LyricAligner.Align(sheet, vocabulary, emissions, cancellationToken: TestContext.Current.CancellationToken);
        var repaired = LyricAligner.Align(sheet, vocabulary, emissions,
            cancellationToken: TestContext.Current.CancellationToken, envelope: envelope);

        Assert.True(raw.Succeeded, raw.Failure);
        Assert.True(repaired.Succeeded, repaired.Failure);
        Assert.True(raw.Syllables[0].StartSeconds > 1.2);
        Assert.True(raw.Syllables[0].Confidence >= LyricAlignmentReview.LowConfidenceThreshold);
        Assert.False(raw.Syllables[0].ReviewReasons.HasFlag(LyricReviewReason.Gap));
        for (var i = 0; i < raw.Syllables.Count; i++) Assert.Equal(raw.Syllables[i].StartSeconds, repaired.Syllables[i].StartSeconds, 1e-9);
        Assert.True(repaired.Syllables[0].ReviewReasons.HasFlag(LyricReviewReason.Compressed | LyricReviewReason.Gap));
        Assert.False(repaired.Syllables[0].ReviewReasons.HasFlag(LyricReviewReason.Repaired));
    }
}
