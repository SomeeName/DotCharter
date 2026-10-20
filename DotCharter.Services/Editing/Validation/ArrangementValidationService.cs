using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Orchestrates a set of <see cref="IArrangementRule"/> implementations to
///     validate an arrangement. The service itself has no rule logic — each
///     category (phrases / notes / chords / anchors / hand shapes / link-next)
///     lives in its own rule class under <c>Rules/</c>, injected via DI.
///
///     A logger-only convenience constructor is retained so existing callers
///     (and tests) can instantiate the service with the default rule set.
/// </summary>
public sealed class ArrangementValidationService : IArrangementValidationService
{
    private readonly IAppLogger _logger;
    private readonly IArrangementRule[] _rules;

    /// <summary>
    ///     DI-friendly constructor. Register each rule against
    ///     <see cref="IArrangementRule"/>; execution order is the order they
    ///     are resolved (i.e. registration order).
    /// </summary>
    public ArrangementValidationService(IAppLogger logger, IEnumerable<IArrangementRule> rules)
    {
        _logger = logger;
        _rules = rules.ToArray();
    }

    /// <summary>
    ///     Convenience constructor that wires up the built-in rule set in the
    ///     same order the old partial-class implementation ran checks in.
    /// </summary>
    public ArrangementValidationService(IAppLogger logger)
        : this(logger, DefaultRules())
    {
    }

    public static IReadOnlyList<IArrangementRule> DefaultRules() => new IArrangementRule[]
    {
        new PhraseRule(),
        new NoteRule(),
        new ChordRule(),
        new SongEndAudioRule(),
        new AnchorRule(),
        new HandShapeRule(),
        new IndexFingerAnchorRule(),
        new HandShapeContentRule(),
        new ChordOutsideHandShapeRule(),
        new PhraseAnchorRule(),
        new OpenNoteBendRule(),
        new LinkNextRule(),
        // Cross-engine ports (Log Charter + Editor on Fire parity):
        new CapoAnchorRangeRule(),
        new SlideTechniqueRule(),
        new TemplateAndOverlapRule(),
        // DotCharter-native pre-export gates (D-series), live in the FEHLER inspector:
        new ToneSilenceAndPhraseRule(),
    };

    /// <summary><paramref name="project" /> lets rules see PROJECT-level facts the
    /// arrangement cannot carry — the authored tone bodies. Optional, and null
    /// means unknown rather than empty: a caller that omits it gets no
    /// project-level findings rather than a screenful of false ones.</summary>
    public IReadOnlyList<ArrangementIssue> Validate(
        Arrangement? arrangement,
        IList<BeatMarker> beats,
        double? totalAudioLengthMs = null,
        ChartProject? project = null)
    {
        if (arrangement == null) return Array.Empty<ArrangementIssue>();

        // No beat grid ⇒ nothing can be judged: every rule positions its finding
        // on the beat map. Say so instead of returning an empty list — "I could
        // not check" and "I found nothing" are different answers, and only one of
        // them means the chart is fine. ChartExportValidator already calls this
        // state a hard error, so swallowing it here put the inspector in open
        // disagreement with the export dialog.
        if (beats.Count == 0)
        {
            return new[]
            {
                new ArrangementIssue(
                    ArrangementIssueType.NoBeatGridSoNoChecksRan,
                    "This arrangement has no beat grid, so none of the chart checks could run. " +
                    "Nothing here means the chart is clean — generate or import a beat map first.")
            };
        }

        // A raw TotalAudioLengthMs of 0 means "no audio decoded", NOT "the
        // song ends at 0:00" — passing it through as a boundary made the
        // per-sound I29 checks flag EVERY sound of an audio-less chart
        // (1,011 duplicates on one GP import). Normalized here, at the ONE
        // boundary, so the rules never have to re-learn the convention;
        // SongEndAudioRule then reports the unchecked state once.
        if (totalAudioLengthMs is <= 0) totalAudioLengthMs = null;

        var context = ArrangementValidationContext.Build(arrangement, beats, totalAudioLengthMs, project);
        var issues = new List<ArrangementIssue>();

        foreach (var rule in _rules)
        {
            try
            {
                rule.Validate(context, issues);
            }
            catch (Exception ex)
            {
                var ruleName = rule.GetType().Name;
                _logger.Error(AppLogCategory.Editing,
                    $"Rule '{ruleName}' crashed validating '{arrangement.Name}'", ex);

                // A crashed rule checked NOTHING, and a shorter list with no
                // marker reads exactly like a cleaner chart. Surface it.
                issues.Add(new ArrangementIssue(
                    ArrangementIssueType.ValidationRuleCrashed,
                    $"The '{ruleName}' check could not run ({ex.GetType().Name}), so whatever it " +
                    "inspects is UNCHECKED here. This is a bug — please report it."));
            }
        }

        // Stable order: non-positional first (TimeMs == null), then ascending
        // time, and — for everything those two leave equal — the order the rules
        // produced them in.
        //
        // That last clause is the whole point. A plain
        // `issues.Sort(comparer)` would be wrong: List<T>.Sort is INTROSORT: unstable, so
        // every pair the comparer calls equal (all findings sharing a time, and
        // all non-positional findings) would come out in an order nobody chose. Under
        // 17 elements introsort falls through to insertion sort and LOOKS
        // stable, which is why small charts never show it.
        //
        // The FEHLER tab is where it bit: IssueGrouping.Build buckets by
        // severity and documents that it "preserves the original issue order
        // inside each group", so a charter triaging a stack of findings on one
        // beat had the rows reshuffle under the cursor whenever an edit ANYWHERE
        // else added or removed a finding — a different list length makes
        // introsort pick different pivots and move unrelated rows.
        //
        // Carrying the production ordinal makes the comparator a TOTAL order, so
        // the result is a function of the findings alone.
        var ordered = new (ArrangementIssue Issue, int Ordinal)[issues.Count];
        for (var i = 0; i < issues.Count; i++) ordered[i] = (issues[i], i);

        Array.Sort(ordered, static (a, b) =>
        {
            if (a.Issue.HasTime != b.Issue.HasTime) return a.Issue.HasTime ? 1 : -1;
            if (a.Issue.HasTime)
            {
                var byTime = a.Issue.TimeMs!.Value.CompareTo(b.Issue.TimeMs!.Value);
                if (byTime != 0) return byTime;
            }

            return a.Ordinal.CompareTo(b.Ordinal);
        });

        var result = new List<ArrangementIssue>(ordered.Length);
        foreach (var entry in ordered) result.Add(entry.Issue);
        return result;
    }
}
