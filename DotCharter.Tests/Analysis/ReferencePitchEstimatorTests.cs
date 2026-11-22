using DotCharter.Analysis.Tuning;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The A440 reference estimator (#821) on synthetic signals: sines and harmonic notes tuned
///     off A440 by a known amount come back within ±3 cents, and silence or noise report a
///     confidence too low to act on.
/// </summary>
public sealed class ReferencePitchEstimatorTests
{
    private const int Rate = 44100;
    private const double Tolerance = 3.0;

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(-30.0)]
    [InlineData(45.0)]
    public void ASine_OffA440_IsMeasuredWithinThreeCents(double cents)
    {
        var signal = Notes(cents, harmonics: 1);

        var estimate = ReferencePitchEstimator.Estimate(signal, Rate, TestContext.Current.CancellationToken);

        Assert.InRange(estimate.Cents, cents - Tolerance, cents + Tolerance);
        Assert.True(estimate.Confidence >= ReferencePitchEstimator.DefaultMinConfidence, $"confidence {estimate.Confidence}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(-30.0)]
    [InlineData(45.0)]
    public void HarmonicNotes_OffA440_AreMeasuredWithinThreeCents(double cents)
    {
        var signal = Notes(cents, harmonics: 8);

        var estimate = ReferencePitchEstimator.Estimate(signal, Rate, TestContext.Current.CancellationToken);

        Assert.InRange(estimate.Cents, cents - Tolerance, cents + Tolerance);
        Assert.True(estimate.Confidence >= ReferencePitchEstimator.DefaultMinConfidence, $"confidence {estimate.Confidence}");
    }

    [Fact]
    public void Silence_HasNoConfidence()
    {
        var estimate = ReferencePitchEstimator.Estimate(new float[Rate * 5], Rate, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, estimate.Confidence);
    }

    [Fact]
    public void WhiteNoise_HasLowConfidence()
    {
        var random = new Random(821);
        var noise = new float[Rate * 10];
        for (var i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() * 0.6 - 0.3);

        var estimate = ReferencePitchEstimator.Estimate(noise, Rate, TestContext.Current.CancellationToken);

        Assert.True(estimate.Confidence < ReferencePitchEstimator.DefaultMinConfidence, $"confidence {estimate.Confidence}");
    }

    [Theory]
    [InlineData(0.0, 15.0, 15.0, 0)]
    [InlineData(0.0, -48.0, -48.0, 0)]
    [InlineData(-1200.0, 15.0, -1185.0, -12)]
    [InlineData(40.0, -45.0, 55.0, 1)]
    [InlineData(100.0, -20.0, 80.0, 1)]
    public void TheCorrection_KeepsTheChartsSemitones_AndMovesOnlyTheResidual(double authored, double measured,
        double suggested, int semitones)
    {
        var comparison = ReferencePitchComparison.Compare(authored, new ReferencePitchEstimate(measured, 1.0, 500, 60));

        Assert.Equal(suggested, comparison.SuggestedCentOffset, 1);
        Assert.Equal(semitones, comparison.Semitones);
        Assert.True(comparison.ShouldOffer);
    }

    [Fact]
    public void ASmallDifference_OrALowConfidence_IsNotOffered()
    {
        Assert.False(ReferencePitchComparison.Compare(0, new ReferencePitchEstimate(6, 1.0, 500, 60)).ShouldOffer);
        Assert.False(ReferencePitchComparison.Compare(0, new ReferencePitchEstimate(30, 0.1, 500, 60)).ShouldOffer);
        Assert.False(ReferencePitchComparison.Compare(-1200, new ReferencePitchEstimate(-4, 1.0, 500, 60)).ShouldOffer);
    }

    /// <summary>Twelve seconds of notes across the guitar range, each detuned by <paramref name="cents" />.</summary>
    private static float[] Notes(double cents, int harmonics)
    {
        int[] midi = [40, 45, 52, 57, 59, 62, 64, 67, 69, 71, 74, 76];
        var noteSamples = Rate;
        var signal = new float[midi.Length * noteSamples];
        for (var n = 0; n < midi.Length; n++)
        {
            var f0 = 440.0 * Math.Pow(2, (midi[n] - 69 + cents / 100.0) / 12.0);
            for (var i = 0; i < noteSamples; i++)
            {
                var t = (double)i / Rate;
                var envelope = Math.Exp(-1.5 * t);
                var sample = 0.0;
                for (var h = 1; h <= harmonics; h++) sample += Math.Sin(2 * Math.PI * f0 * h * t) / h;
                signal[n * noteSamples + i] = (float)(0.3 * envelope * sample);
            }
        }

        return signal;
    }
}
