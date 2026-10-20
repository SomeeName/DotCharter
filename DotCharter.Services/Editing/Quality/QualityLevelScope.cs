namespace DotCharter.Services.Editing.Quality;

/// <summary>
/// Which findings sit on an EASIER difficulty level than the hardest. On a chart with a
/// difficulty ladder (every Rocksmith import keeps the official one) the rules run on
///     every level, so most findings repeat the hardest level's on its thinned copies. The report
///     and the Inspector list them only on request, except what stops the export.
/// </summary>
public static class QualityLevelScope
{
    /// <summary>True when <paramref name="level" /> is a level of <paramref name="arrangement" /> other
    /// than its hardest. A finding with no level is about the whole arrangement and never is.</summary>
    public static bool IsEasierLevel(Arrangement? arrangement, ChartLevel? level) =>
        arrangement != null && level != null && !ReferenceEquals(level, ChartLevels.Hardest(arrangement));

    /// <summary>A report finding the default view leaves out: on an easier level, and not blocking.</summary>
    public static bool IsOnEasierLevelAndNotBlocking(QualityFinding finding) =>
        finding.Severity != QualitySeverity.Error && IsEasierLevel(finding.Arrangement, finding.Level);
}
