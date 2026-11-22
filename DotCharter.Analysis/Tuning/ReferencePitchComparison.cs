namespace DotCharter.Analysis.Tuning;

/// <summary>
///     A measured reference against the chart's authored <c>CentOffset</c>. A reading only
///     sees the offset within ±50 cents of a semitone, so the authored offset's whole semitones —
///     the −1200 of an A220 chart, a tuning a semitone off — are kept and only the residual moves:
///     the suggestion is the authored value plus the shortest way round the semitone circle to the
///     measured residual. <see cref="Semitones" /> reports the suggestion's whole semitones apart.
/// </summary>
/// <param name="AuthoredCentOffset">The chart's offset as authored.</param>
/// <param name="MeasuredCents">The measured residual, −50..+50.</param>
/// <param name="DifferenceCents">Measured minus the authored residual, −50..+50.</param>
/// <param name="SuggestedCentOffset">The authored offset corrected to the measurement, whole cents.</param>
/// <param name="Semitones">The suggestion's whole semitones (a tuning, not an A4 shift).</param>
/// <param name="ShouldOffer">Confident and further apart than the threshold.</param>
public readonly record struct ReferencePitchComparison(double AuthoredCentOffset, double MeasuredCents, double DifferenceCents,
    double SuggestedCentOffset, int Semitones, bool ShouldOffer)
{
    /// <summary>A difference at or below this is left alone.</summary>
    public const double DefaultThresholdCents = 10.0;

    public static ReferencePitchComparison Compare(double authoredCentOffset, ReferencePitchEstimate estimate,
        double thresholdCents = DefaultThresholdCents, double minConfidence = ReferencePitchEstimator.DefaultMinConfidence)
    {
        var authoredResidual = ReferencePitchEstimator.FoldToSemitone(authoredCentOffset);
        var difference = ReferencePitchEstimator.FoldToSemitone(estimate.Cents - authoredResidual);
        var suggested = Math.Round(authoredCentOffset + difference, MidpointRounding.AwayFromZero);
        var semitones = (int)Math.Round(suggested / 100.0, MidpointRounding.AwayFromZero);
        var offer = estimate.Confidence >= minConfidence && Math.Abs(difference) > thresholdCents;
        return new ReferencePitchComparison(authoredCentOffset, estimate.Cents, difference, suggested, semitones, offer);
    }
}
