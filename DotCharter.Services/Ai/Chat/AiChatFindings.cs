using System.Text;
using DotCharter.Localization;
using DotCharter.Services.Ai.Chart;

namespace DotCharter.Services.Ai.Chat;

/// <summary>
/// The Issues tab's and the chat's way into the AI chart pass for FINDINGS: which job can fix
///     a finding, the scope that covers it, the words that ask for the fix or the explanation, and the
///     list of findings a pass left behind.
///     <para>
///         <b>Only the jobs the pass already has.</b> A finding is fixable when one of them changes what
///         the rule reads: the hand positions (FHP only, the fingering follows), the chord fingers
///         (fingering only), or both (E04 and I46, where the two disagree); or the phrase markers
/// (D03, I10, I25, I26 — the sections &amp; phrases job, which judges its WHOLE proposal
///         against the targets, <see cref="AiStructureGate.TargetsLeft" />). Every other finding, a
///         note-level problem among them, has no job, and the Issues tab says so instead of offering the
///         button. The FHP / fingering codes are exactly the rule gate's (<see cref="AiChartRuleGate.Guarded" />),
///         so the pass can check that the fix removed them; <c>AiChartRuleGateCoverageTests</c> pins that.
///     </para>
/// </summary>
public static class AiChatFindings
{
    /// <summary>The most findings one request lists; the rest are named as a count.</summary>
    public const int MaxListed = 12;

    private static readonly HashSet<ArrangementIssueType> AnchorFixed =
    [
        ArrangementIssueType.AnchorOnOrBelowCapo,
        ArrangementIssueType.AnchorTopFretTooHigh,
        ArrangementIssueType.NoteWithoutAnchor,
        ArrangementIssueType.NoteOutsideAnchorRange,
        ArrangementIssueType.AnchorInsideHandShape,
        ArrangementIssueType.AnchorInsideHandShapeAtPhraseBoundary,
        ArrangementIssueType.AnchorCloseToUnpitchedSlide,
        ArrangementIssueType.AnchorMissingAtPhraseStart,
        ArrangementIssueType.PositionShiftIntoPullOff,
        ArrangementIssueType.FingerChangeDuringSlide,
    ];

    private static readonly HashSet<ArrangementIssueType> FingerFixed =
    [
        ArrangementIssueType.FingerSetForOpenString,
        ArrangementIssueType.FingerMissingOnFrettedString,
        ArrangementIssueType.FirstFingerNotOnLowestFret,
        ArrangementIssueType.PossiblyWrongChordFingering,
        ArrangementIssueType.BarreOverOpenStrings,
    ];

    private static readonly HashSet<ArrangementIssueType> BothFixed =
    [
        ArrangementIssueType.IndexFingerNotOnAnchorFret,
        ArrangementIssueType.FingeringAnchorMismatch,
    ];

    /// <summary>The phrase findings the sections &amp; phrases job fixes: a phrase that plays nothing
    /// (D03), a phrase change under a ringing Link-Next note (I10), a first phrase that plays (I25), no END
    /// phrase (I26, untimed).</summary>
    private static readonly HashSet<ArrangementIssueType> StructureFixed =
    [
        ArrangementIssueType.EmptyPhrase,
        ArrangementIssueType.PhraseChangeOnLinkNextNote,
        ArrangementIssueType.FirstPhraseNotEmpty,
        ArrangementIssueType.NoEndPhrase,
    ];

    /// <summary>Every finding type the FHP / fingering jobs can fix (the rule gate's <c>Guarded</c> set).</summary>
    public static IEnumerable<ArrangementIssueType> FixableTypes => AnchorFixed.Concat(FingerFixed).Concat(BothFixed);

    /// <summary>Every finding type the sections &amp; phrases job can fix.</summary>
    public static IEnumerable<ArrangementIssueType> StructureTypes => StructureFixed;

