using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Services.Editing.Quality;

/// <summary>Which existing check reported a finding; a finding two checks agree on carries both.</summary>
[Flags]
public enum QualityChecks
{
    None = 0,

    /// <summary>The Inspector's arrangement rules (<c>IArrangementValidationService</c>).</summary>
    ChartRules = 1,

    /// <summary>The export gate (<see cref="ChartExportValidator" />).</summary>
    ExportValidation = 2,

    /// <summary>The audio-verified lint (<c>IAudioChartLintService</c>), when it was run.</summary>
    AudioLint = 4
}

/// <summary>
///     Error: the export refuses the chart (an export-gate error). Important and Warning: the
///     rules' own two levels, and the export gate's warnings, which ship but are worth a look.
/// </summary>
public enum QualitySeverity
{
    Warning,
    Important,
    Error
}

/// <summary>
/// One thing the report says about the chart, normalised across the checks so a
///     screen can sort, count and jump, while the check's own record stays attached
///     (<see cref="Rule" />, <see cref="Export" />, <see cref="Audio" />) so its wording is the
///     check's, never a copy.
///     <para><c>Code</c> is the rule code (<c>ArrangementIssue.Code</c>), the export message key or
///     <c>ChartQualityReporter.AudioLintCode</c>; a merged finding keeps the rule's.
///     <c>Arrangement</c> is null for a project-level finding (no beat map, no arrangements).
///     <c>Element</c> is the note, chord, hand shape or anchor a rule named, and <c>Level</c> the
///     level holding it, for jump-to.</para>
/// </summary>
public sealed record QualityFinding(
    QualityChecks Checks,
    QualitySeverity Severity,
    string Code,
    Arrangement? Arrangement,
    ChartLevel? Level,
    double? TimeMs,
    object? Element,
    ArrangementIssue? Rule,
    ExportValidationFinding? Export,
    ChartAudioFinding? Audio)
{
    public bool CanJump => TimeMs.HasValue;

    /// <summary>The repair verb, carried by the export half of a finding; the screen
    /// runs it through <c>RocksmithFixes</c>.</summary>
    public ExportFixKind Fix => Export?.Fix ?? ExportFixKind.None;
}

/// <summary>Per arrangement: its estimated difficulty (<see cref="ChartDifficultyEstimator" />)
/// and what the report found in it.</summary>
public sealed record ArrangementQuality(
    Arrangement Arrangement,
    double Difficulty,
    int Errors,
    int Important,
    int Warnings,
    bool AudioChecked);

/// <summary>"This chart is ready to ship, except these things".</summary>
public sealed record ChartQualityReport(IReadOnlyList<QualityFinding> Findings, IReadOnlyList<ArrangementQuality> Arrangements)
{
    public int Errors => Findings.Count(f => f.Severity == QualitySeverity.Error);
    public int Important => Findings.Count(f => f.Severity == QualitySeverity.Important);
    public int Warnings => Findings.Count(f => f.Severity == QualitySeverity.Warning);

    /// <summary>Nothing would stop the export. Important findings and warnings may remain.</summary>
    public bool IsReady => Errors == 0;
}

/// <summary>
/// What an Editor on Fire export of a chart would approximate or drop, from
///     <c>ChartQualityReporter.CheckEofRoundTrip</c>. Kept apart from the report's findings: a
///     round trip to another editor neither stops the export nor counts against the verdict.
///     <see cref="Summary" /> is null when the check failed, and <see cref="Error" /> says why.
/// </summary>
public sealed record EofRoundTripCheck(EofLossSummary? Summary, string Error);
