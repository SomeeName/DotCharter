namespace DotCharter.Services.Export.Rocksmith;

/// <summary>RS XML and SNG strengths are semitones; three whole steps are six semitones.</summary>
internal static class RsBendStrength
{
    public const double Maximum = 6.0;

    public static double Normalize(double step) => Math.Clamp(step, 0.0, Maximum);
}