    /// <summary>The job that fixes <paramref name="type" />, or null when none can.</summary>
    /// <param name="type">The finding type.</param>
    public static AiChartJob? JobFor(ArrangementIssueType type) =>
        AnchorFixed.Contains(type) ? AiChartJob.FhpOnly
        : FingerFixed.Contains(type) ? AiChartJob.FingeringOnly
        : BothFixed.Contains(type) ? AiChartJob.FhpAndFingering
        : StructureFixed.Contains(type) ? AiChartJob.SectionsAndPhrases
        : null;

    /// <summary>
    ///     True when a run of <paramref name="job" /> can remove a finding of <paramref name="type" />: its own
    ///     job, the joint job for any hand-position or finger finding, and either half for the E04 / I46 pair
    ///     the two disagree on. The sections &amp; phrases job fixes only its own.
    /// </summary>
    /// <param name="job">The job that runs.</param>
    /// <param name="type">The finding type.</param>
    public static bool Fixes(AiChartJob job, ArrangementIssueType type) => JobFor(type) switch
    {
        null => false,
        AiChartJob.SectionsAndPhrases => job == AiChartJob.SectionsAndPhrases,
        var own => job == AiChartJob.FhpAndFingering || own == job ||
                   (own == AiChartJob.FhpAndFingering && job is AiChartJob.FhpOnly or AiChartJob.FingeringOnly)
    };

    /// <summary>The one job that fixes every finding in <paramref name="issues" /> (a mix of anchor and
    /// finger findings takes both), or null when any of them has none, or when a phrase finding is mixed
    /// with a hand-position or finger one (no one job changes both).</summary>
    /// <param name="issues">The findings.</param>
    public static AiChartJob? JobFor(IEnumerable<ArrangementIssue> issues)
    {
        AiChartJob? job = null;
        foreach (var issue in issues)
        {
            if (JobFor(issue.Type) is not { } one) return null;
            if (job is { } before && before != one &&
                (one == AiChartJob.SectionsAndPhrases || before == AiChartJob.SectionsAndPhrases))
            {
                return null;
            }

            job = job is null || job == one ? one : AiChartJob.FhpAndFingering;
        }

        return job;
    }

    /// <summary>True when a job can fix <paramref name="issue" /> and it has a time to aim at — a phrase
    /// finding needs none: the structure job judges the whole arrangement, so I26 (untimed) is a target.</summary>
    /// <param name="issue">The finding.</param>
    public static bool CanFix(ArrangementIssue issue) =>
        JobFor(issue.Type) is { } job && (issue.TimeMs is not null || job == AiChartJob.SectionsAndPhrases);

    /// <summary>
    ///     The scope that covers every finding: the phrase each one sits in, joined into one range on
    ///     <paramref name="level" />; null when no finding is timed inside a phrase that plays.
    /// </summary>
    /// <param name="arrangement">The arrangement.</param>
    /// <param name="level">The level the findings are on.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="issues">The findings.</param>
    public static AiChatScope? Scope(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        IEnumerable<ArrangementIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        FractionalPosition? from = null, to = null;
        var anyOpenStart = false;
        foreach (var issue in issues)
        {
            if (issue.TimeMs is not { } ms) continue;
            // A hair past the finding: E06 sits exactly ON its phrase's marker.
            var section = AiChatScope.SectionAt(arrangement, level, TimelineMath.PositionFromMs(ms + 0.5, beats));
            if (section is null) continue;
            if (section.From is { } f) from = from is { } a && a < f ? a : f;
            else anyOpenStart = true;
            if (section.To is { } t) to = to is { } b && b > t ? b : t;
        }

        if (to is null) return null;
        return new AiChatScope(AiChatScopeKind.Section, arrangement, anyOpenStart ? null : from, to);
    }

    /// <summary>The targets the pass must remove: one per finding.</summary>
    /// <param name="issues">The findings.</param>
    public static IReadOnlyList<AiChartFindingTarget> Targets(IEnumerable<ArrangementIssue> issues) =>
        [.. issues.Select(AiChartFindingTarget.Of)];

