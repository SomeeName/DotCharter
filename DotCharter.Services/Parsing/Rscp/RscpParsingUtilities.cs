namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Shared utilities for parsing RSCP chart files
/// </summary>
public static class RscpParsingUtilities
{
    public static FractionalPosition ParseFraction(string? pVal)
    {
        if (FractionalPosition.TryParse(pVal, out var result)) return result;
        return FractionalPosition.Zero;
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
