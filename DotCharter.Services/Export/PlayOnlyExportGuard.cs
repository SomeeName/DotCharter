namespace DotCharter.Services.Export;

/// <summary>
/// The ONE check every exporter makes before it writes chart content:
///     a chart stamped play-only
///     (<see cref="ChartProvenance.PlayOnly" />, set by the source that imported it)
///     is never written out.
///     <para>
///         <b>Every exporter calls it at its own entry point</b>, not only the
///         menu that launches it, so a second caller (a save companion, a script,
///         a future button) cannot skip it. <c>Export/PlayOnlyExportGuardCoverageTests</c>
///         lists the guarded entry points and fails on a new exporter that is not
///         among them.
///     </para>
///     <para>
///         A project is play-only when the PROJECT is play-only (its beats, lyrics
///         and showlights came from the package) or when ANY arrangement in it is.
///     </para>
/// </summary>
public static class PlayOnlyExportGuard
{
    /// <summary>The localized message a refusal shows (all three resx files).</summary>
    public const string MessageKey = "Export_PlayOnly";

    public static bool IsPlayOnly(ChartProject? project) =>
        project != null &&
        (project.Provenance != ChartProvenance.Unrestricted ||
         project.Arrangements.Any(a => a.Provenance != ChartProvenance.Unrestricted));

    public static bool IsPlayOnly(Arrangement? arrangement) =>
        arrangement != null && arrangement.Provenance != ChartProvenance.Unrestricted;

    /// <summary>Throws <see cref="PlayOnlyContentException" /> for a play-only project.</summary>
    public static void ThrowIfPlayOnly(ChartProject? project, string exporter)
    {
        if (IsPlayOnly(project)) throw new PlayOnlyContentException(exporter);
    }

    /// <summary>Throws <see cref="PlayOnlyContentException" /> for a play-only arrangement.</summary>
    public static void ThrowIfPlayOnly(Arrangement? arrangement, string exporter)
    {
        if (IsPlayOnly(arrangement)) throw new PlayOnlyContentException(exporter);
    }
}

/// <summary>
/// An exporter refused play-only content. The UI shows
///     <see cref="PlayOnlyExportGuard.MessageKey" />; <see cref="Exception.Message" />
///     is the English log line.
/// </summary>
public sealed class PlayOnlyContentException(string exporter)
    : InvalidOperationException(
        $"{exporter}: this chart is play-only; it cannot be exported.")
{
    public string Exporter { get; } = exporter;
}