    /// <summary>"I20 · M21.1 · 00:41.20 — Anchor is placed inside a hand shape …", one per line.</summary>
    /// <param name="issues">The findings.</param>
    /// <param name="beats">The beat map, for the bar.</param>
    /// <param name="localization">The "and N more" line.</param>
    public static string Lines(IReadOnlyList<ArrangementIssue> issues, IReadOnlyList<BeatMarker> beats,
        ILocalizationService localization)
    {
        var sb = new StringBuilder();
        foreach (var issue in issues.Take(MaxListed))
        {
            sb.Append("• ").Append(issue.Code);
            var where = ChartPositionLabel.Format(beats, issue.TimeMs);
            if (where.Length > 0) sb.Append(" · ").Append(where);
            sb.Append(" — ").Append(issue.Message).Append('\n');
        }

        if (issues.Count > MaxListed) sb.Append(localization.Format("AiChat_Leftovers_More", issues.Count - MaxListed)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    ///     The chat request that asks the AI to FIX <paramref name="issues" />: their job, their scope, the
    ///     findings with their codes and the prompt's own rule text as the instruction, and the targets the
    ///     rule gate holds each chunk to. Null when a finding has no job or no scope covers them. Phrase
    /// findings take the sections &amp; phrases job on the WHOLE arrangement: a song's form is only
    ///     visible whole, and the fix may land outside the finding's phrase (an END after the last note).
    /// </summary>
    /// <param name="issues">The findings (each <see cref="CanFix" />).</param>
    /// <param name="arrangement">Their arrangement.</param>
    /// <param name="level">Their level.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="localization">The request's words.</param>
    /// <param name="modelId">The model, or null for Settings ▸ AI's.</param>
    public static AiChatInstructionRequest? FixRequest(IReadOnlyList<ArrangementIssue> issues, Arrangement arrangement,
        ChartLevel level, IList<BeatMarker> beats, ILocalizationService localization, string? modelId = null)
    {
        if (issues.Count == 0 || JobFor(issues) is not { } job) return null;
        var scope = job == AiChartJob.SectionsAndPhrases
            ? AiChatScope.WholeArrangement(arrangement)
            : Scope(arrangement, level, beats, issues);
        if (scope is null) return null;
        var text = localization["AiChat_Fix_Header"] + "\n" + Lines(issues, beats.ToList(), localization) + "\n" +
                   RuleLines(issues);
        return new AiChatInstructionRequest(text.TrimEnd('\n'), scope, null, modelId, job) { Targets = Targets(issues) };
    }

    /// <summary>
    ///     The chat request that asks the AI to EXPLAIN <paramref name="issue" /> (the Ask / explain job):
    ///     the finding, its rule text and its phrase as the scope. It writes nothing. Null when the finding
    ///     has no time or sits in no phrase that plays.
    /// </summary>
    /// <param name="issue">The finding.</param>
    /// <param name="arrangement">Its arrangement.</param>
    /// <param name="level">Its level.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="localization">The question's words.</param>
    /// <param name="modelId">The model, or null for Settings ▸ AI's.</param>
    public static AiChatInstructionRequest? ExplainRequest(ArrangementIssue issue, Arrangement arrangement,
        ChartLevel level, IList<BeatMarker> beats, ILocalizationService localization, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var scope = Scope(arrangement, level, beats, [issue]) ?? AiChatScope.WholeArrangement(arrangement);
        var where = ChartPositionLabel.Format(beats.ToList(), issue.TimeMs);
        var text = localization.Format("AiChat_Explain_Question", issue.Code, where, issue.Message);
        var rule = AiChartPrompt.RuleText(issue.Code);
        if (rule.Length > 0) text += "\n" + rule;
        return new AiChatInstructionRequest(text, scope, null, modelId, AiChartJob.Explain);
    }

    /// <summary>The prompt's rule line for each distinct code, so the instruction carries the rule text.</summary>
    private static string RuleLines(IEnumerable<ArrangementIssue> issues) => string.Join("\n", issues
        .Select(i => i.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal)
        .Select(AiChartPrompt.RuleText).Where(t => t.Length > 0));
}
