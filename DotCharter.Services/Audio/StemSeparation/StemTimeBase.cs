namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>Rejects stems from another version of a recording before using their timestamps.</summary>
internal static class StemTimeBase
{
    // Allows codec padding and sample-rate rounding, but not an audio splice.
    private const double DurationToleranceSeconds = 0.1;

    public static bool Matches(double stemSeconds, double mixSeconds) =>
        double.IsFinite(stemSeconds) && double.IsFinite(mixSeconds) && stemSeconds > 0 && mixSeconds > 0 &&
        Math.Max(stemSeconds, mixSeconds) <= Math.Min(stemSeconds, mixSeconds) + DurationToleranceSeconds;

    public static string DescribeMismatch(string stemPath, double stemSeconds, string mixPath, double mixSeconds) =>
        FormattableString.Invariant($"Stem '{stemPath}' ({stemSeconds:0.000} s) differs in duration from mix '{mixPath}' ({mixSeconds:0.000} s); using the mix instead.");

    public static string DescribeOffset(string stemPath, double offset) =>
        FormattableString.Invariant($"Stem '{stemPath}' has non-zero project offset {offset:0.###}; using the mix instead.");
}
