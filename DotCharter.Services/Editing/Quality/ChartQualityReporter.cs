using DotCharter.Services.Editing.Linting;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Services.Editing.Quality;

/// <summary>
/// One verdict over every existing check (the model behind the "Ready to ship?"
///     report). It RUNS the checks and normalises what they say; it holds no rule of its own:
///     <list type="bullet">
///         <item>the arrangement rules, over every arrangement and level;</item>
///         <item>the export gate, <see cref="ChartExportValidator" />;</item>
///         <item>the audio-verified lint, when the caller ran it (it decodes the song, so it is
///         passed in rather than run here);</item>
///         <item>the difficulty estimate, per arrangement (a summary, not a finding);</item>
///     </list>
///     <para>Apart from the verdict, <see cref="CheckEofRoundTrip" /> says what an Editor on Fire
/// export would approximate or drop. It is a separate call because it is the
///     expensive one (it writes the whole <c>.eof</c> in memory): the report runs it only when the
/// user asks, on a snapshot and off the UI thread.</para>
///     <para>Two checks sometimes report the SAME fault (a fret above 24 is both a rule and an
///     export error). <see cref="SameFault" /> names those pairs, and a pair at the same time in the
///     same arrangement is ONE finding carrying both records: the rule's element for jump-to and
///     the export's Fix.</para>
///     <para>The project is read as given: a caller that validates for export assembles it first
///     (<c>IProjectDocumentService.AssembleProject</c>), as the export readiness check does.</para>
/// </summary>
public sealed class ChartQualityReporter(IArrangementValidationService rules)
{
    /// <summary>The code of an audio-lint finding: a charted pitch the audio does not confirm.</summary>
    public const string AudioLintCode = "AudioLint.PitchNotConfirmed";

    /// <summary>Export keys whose fault a rule also reports, and that rule.</summary>
    internal static readonly IReadOnlyDictionary<string, ArrangementIssueType> SameFault =
        new Dictionary<string, ArrangementIssueType>(StringComparer.Ordinal)
        {
            [ChartExportValidator.KeyFretOutOfRange] = ArrangementIssueType.FretNumberMoreThan24,
            [ChartExportValidator.KeyNoteBelowCapo] = ArrangementIssueType.NoteFretBelowCapo,
            [ChartExportValidator.KeyOverlappingNotes] = ArrangementIssueType.OverlappingNotes,
            [ChartExportValidator.KeyTooManyPhrases] = ArrangementIssueType.MoreThan100Phrases,
            [ChartExportValidator.KeyFirstNoteTooEarly] = ArrangementIssueType.FirstNoteTooEarly,
            [ChartExportValidator.KeyEmptyPhrase] = ArrangementIssueType.EmptyPhrase,
            [ChartExportValidator.KeyToneNotDefined] = ArrangementIssueType.ToneChangeWithoutDefinition,
            [ChartExportValidator.KeyToneWithoutSlot] = ArrangementIssueType.ToneChangeWithoutMatchingTone
        };

    /// <summary>How far apart two checks may place the same fault and still be one finding.</summary>
    private const double SameTimeMs = 1.0;

    public ChartQualityReport Build(ChartProject project, double? audioLengthMs = null,
        IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>? audio = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var findings = new List<QualityFinding>();
        var exportFindings = new ChartExportValidator().Validate(project);
        var merged = new bool[exportFindings.Count];

        foreach (var arrangement in project.Arrangements)
        {
            Dictionary<object, ChartLevel>? levels = null;
            var uniqueName = Named(project, arrangement.Name) == arrangement;
            foreach (var issue in rules.Validate(arrangement, project.Beats, audioLengthMs, project))
            {
                var element = (object?)issue.Note ?? (object?)issue.Chord ?? (object?)issue.HandShape ?? issue.Anchor;
                ChartLevel? level = null;
                if (element != null)
                {
                    levels ??= LevelsByElement(arrangement);
                    level = levels.GetValueOrDefault(element);
                }

                var exportIndex = uniqueName ? SameFaultIndex(exportFindings, merged, arrangement.Name, issue) : -1;
                var export = exportIndex < 0 ? null : exportFindings[exportIndex];
                if (exportIndex >= 0) merged[exportIndex] = true;

                var severity = Severity(issue);
                if (export != null && Severity(export) > severity) severity = Severity(export);
                findings.Add(new QualityFinding(
                    export == null ? QualityChecks.ChartRules : QualityChecks.ChartRules | QualityChecks.ExportValidation,
                    severity, issue.Code, arrangement, level, issue.TimeMs ?? export?.TimeMs, element, issue, export,
                    null));
            }
        }

        for (var i = 0; i < exportFindings.Count; i++)
        {
            if (merged[i]) continue;
            var export = exportFindings[i];
            findings.Add(new QualityFinding(QualityChecks.ExportValidation, Severity(export), export.MessageKey,
                Named(project, export.ArrangementName), null, export.TimeMs, null, null, export, null));
        }

        if (audio != null)
        {
            foreach (var (arrangement, outcome) in audio)
            {
                if (!outcome.Ran) continue;
                var hardest = ChartLevels.Hardest(arrangement); // the level the lint verifies
                foreach (var finding in outcome.Findings)
                {
                    findings.Add(new QualityFinding(QualityChecks.AudioLint, QualitySeverity.Warning, AudioLintCode,
                        arrangement, hardest, finding.TimeMs, null, null, null, finding));
                }
            }
        }

        var order = new Dictionary<Arrangement, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < project.Arrangements.Count; i++) order.TryAdd(project.Arrangements[i], i);
        var sorted = findings
            .OrderBy(f => f.Arrangement == null ? -1 : order.GetValueOrDefault(f.Arrangement, int.MaxValue))
            .ThenBy(f => f.TimeMs ?? double.MinValue)
            .ThenByDescending(f => f.Severity)
            .ToList();

