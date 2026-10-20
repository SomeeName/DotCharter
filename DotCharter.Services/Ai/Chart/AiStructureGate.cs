using System.Globalization;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.State;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
/// The export's section and phrase rules for the structure job: a change may not add a
///     finding to the CHART QUALITY REPORT (<see cref="ChartQualityReporter" />, the "Ready to ship?"
///     verdict over every arrangement rule and the export gate). Nothing is re-implemented here:
///     the change becomes the very action <see cref="AiStructureApplier" /> writes, it runs on a
///     scratch copy of the project, and the report is compared before and after.
///     <para>
///         Findings the chart already had are not the reply's fault; a finding is "added" when its
///         (code, arrangement, level, time) occurs more often after than before. The pass judges
///         each change alone, in reply order, with every earlier accepted one in place
///         (<see cref="TryAccept" />); the write judges the charter's accepted SUBSET again
///         (<see cref="Introduced" />), because leaving a neighbour out can change a verdict.
///     </para>
///     <para>
/// <b>A phrase brings the hand position it needs.</b> A phrase that plays notes must
///         own an anchor from its start to its first note (E06), so on a chart whose anchors do not
///         line up with the phrase starts the model aims for, most phrase proposals would be
///         refused. An added or moved phrase is first put on the scratch alone; for every level where
///         it then lacks an anchor, <see cref="PhraseStartAnchors.Needed" /> answers the one it needs
///         (the anchor in force restated at the phrase start, or the FHP generator's where none is in
///         force — never an invented fret). The change is judged and accepted WITH those anchors
///         attached (<see cref="AiStructureChange.Anchors" />), so the report sees exactly what the
///         write adds; one that still adds a finding (a phrase starting inside a hand shape, where
///         the anchor is I21) is refused as before.
///     </para>
/// </summary>
public sealed class AiStructureGate
{
    private readonly ChartQualityReporter _reporter;
    private readonly ChartProject _scratch;
    private readonly Arrangement _arrangement;
    private readonly int _arrangementIndex;
    private readonly IFretAnchorGenerationService _anchors;
    private Dictionary<FindingKey, int> _current;

    /// <summary>Copies <paramref name="project" />; the original is never touched.</summary>
    /// <param name="project">The project (live or a snapshot).</param>
    /// <param name="arrangement">One of its arrangements: the one the changes are for.</param>
    /// <param name="reporter">The chart quality report.</param>
    /// <param name="anchors">The FHP generator a phrase's anchor comes from where no anchor is in force;
    /// null for the default one.</param>
    public AiStructureGate(ChartProject project, Arrangement arrangement, ChartQualityReporter reporter,
        IFretAnchorGenerationService? anchors = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _anchors = anchors ?? new FretAnchorGenerationService();
        var index = project.Arrangements.IndexOf(arrangement);
        if (index < 0) throw new ArgumentException("The arrangement is not one of the project's.", nameof(arrangement));
        _scratch = ChartProjectCloner.Clone(project);
        _arrangement = _scratch.Arrangements[index];
        _arrangementIndex = index;
        _current = Findings(_reporter, _scratch);
    }

    /// <summary>
    /// The targets (Fix with AI) still in the report with every change accepted so far in place:
    ///     the check over the WHOLE proposal, because a phrase finding is fixed by a set of changes (move one
    ///     phrase, add another) that no single change does alone. A target matches a finding of its code in
    ///     this arrangement at its time (within 1 ms), on any level; an untimed one (I26) matches an untimed one.
    /// </summary>
    /// <param name="targets">The findings the proposal must remove.</param>
    public IReadOnlyList<AiChartFindingTarget> TargetsLeft(IReadOnlyList<AiChartFindingTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return [.. targets.Where(t => _current.Keys.Any(k => k.Code == t.Code && k.Arrangement == _arrangementIndex &&
                                                             (t.TimeMs is { } ms ? k.Ms != long.MinValue && Math.Abs(k.Ms - ms) <= 1
                                                                 : k.Ms == long.MinValue)))];
    }

    /// <summary>
    /// Whether an added or moved phrase brings the anchor it needs. True in the product;
    ///     false only to MEASURE what the job refused before it did (and to show a test that the
    ///     anchor is what lets a phrase pass).
    /// </summary>
    internal bool PlacesAnchors { get; init; } = true;

