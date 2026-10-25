using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The vocal envelope and the rule that moves a syllable's end with the
///     voice. Pure, on synthetic envelopes: a stretch of level 0.5 is a sung
///     note, a stretch of 0 is a breath or a rest.
/// </summary>
public sealed class VocalEndRefinerTests
{
    private const double Hop = 0.010;
    private const float Loud = 0.5f;

    private static readonly VocalEndOptions Options = VocalEndOptions.Default;

    /// <summary>A 10 ms envelope: <c>Loud</c> inside each voiced span, silent elsewhere.</summary>
    private static VocalEnvelope Envelope(double seconds, params (double From, double To)[] voiced)
    {
        var levels = new float[(int)Math.Round(seconds / Hop)];
        for (var k = 0; k < levels.Length; k++)
        {
            var t = k * Hop;
            if (voiced.Any(v => t >= v.From - 1e-9 && t < v.To - 1e-9)) levels[k] = Loud;
        }

        return new VocalEnvelope(levels, Hop);
    }

    /// <summary>Voice at <c>Loud</c> up to <paramref name="holdUntil" />, a linear fade to zero over <paramref name="fade" />.</summary>
    private static VocalEnvelope Fading(double seconds, double from, double holdUntil, double fade)
    {
        var levels = new float[(int)Math.Round(seconds / Hop)];
        for (var k = 0; k < levels.Length; k++)
        {
            var t = k * Hop;
            if (t < from) continue;
            levels[k] = t < holdUntil ? Loud : (float)Math.Max(0.0, Loud * (1 - (t - holdUntil) / fade));
        }

        return new VocalEnvelope(levels, Hop);
    }

    private static double Refine(
        VocalEnvelope envelope, double floor, double limit, bool bridge, VocalEndOptions? options = null)
        => VocalEndRefiner.Refine(envelope, Loud, floor, limit, bridge, options ?? Options);

    [Fact]
    public void ASpanOverASilentGapStopsAtTheGap()
    {
        var envelope = Envelope(4.0, (1.0, 1.5), (1.8, 3.0));

        Assert.Equal(1.5, Refine(envelope, floor: 1.1, limit: 1.97, bridge: true), 0.015);
    }

    [Fact]
    public void ASpanOverContinuousVoiceRunsToTheLimit()
    {
        var envelope = Envelope(4.0, (1.0, 3.0));

        Assert.Equal(1.97, Refine(envelope, floor: 1.1, limit: 1.97, bridge: true), 1e-9);
    }

    [Fact]
    public void ADipShorterThanTheMinimumSilenceIsBridged()
    {
        var envelope = Envelope(4.0, (1.0, 1.5), (1.53, 3.0));

        Assert.Equal(1.97, Refine(envelope, floor: 1.1, limit: 1.97, bridge: true), 1e-9);
    }

    [Fact]
    public void ASustainedPhraseFinalVowelExtendsToTheFadeOut()
    {
        // Held at 0.5 until 2.0 s, then a linear fade to zero by 2.6 s. The level
        // crosses the 0.15 silence threshold (30% of 0.5) at 2.42 s.
        var envelope = Fading(5.0, from: 1.0, holdUntil: 2.0, fade: 0.6);

        var end = Refine(envelope, floor: 1.1, limit: 4.5, bridge: false);

        Assert.Equal(2.42, end, 0.03);
    }

    [Fact]
    public void APhraseFinalSyllableOverSilenceKeepsItsCtcEnd()
    {
        var envelope = Envelope(5.0, (1.0, 1.1));

        Assert.Equal(1.1, Refine(envelope, floor: 1.1, limit: 4.5, bridge: false), 1e-9);
    }

    [Fact]
    public void AVoiceThatNeverFadesBeforeTheLimitKeepsTheFloorWhenTheBoundaryIsNotSpanned()
    {
        var envelope = Envelope(5.0, (1.0, 4.0));

        Assert.Equal(1.1, Refine(envelope, floor: 1.1, limit: 2.0, bridge: false), 1e-9);
    }

