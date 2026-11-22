using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     #868: an aligned onset moves to the vocal stem's clear onset near it, and only then.
///     The fixtures are synthetic sung notes (a harmonic tone, optionally behind a short
///     noise burst for an unvoiced consonant) on a 16 kHz stem.
/// </summary>
public sealed class LyricOnsetSnapTests
{
    private const int Rate = 16000;

    [Theory]
    [InlineData(SyllableOnsetClass.Vowel)]
    [InlineData(SyllableOnsetClass.Voiced)]
    [InlineData(SyllableOnsetClass.Unvoiced)]
    public void AnOnsetFortyMillisecondsLate_SnapsBackToTheSungOnset(SyllableOnsetClass onsetClass)
    {
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 2.0, onsetClass == SyllableOnsetClass.Unvoiced), Rate);
        var aligned = new[] { Syllable(0, 1.04, 1.8) };

        var snapped = LyricOnsetSnap.Apply(aligned, [onsetClass], features);

        Assert.InRange(snapped[0].StartSeconds, 0.985, 1.015);
        Assert.Equal(1.8, snapped[0].EndSeconds);
    }

    [Fact]
    public void AnOnsetFortyMillisecondsEarly_SnapsForwardToTheSungOnset()
    {
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 2.0, consonant: false), Rate);

        var snapped = LyricOnsetSnap.Apply([Syllable(0, 0.96, 1.8)], [SyllableOnsetClass.Vowel], features);

        Assert.InRange(snapped[0].StartSeconds, 0.985, 1.015);
    }

    [Fact]
    public void AWordWithNoClearPeak_StaysPut()
    {
        // The note has been sounding for a second already: nothing starts near 2.0 s.
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 3.0, consonant: false), Rate);

        var snapped = LyricOnsetSnap.Apply([Syllable(0, 2.0, 2.5)], [SyllableOnsetClass.Vowel], features);

        Assert.Equal(2.0, snapped[0].StartSeconds);
    }

    [Fact]
    public void SteadyNoiseWithNoStandoutPeak_MovesNothing()
    {
        // Loud bleed: every frame has flux well above the absolute floor, none stands out.
        var noise = new Random(868);
        var samples = new float[Rate * 3];
        for (var n = 0; n < samples.Length; n++) samples[n] = (float)(0.3 * (noise.NextDouble() * 2 - 1));
        var features = VocalOnsetFeatures.FromSamples(samples, Rate);

        var snapped = LyricOnsetSnap.Apply([Syllable(0, 1.5, 2.0)],
            [SyllableOnsetClass.Unvoiced], features);

        Assert.Equal(1.5, snapped[0].StartSeconds);
    }

    [Fact]
    public void Silence_MovesNothing()
    {
        var features = VocalOnsetFeatures.FromSamples(new float[Rate * 3], Rate);

        var snapped = LyricOnsetSnap.Apply([Syllable(0, 1.04, 1.8)], [SyllableOnsetClass.Vowel], features);

        Assert.Equal(1.04, snapped[0].StartSeconds);
    }

    [Fact]
    public void AnOnsetNeverCrossesOrCrowdsThePreviousOne()
    {
        // The true onset (1.0 s) is only 20 ms after the previous syllable's start: moving
        // there would leave that syllable shorter than the minimum, so nothing moves.
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 2.0, consonant: false), Rate);
        var aligned = new[] { Syllable(0, 0.98, 1.03), Syllable(1, 1.04, 1.8) };

        var snapped = LyricOnsetSnap.Apply(aligned, [SyllableOnsetClass.Vowel, SyllableOnsetClass.Vowel], features);

        Assert.Equal(0.98, snapped[0].StartSeconds);
        Assert.Equal(1.04, snapped[1].StartSeconds);
        Assert.True(snapped[0].StartSeconds + 0.05 <= snapped[1].StartSeconds);
    }

    [Fact]
    public void AnOnsetMovedEarlier_EndsThePreviousSyllableThere()
    {
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 2.0, consonant: false), Rate);
        var aligned = new[] { Syllable(0, 0.5, 1.04), Syllable(1, 1.04, 1.8) };

        var snapped = LyricOnsetSnap.Apply(aligned, [SyllableOnsetClass.Vowel, SyllableOnsetClass.Vowel], features);

        Assert.InRange(snapped[1].StartSeconds, 0.985, 1.015);
        Assert.Equal(snapped[1].StartSeconds, snapped[0].EndSeconds);
        Assert.True(snapped[0].EndSeconds > snapped[0].StartSeconds);
    }

    [Fact]
    public void AnInterpolatedSyllable_IsLeftAlone()
    {
        var features = VocalOnsetFeatures.FromSamples(Note(1.0, 2.0, consonant: false), Rate);

        var snapped = LyricOnsetSnap.Apply([Syllable(0, 1.04, 1.8) with { IsInterpolated = true }],
            [SyllableOnsetClass.Vowel], features);

        Assert.Equal(1.04, snapped[0].StartSeconds);
    }

    [Fact]
    public void FeaturesReadOnlyNearTheOnsets_MatchTheWholeSongsThere()
    {
        var samples = Note(1.0, 2.0, consonant: true);
        var whole = VocalOnsetFeatures.FromSamples(samples, Rate);
        var near = VocalOnsetFeatures.FromSamples(samples, Rate, [1.04], 0.3);

        for (var k = whole.FrameAt(0.74); k <= whole.FrameAt(1.34); k++)
        {
            Assert.Equal(whole.Flux(k), near.Flux(k));
            Assert.Equal(whole.ConsonantFlux(k), near.ConsonantFlux(k));
            Assert.Equal(whole.VoicingRise(k), near.VoicingRise(k));
        }

        // An onset out of reach is not read at all.
        var elsewhere = VocalOnsetFeatures.FromSamples(samples, Rate, [2.5], 0.3);
        Assert.Equal(0f, elsewhere.ConsonantFlux(elsewhere.FrameAt(0.99)));
        Assert.True(whole.ConsonantFlux(whole.FrameAt(0.99)) > 10f);
    }

    [Fact]
    public void RomajiIsLeftToTheAligner_OtherLyricsAreSnapped()
    {
        Assert.False(LyricOnsetSnap.AppliesTo(LyricSheetParser.Parse(
            "ki-mi no na-ma-e wo yo-bu ko-e ga\nso-ra ni hi-bi-ku yo-ru no u-mi de\nwa-ta-shi wa ma-da ko-ko ni i-ru")));
        Assert.True(LyricOnsetSnap.AppliesTo(LyricSheetParser.Parse(
            "the night is long and the road goes on\nwe walk a-lone un-til the dawn")));
    }

    private static AlignedSyllable Syllable(int index, double start, double end) => new(index, start, end, 0.9, false);

    /// <summary>A sung 220 Hz note from <paramref name="from" /> to <paramref name="to" /> in 3 s of stem.</summary>
    private static float[] Note(double from, double to, bool consonant)
    {
        var samples = new float[Rate * 3];
        var noise = new Random(868);
        for (var n = (int)(from * Rate); n < (int)(to * Rate); n++)
        {
            var t = (double)n / Rate;
            var tone = 0.0;
            for (var h = 1; h <= 8; h++) tone += Math.Sin(2 * Math.PI * 220 * h * t) / h;
            samples[n] = (float)(0.15 * tone);
            if (consonant && t < from + 0.04) samples[n] = (float)(0.3 * (noise.NextDouble() * 2 - 1));
        }

        return samples;
    }
}