    /// <summary>Applies <paramref name="change" /> to the scratch, or refuses it and leaves the
    /// scratch as it was.</summary>
    /// <param name="change">A validated change.</param>
    /// <param name="index">Its place in the reply (the rejection's index).</param>
    /// <param name="accepted">The change as accepted: <paramref name="change" /> with the hand
    /// positions it brings (<see cref="AiStructureChange.Anchors" />) — what the review holds and the
    /// write writes. <paramref name="change" /> itself when refused.</param>
    /// <param name="rejection">Why it was refused, when the answer is false.</param>
    public bool TryAccept(AiStructureChange change, int index, out AiStructureChange accepted,
        out AiChartRejection? rejection)
    {
        ArgumentNullException.ThrowIfNull(change);
        rejection = null;
        accepted = change;
        var action = AiStructureApplier.Build(_arrangement, [change], out var applied, out _);
        if (action is null || applied == 0)
        {
            rejection = new AiChartRejection(index, AiChartRejectionCodes.Conflict,
                "it collides with another change already accepted");
            return false;
        }

        var withAnchors = PlacesAnchors ? WithAnchors(change, action) : change;
        if (!ReferenceEquals(withAnchors, change))
            action = AiStructureApplier.Build(_arrangement, [withAnchors], out _, out _)!;

        action.Execute();
        Dictionary<FindingKey, int> after;
        try
        {
            after = Findings(_reporter, _scratch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            action.Undo();
            rejection = new AiChartRejection(index, AiChartRejectionCodes.Rules,
                "the chart checks could not run on this change: " + ex.Message);
            return false;
        }

        var added = Added(_current, after);
        if (added.Count > 0)
        {
            action.Undo();
            var with = withAnchors.Anchors.Count == 0
                ? ""
                : " even with the hand position it needs at fret " +
                  string.Join(", ", withAnchors.Anchors.Select(a => a.Fret).Distinct().Order());
            rejection = new AiChartRejection(index, AiChartRejectionCodes.Rules,
                string.Create(CultureInfo.InvariantCulture,
                    $"it adds {added.Count} chart finding(s){with}: {string.Join(" ", added)}"));
            return false;
        }

        _current = after;
        accepted = withAnchors;
        return true;
    }

    /// <summary>
    /// <paramref name="change" /> with the hand positions it needs, or itself when it needs
    ///     none: only an added or moved PHRASE can. Its structure-only <paramref name="action" /> runs
    ///     on the scratch, so the phrase ends at the next phrase AS IT WILL BE; every level that then
    ///     plays notes in it without an anchor of its own gets <see cref="PhraseStartAnchors.Needed" />.
    ///     The scratch is put back before returning.
    /// </summary>
    private AiStructureChange WithAnchors(AiStructureChange change, IChartAction action)
    {
        if (change is not { Kind: AiStructureKind.Phrase, Op: AiStructureOp.Add or AiStructureOp.Move, To: { } to })
            return change;

        var anchors = new List<AiStructureAnchor>();
        action.Execute();
        try
        {
            var phrases = PhraseMarkers.Playable(_arrangement);
            var index = phrases.FindIndex(p => p.Position == to);
            if (index < 0) return change;
            for (var l = 0; l < _arrangement.Levels.Count; l++)
            {
                if (PhraseStartAnchors.Needed(_arrangement, _arrangement.Levels[l], phrases, index, _anchors) is { } a)
                    anchors.Add(new AiStructureAnchor(l, a.Position, a.Fret, a.Width));
            }
        }
        finally
        {
            action.Undo();
        }

        return anchors.Count == 0 ? change : change with { Anchors = anchors };
    }

    /// <summary>
    ///     The finding codes <paramref name="changes" /> together would ADD to the quality report of
    ///     <paramref name="project" />, judged on a copy — empty when the write is clean.
    /// </summary>
    public static IReadOnlyList<string> Introduced(ChartProject project, Arrangement arrangement,
        IReadOnlyList<AiStructureChange> changes, ChartQualityReporter reporter)
    {
        var gate = new AiStructureGate(project, arrangement, reporter);
        var action = AiStructureApplier.Build(gate._arrangement, changes, out _, out _);
        if (action is null) return [];
        action.Execute();
        return Added(gate._current, Findings(reporter, gate._scratch));
    }

    private static List<string> Added(Dictionary<FindingKey, int> before, Dictionary<FindingKey, int> after)
        => after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key))
            .Select(kv => kv.Key.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

    private static Dictionary<FindingKey, int> Findings(ChartQualityReporter reporter, ChartProject project)
    {
        var report = reporter.Build(project);
        var counts = new Dictionary<FindingKey, int>();
        foreach (var f in report.Findings)
        {
            var arrangement = f.Arrangement is null ? -1 : project.Arrangements.IndexOf(f.Arrangement);
            var level = f.Level is null || f.Arrangement is null ? -1 : f.Arrangement.Levels.IndexOf(f.Level);
            var key = new FindingKey(f.Code, arrangement, level, f.TimeMs is { } ms ? (long)Math.Round(ms) : long.MinValue);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private readonly record struct FindingKey(string Code, int Arrangement, int Level, long Ms);
}
