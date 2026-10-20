using System.Globalization;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     The second half of the validation: every accepted chunk must pass the inspector's
/// OWN rules — the ones that judge anchors and fingerings — on a scratch copy of
///     the level with every earlier accepted chunk already applied.
///     <para>
///         <b>Nothing is re-implemented here.</b> The plan is turned into the very action
///         <see cref="AiChartApplier" /> will write, executed on the scratch level, and the
///         job's production rules (<see cref="RulesFor" />) run before and after. A chunk that
///         INTRODUCES a guarded finding anywhere its anchors reach is undone on the scratch
///         and rejected; findings the chart already had are not the reply's fault.
///     </para>
///     <para>
/// <b>The rule list is pinned per job.</b> A job that moves anchors also runs
///         <see cref="AnchorRule" /> (I20 I21 I47), <see cref="PhraseAnchorRule" /> (E06) and
///         <see cref="NoteRule" /> (I42 I43): the AI's own anchors broke them and nothing said so.
///         <c>AiChartRuleGateCoverageTests</c> fails when a production rule is neither here nor
///         listed there with the reason the job's output cannot trip it.
///     </para>
///     <para>
/// <b>Targets (Fix with AI).</b> A chunk whose reach holds a finding it was asked to
///         fix must REMOVE it: one still there rejects the chunk (<see cref="AiChartRejectionCodes.FindingLeft" />)
///         and the retry names it.
///     </para>
/// </summary>
public sealed class AiChartRuleGate
{
    /// <summary>The findings an accepted chunk may not introduce: the capo floor and the
    /// neck (L02 L03), notes outside or without an anchor (L04 L05), fingers vs open and
    /// fretted strings (L06 L07), the index on the lowest fret (L08), the index finger on
    /// the anchor fret (E04), a barre over open strings (I45), the shape/anchor agreement
    /// (I46), the index above another finger (I44), an anchor inside a hand shape (I20, and
    /// I21 on a phrase boundary), an anchor on an unpitched slide's end (I47), a phrase that
    /// plays notes with no anchor of its own (E06), and the two anchor-derived note checks: a
    /// position shift into a pull-off (I42) and a finger change during a slide (I43).</summary>
    public static readonly IReadOnlySet<ArrangementIssueType> Guarded = new HashSet<ArrangementIssueType>
    {
        ArrangementIssueType.AnchorOnOrBelowCapo,
        ArrangementIssueType.AnchorTopFretTooHigh,
        ArrangementIssueType.NoteWithoutAnchor,
        ArrangementIssueType.NoteOutsideAnchorRange,
        ArrangementIssueType.FingerSetForOpenString,
        ArrangementIssueType.FingerMissingOnFrettedString,
        ArrangementIssueType.FirstFingerNotOnLowestFret,
        ArrangementIssueType.IndexFingerNotOnAnchorFret,
        ArrangementIssueType.BarreOverOpenStrings,
        ArrangementIssueType.FingeringAnchorMismatch,
        ArrangementIssueType.PossiblyWrongChordFingering,
        ArrangementIssueType.AnchorInsideHandShape,
        ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary,
        ArrangementIssueType.AnchorCloseToUnpitchedSlide,
        ArrangementIssueType.AnchorMissingAtPhraseStart,
        ArrangementIssueType.PositionShiftIntoPullOff,
        ArrangementIssueType.FingerChangeDuringSlide,
    };

    /// <summary>The rules every chunked job runs: they judge fingers, templates, hand shapes and
    /// the anchor range.</summary>
    private static readonly IArrangementRule[] FingerRules =
    [
        new CapoAnchorRangeRule(),
        new ChordRule(),
        new HandShapeRule(),
        new IndexFingerAnchorRule(),
        new TemplateAndOverlapRule(),
    ];

    /// <summary>A job that moves anchors also runs the anchor-placement rules.</summary>
    private static readonly IArrangementRule[] AnchorRules =
    [
        .. FingerRules,
        new AnchorRule(),
        new NoteRule(),
        new PhraseAnchorRule(),
    ];

    /// <summary>The production rules the gate runs for <paramref name="job" />.</summary>
    /// <param name="job">The job.</param>
    public static IReadOnlyList<IArrangementRule> RulesFor(AiChartJob job) =>
        job.ProposesAnchors() ? AnchorRules : FingerRules;

    private readonly IArrangementRule[] _rules;
    private readonly IReadOnlyList<AiChartFindingTarget> _targets;
    private readonly Arrangement _scratch;
    private readonly ChartLevel _level;
    private readonly IList<BeatMarker> _beats;
    private readonly IFingeringGenerationService _fingering;
    private List<ArrangementIssue> _current;

    /// <summary>Copies <paramref name="level" /> of <paramref name="arrangement" /> into a
    /// one-level scratch arrangement. The originals are never touched.</summary>
    /// <param name="arrangement">The arrangement (live or a clone).</param>
    /// <param name="level">One of its levels.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="fingering">The template resolver the applier uses.</param>
    /// <param name="job">Whose rule list runs (<see cref="RulesFor" />).</param>
    /// <param name="targets">Findings a chunk reaching them must remove (Fix with AI); null for none.</param>
    public AiChartRuleGate(
        Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats, IFingeringGenerationService fingering,
        AiChartJob job, IReadOnlyList<AiChartFindingTarget>? targets)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        var index = arrangement.Levels.IndexOf(level);
        if (index < 0) throw new ArgumentException("The level is not one of the arrangement's.", nameof(level));

