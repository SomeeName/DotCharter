namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
/// The <c>.dcproj</c> spelling of <see cref="ChartProvenance" />: a root
///     <c>&lt;dcProvenance&gt;</c> for the project and a trailing
///     <c>&lt;dcArrangementProvenance&gt;</c> per arrangement, each absent when
///     unrestricted so every other chart stays byte-identical.
///     <para>
///         <b>An unrecognised value reads as PLAY-ONLY, not unrestricted.</b> A
///         newer build may add a provenance this one does not know; reading it as
///         "export freely" would turn a downgrade into a way round the guard.
///         Only a missing element means unrestricted. That includes the spelling
///         earlier builds wrote (<see cref="LegacyPlayOnly" />), which therefore
///         still loads as play-only; new files write <see cref="PlayOnly" />.
///     </para>
/// </summary>
internal static class RscpProvenance
{
    public const string ProjectElement = "dcProvenance";
    public const string ArrangementElement = "dcArrangementProvenance";

    public const string PlayOnly = "playOnly";

    /// <summary>The value builds before the neutral naming wrote. Read, never written.</summary>
    public const string LegacyPlayOnly = "officialRocksmith";

    public static string Format(ChartProvenance provenance) => provenance switch
    {
        ChartProvenance.PlayOnly => PlayOnly,
        _ => provenance.ToString()
    };

    public static ChartProvenance Parse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? ChartProvenance.Unrestricted : ChartProvenance.PlayOnly;
}
