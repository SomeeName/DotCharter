namespace DotCharter.Analysis.Tuning;

/// <summary>
///     How far a recording's reference pitch sits from A440.
/// </summary>
/// <param name="Cents">The offset folded into -50..+50 cents around the nearest equal-tempered semitone.</param>
/// <param name="Confidence">0 (no steady pitch: silence, noise) to 1 (every partial agrees).</param>
/// <param name="PeakCount">How many spectral peaks voted.</param>
/// <param name="AnalyzedSeconds">How much audio the peaks came from.</param>
public sealed record ReferencePitchEstimate(double Cents, double Confidence, int PeakCount, double AnalyzedSeconds)
{
    public static readonly ReferencePitchEstimate None = new(0, 0, 0, 0);

    /// <summary>The A4 the offset implies, in hertz.</summary>
    public double ReferenceHz => 440.0 * Math.Pow(2, Cents / 1200.0);
}