        _scratch = ChartProjectCloner.CloneArrangement(arrangement);
        _level = _scratch.Levels[index];
        _scratch.Levels.Clear();
        _scratch.Levels.Add(_level);
        _beats = beats;
        _fingering = fingering;
        _rules = [.. RulesFor(job)];
        _targets = targets ?? [];
        _current = Findings();
    }

    /// <summary>The scratch arrangement, with every accepted chunk applied (for the tests
    /// and the eval).</summary>
    public Arrangement Scratch => _scratch;

    /// <summary>The guarded findings on the scratch now (every accepted chunk applied) timed in
    /// <c>[fromMs, toMs]</c>: what would be LEFT if every accepted chunk were written.</summary>
    /// <param name="fromMs">Earliest time, or null for the start.</param>
    /// <param name="toMs">Latest time, or null for the end.</param>
    public IReadOnlyList<ArrangementIssue> FindingsIn(double? fromMs, double? toMs) =>
        [.. _current.Where(i => i.TimeMs is { } ms && (fromMs is not { } f || ms >= f - 0.5) && (toMs is not { } t || ms <= t + 0.5))];

    /// <summary>Applies <paramref name="plan" /> to the scratch level, or rejects it and
    /// leaves the scratch as it was.</summary>
    /// <param name="plan">A structurally valid plan.</param>
    /// <param name="rejection">Why it was rejected, when the answer is false.</param>
    public bool TryAccept(AiChartChunkPlan plan, out AiChartRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        rejection = null;

        // No guard check here: an earlier chunk's follower may have repointed this one's chords.
        var action = AiChartApplier.Build(_scratch, _level, [plan], _fingering, out _, checkGuards: false);
        if (action is null) return TargetsRemoved(plan, _current, out rejection);
        action.Execute();

        List<ArrangementIssue> after;
        try
        {
            after = Findings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            action.Undo();
            rejection = new AiChartRejection(plan.ChunkIndex, AiChartRejectionCodes.Rules,
                "the chart checks could not run on this chunk: " + ex.Message);
            return false;
        }

        var (fromMs, toMs) = Reach(plan);
        var before = Count(_current, fromMs, toMs);
        var introduced = Count(after, fromMs, toMs)
            .Where(kv => kv.Value > before.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
        if (introduced.Count > 0)
        {
            action.Undo();
            var codes = string.Join(" ", introduced.Select(k => k.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal));
            rejection = new AiChartRejection(plan.ChunkIndex, AiChartRejectionCodes.Rules,
                $"the reply introduces {introduced.Count} chart finding(s): {codes}. {RuleHints(introduced.Select(k => k.Code))}".TrimEnd());
            return false;
        }

        if (!TargetsRemoved(plan, after, out rejection))
        {
            action.Undo();
            return false;
        }

        _current = after;
        return true;
    }

    /// <summary>
    ///     True when no target this chunk reaches is still in <paramref name="after" />. A target the
    ///     chart lacked before the chunk (an earlier chunk fixed it) is not this chunk's.
    /// </summary>
    private bool TargetsRemoved(AiChartChunkPlan plan, List<ArrangementIssue> after, out AiChartRejection? rejection)
    {
        rejection = null;
        if (_targets.Count == 0) return true;
        var (fromMs, toMs) = Reach(plan);
        var left = _targets
            .Where(t => t.TimeMs >= fromMs && t.TimeMs < toMs && Holds(_current, t) && Holds(after, t))
            .ToList();
        if (left.Count == 0) return true;
        var named = string.Join(", ", left.Select(t => string.Create(CultureInfo.InvariantCulture,
            $"{t.Code} at {t.TimeMs / 1000.0:F2} s")));
        rejection = new AiChartRejection(plan.ChunkIndex, AiChartRejectionCodes.FindingLeft,
            $"the reply leaves {left.Count} finding(s) it was asked to fix: {named}. {RuleHints(left.Select(t => t.Code))}".TrimEnd());
        return false;
    }

    private static bool Holds(List<ArrangementIssue> issues, AiChartFindingTarget target) =>
        target.TimeMs is { } at &&
        issues.Any(i => i.Code == target.Code && i.TimeMs is { } ms && Math.Abs(ms - at) < 1 &&
                        (target.StringIndex is null || i.StringIndex == target.StringIndex));

    /// <summary>The prompt's own rule line for each code (<see cref="AiChartPrompt.RuleText" />), so a
    /// retry names a rule the model has already been shown.</summary>
    private static string RuleHints(IEnumerable<string> codes) => string.Join(" ", codes.Distinct()
        .OrderBy(c => c, StringComparer.Ordinal)
        .Select(AiChartPrompt.RuleText)
        .Where(t => t.Length > 0));

    /// <summary>From the chunk's start to the first anchor at or after its end (the last
    /// anchor of the chunk governs until then), in milliseconds.</summary>
    private (double From, double To) Reach(AiChartChunkPlan plan)
    {
        var from = plan.Start.GetPositionMs(_beats) - 0.5;
        if (plan.End is not { } end) return (from, double.MaxValue);
        var next = _level.Anchors.Where(a => a.Position >= end).OrderBy(a => a.Position).FirstOrDefault();
        return (from, next is null ? double.MaxValue : next.Position.GetPositionMs(_beats) - 0.5);
    }

    private static Dictionary<(string Code, long Ms, int String), int> Count(
        List<ArrangementIssue> issues, double fromMs, double toMs)
    {
        var counts = new Dictionary<(string, long, int), int>();
        foreach (var issue in issues)
        {
            if (issue.TimeMs is not { } ms || ms < fromMs || ms >= toMs) continue;
            var key = (issue.Code, (long)Math.Round(ms), issue.StringIndex ?? -1);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private List<ArrangementIssue> Findings()
    {
        var context = ArrangementValidationContext.Build(_scratch, _beats, null);
        var issues = new List<ArrangementIssue>();
        foreach (var rule in _rules) rule.Validate(context, issues);
        issues.RemoveAll(i => !Guarded.Contains(i.Type));
        return issues;
    }
}
