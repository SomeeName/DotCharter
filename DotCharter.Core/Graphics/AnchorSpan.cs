namespace DotCharter.Core.Graphics;

/// <summary>The finite anchor span shared by timeline projection and pointer hit testing.</summary>
public static class AnchorSpan
{
    public const double TailMaxMs = 5000;

    public static double EndTimeMs(double timeMs, double? nextTimeMs, double currentTimeMs) =>
        nextTimeMs ?? Math.Max(currentTimeMs + HighwaySpeedPolicy.MaxHorizonMs, timeMs + TailMaxMs);
}