    [Fact]
    public void TheEndIsNeverEarlierThanTheFloor()
    {
        // Silence starts at 1.5 s, but the syllable is already heard until 1.7 s.
        var envelope = Envelope(4.0, (1.0, 1.5), (2.5, 3.0));

        Assert.Equal(1.7, Refine(envelope, floor: 1.7, limit: 2.4, bridge: true), 1e-9);
    }

    [Fact]
    public void TheEndIsNeverLaterThanTheLimit()
    {
        // The voice fades only at 3.5 s, past the limit.
        var envelope = Envelope(5.0, (1.0, 3.5));

        var spanned = Refine(envelope, floor: 1.1, limit: 2.0, bridge: true);

        Assert.True(spanned <= 2.0 + 1e-9);
        Assert.Equal(2.0, spanned, 1e-9);
    }

    [Fact]
    public void SilenceIsJudgedAgainstThePhrasesOwnLoudness()
    {
        // A quiet passage (0.04) is voice when the phrase's reference is 0.04
        // and silence when it is 0.5, with the same envelope.
        var levels = Enumerable.Repeat(0.04f, 400).ToArray();
        var envelope = new VocalEnvelope(levels, Hop);

        Assert.Equal(3.0, VocalEndRefiner.Refine(envelope, 0.04f, 1.0, 3.0, true, Options), 1e-9);
        Assert.Equal(1.0, VocalEndRefiner.Refine(envelope, 0.50f, 1.0, 3.0, true, Options), 0.015);
    }

    [Fact]
    public void AnEmptyStretchIsNotMistakenForVoiceByTheRelativeThreshold()
    {
        // The reference is zero (nothing sang in the phrase): the absolute floor
        // alone decides, and residue below it is silence.
        var levels = Enumerable.Repeat(0.001f, 400).ToArray();
        var envelope = new VocalEnvelope(levels, Hop);

        Assert.Equal(1.0, VocalEndRefiner.Refine(envelope, 0f, 1.0, 3.0, true, Options), 0.015);
    }

    [Fact]
    public void AnEnvelopeOfTheRightLengthIsBuiltFromSamples()
    {
        var samples = new float[16000 * 2];
        for (var n = 0; n < 16000; n++) samples[n] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * n / 16000.0));

        var envelope = VocalEnvelope.FromSamples(samples, 16000);

        Assert.Equal(200, envelope.FrameCount);
        Assert.Equal(0.010, envelope.HopSeconds, 1e-9);
        Assert.Equal(0.5 / Math.Sqrt(2), envelope.Level(50), 0.03);
        Assert.Equal(0.0, envelope.Level(150), 1e-6);
    }

    [Fact]
    public void TheReferenceLevelIsAHighPercentileOfTheStretch()
    {
        var levels = new float[100];
        Array.Fill(levels, 0.4f);
        levels[10] = 5.0f; // a click
        var envelope = new VocalEnvelope(levels, Hop);

        Assert.Equal(0.4f, envelope.ReferenceLevel(0.0, 0.99, 0.9), 1e-6);
        Assert.Equal(0f, new VocalEnvelope([], Hop).ReferenceLevel(0.0, 1.0, 0.9));
    }

    [Fact]
    public void TheThresholdsAreInjected()
    {
        var envelope = Envelope(4.0, (1.0, 1.5), (1.54, 3.0));
        var strict = Options with { MinSilenceSeconds = 0.03 };

        // The 40 ms dip bridges at the default 50 ms and splits at 30 ms.
        Assert.Equal(1.97, Refine(envelope, floor: 1.1, limit: 1.97, bridge: true), 1e-9);
        Assert.Equal(1.5, Refine(envelope, floor: 1.1, limit: 1.97, bridge: true, strict), 0.015);
    }
}
