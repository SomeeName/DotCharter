using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

public sealed class LyricSyllableEndsTests
{
    [Theory]
    [InlineData(false, 1.1)]
    [InlineData(true, 1.97)]
    public void ContinuousVoiceUnderTheCapCanHoldALineFinalWithoutSpanningOrdinaryWords(bool nextLine, double expected)
    {
        var envelope = Envelope(4, (1, 3, 0.5f));

        var ends = Ends([new(1, 1.1, 0, false), new(2, 2.1, nextLine ? 1 : 0, false)], envelope);

        Assert.Equal(expected, ends[0], 0.015);
    }

    [Fact]
    public void ALineFinalHoldPastTheCapFollowsTheVoice()
    {
        var envelope = Envelope(10, (1, 8, 0.5f));

        var result = LyricSyllableEnds.ComputeWithReview([new(1, 1.1, 0, false)],
            (_, next) => next - 0.03, 4, envelope, VocalEndOptions.Default);

        Assert.Equal(8, result.Ends[0], 0.015);
        Assert.True(result.CapHits[0]);
    }

    [Fact]
    public void AQuietHeldSyllableUsesItsOwnLevelWithinALoudLine()
    {
        var envelope = Envelope(10, (0, 1, 0.5f), (1.2, 8, 0.04f));

        var ends = Ends([new(0, 0.1, 0, true), new(1.2, 1.3, 0, false)], envelope);

        Assert.Equal(8, ends[1], 0.015);
    }

    [Fact]
    public void AQuietInLineSyllableDoesNotFollowResidueToTheNextOnset()
    {
        var envelope = Envelope(5, (0, 1, 0.5f), (1.2, 1.7, 0.04f), (1.7, 4, 0.015f), (4, 4.5, 0.5f));

        var ends = Ends([new(0, 0.1, 0, true), new(1.2, 1.3, 0, true), new(4, 4.1, 0, false)], envelope);

        Assert.Equal(1.3, ends[1], 0.015);
    }

    [Fact]
    public void ALongHoldStopsAtTheBreathBeforeTheNextLine()
    {
        var envelope = Envelope(12, (1, 7, 0.5f), (8, 10, 0.5f));

        var ends = Ends([new(1, 1.1, 0, false), new(8, 8.1, 1, false)], envelope);

        Assert.Equal(7, ends[0], 0.015);
    }

    [Fact]
    public void ContinuousVoiceCannotExtendPastTheNextOnset()
    {
        var envelope = Envelope(12, (1, 10, 0.5f));

        var ends = Ends([new(1, 1.1, 0, false), new(8, 8.1, 1, false)], envelope);

        Assert.Equal(7.97, ends[0], 0.015);
    }

    [Fact]
    public void WithoutVoiceEvidenceTheCapStillBoundsASpan()
    {
        var ends = Ends([new(1, 1.1, 0, true), new(8, 8.1, 1, false)], null);

        Assert.Equal(5, ends[0], 1e-9);
        Assert.Equal(8.1, ends[1], 1e-9);
    }

    private static double[] Ends(LyricEndSyllable[] syllables, VocalEnvelope? envelope) =>
        LyricSyllableEnds.Compute(syllables, (_, next) => next - 0.03, 4, envelope, VocalEndOptions.Default);

    private static VocalEnvelope Envelope(double duration, params (double Start, double End, float Level)[] spans)
    {
        var levels = new float[(int)(duration * 100)];
        for (var i = 0; i < levels.Length; i++)
        {
            foreach (var span in spans)
                if (i * 0.01 >= span.Start && i * 0.01 < span.End) levels[i] = span.Level;
        }

        return new VocalEnvelope(levels, 0.01);
    }
}
