namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Shared utilities for parsing RSCP chart files
/// </summary>
public static class RscpParsingUtilities
{
    // The same order of headroom as TimelineMath's imported-ms extrapolation.
    // Keep hostile file positions away from integer-index arithmetic downstream.
    internal const int MaxImportedBeatId = 1_000_000;

    public static FractionalPosition ParseFraction(string? pVal)
    {
        if (TryParseFraction(pVal, out var result)) return result;
        return FractionalPosition.Zero;
    }

    internal static bool TryParseFraction(string? value, out FractionalPosition result)
    {
        if (FractionalPosition.TryParse(value, out result) &&
            result.DoubleValue is >= -MaxImportedBeatId and <= MaxImportedBeatId)
        {
            return true;
        }

        result = FractionalPosition.Zero;
        return false;
    }

    /// <summary>Tolerant parse of the v0.29 section-status attribute (`ss`,
    /// lowercase enum name). Absent, blank or UNKNOWN values read
    /// <see cref="Core.Domain.Events.SectionStatus.None"/> — a newer file with
    /// a status this build doesn't know must load, not throw.</summary>
    public static Core.Domain.Events.SectionStatus ParseSectionStatus(string? value)
        => Enum.TryParse<Core.Domain.Events.SectionStatus>(value, ignoreCase: true, out var status) &&
           Enum.IsDefined(status) // TryParse admits numeric strings — "7" must read None, not (SectionStatus)7
            ? status
            : Core.Domain.Events.SectionStatus.None;
}