        var summaries = project.Arrangements.Select(a =>
        {
            var own = sorted.Where(f => ReferenceEquals(f.Arrangement, a)).ToList();
            return new ArrangementQuality(a, ChartDifficultyEstimator.Estimate(a, project.Beats),
                own.Count(f => f.Severity == QualitySeverity.Error),
                own.Count(f => f.Severity == QualitySeverity.Important),
                own.Count(f => f.Severity == QualitySeverity.Warning),
                audio != null && audio.TryGetValue(a, out var outcome) && outcome.Ran);
        }).ToList();

        return new ChartQualityReport(sorted, summaries);
    }

    /// <summary>
    ///     What <see cref="EofExporter" /> would approximate or drop, grouped by
    ///     <see cref="EofLossSummary" />. It runs the exporter in memory, so the places it names are
    ///     this chart's, never a stale export's. Pure over <paramref name="project" />, which the
    ///     caller passes as a snapshot when it runs this off the UI thread. The exporter never
    ///     throws over lossy content, so a throw here is a writer bug; the result says so instead.
    /// </summary>
    public static EofRoundTripCheck CheckEofRoundTrip(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            return new EofRoundTripCheck(EofLossSummary.From(EofExporter.Export(project).Report), "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new EofRoundTripCheck(null, ex.Message);
        }
    }

    private static int SameFaultIndex(IReadOnlyList<ExportValidationFinding> exports, bool[] merged, string arrangementName,
        ArrangementIssue issue)
    {
        for (var i = 0; i < exports.Count; i++)
        {
            var export = exports[i];
            if (merged[i] || export.ArrangementName != arrangementName) continue;
            if (!SameFault.TryGetValue(export.MessageKey, out var type) || type != issue.Type) continue;
            var sameTime = issue.TimeMs is { } a
                ? export.TimeMs is { } b && Math.Abs(a - b) <= SameTimeMs
                : export.TimeMs == null;
            if (sameTime) return i;
        }

        return -1;
    }

    private static QualitySeverity Severity(ArrangementIssue issue) =>
        issue.Severity == ArrangementIssueSeverity.Important ? QualitySeverity.Important : QualitySeverity.Warning;

    private static QualitySeverity Severity(ExportValidationFinding finding) =>
        finding.Severity == ExportFindingSeverity.Error ? QualitySeverity.Error : QualitySeverity.Warning;

    /// <summary>The ONE arrangement carrying <paramref name="name" />, or null when none or several
    /// do — the rule <c>RocksmithFixes.ArrangementNamed</c> applies to an export finding's name.</summary>
    private static Arrangement? Named(ChartProject project, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        Arrangement? found = null;
        foreach (var arrangement in project.Arrangements)
        {
            if (arrangement.Name != name) continue;
            if (found != null) return null;
            found = arrangement;
        }

        return found;
    }

    private static Dictionary<object, ChartLevel> LevelsByElement(Arrangement arrangement)
    {
        var map = new Dictionary<object, ChartLevel>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes) map.TryAdd(note, level);
            foreach (var chord in level.Chords) map.TryAdd(chord, level);
            foreach (var shape in level.HandShapes) map.TryAdd(shape, level);
            foreach (var anchor in level.Anchors) map.TryAdd(anchor, level);
        }

        return map;
    }
}
