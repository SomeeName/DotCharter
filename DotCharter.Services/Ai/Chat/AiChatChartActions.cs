using System.Globalization;
using System.Text;
using DotCharter.Localization;
using DotCharter.Services.Ai.Chart;

namespace DotCharter.Services.Ai.Chat;

/// <summary>
/// One AI chat instruction to run on the chart.
/// </summary>
/// <param name="Instruction">What the charter typed ("use 1·3 for two-fret octaves in the verse").</param>
/// <param name="Scope">Where: the selection, a section or the whole arrangement, and which arrangement.</param>
/// <param name="Conversation">The chat so far (its last turns go into the prompt, bounded by
/// <see cref="AiChatExcerpt" />), or null for a first message.</param>
/// <param name="ModelId">The model the pane picked, or null for the one Settings ▸ AI names.</param>
/// <param name="Job">FHP + fingering (the default), FHP only, fingering only, sections and
/// phrases (<see cref="AiChartJob.SectionsAndPhrases" />), or ask / explain
/// (<see cref="AiChartJob.Explain" />: a text answer, nothing proposed), or tones
/// (<see cref="AiChartJob.Tones" />: Rocksmith tone bodies for the scope's tone changes). Never the
/// name job: the chat changes hand positions, chord fingering, sections and phrases and tone
/// definitions, nothing else.</param>
public sealed record AiChatInstructionRequest(
    string Instruction,
    AiChatScope Scope,
    AiChatConversation? Conversation = null,
    string? ModelId = null,
    AiChartJob Job = AiChartJob.FhpAndFingering)
{
    /// <summary>Fix with AI: the findings the pass must remove (the rule gate rejects a chunk that
    /// leaves one it reaches); null for an ordinary instruction.</summary>
    public IReadOnlyList<AiChartFindingTarget>? Targets { get; init; }
}

/// <summary>
///     The assistant's answer to an instruction: <see cref="Text" /> for the thread, the
///     HELD <see cref="Review" /> the review card shows (null when nothing is proposed, and always
///     for a question — <see cref="AiChartRunStatus.Answered" />), and the tokens the run spent.
/// <see cref="FollowUp" /> is "Ask the AI to fix these": a fix request for the findings the
///     pass left in its range, offered when nothing is held (with a held review the findings are
///     listed and the offer comes after Accept, <see cref="IAiChatChartActions.Applied" />).
/// </summary>
public sealed record AiChatInstructionReply(
    AiChartRunStatus Status,
    string Text,
    AiChartReview? Review,
    AiChatUsage? Usage,
    AiChatInstructionRequest? FollowUp = null)
{
    /// <summary>The reply as a chat turn, linked to its review card by the review's id.</summary>
    public AiChatTurn ToTurn(DateTimeOffset at) => new(AiChatRole.Assistant, Text, at, Usage, Review?.Id);
}

/// <summary>
/// A review was accepted and written: the review's id (the chat turn that holds it), the
///     words for the findings left in its range, and the fix request for them (null when none is left
/// or no job fixes them). <see cref="FromMenu" />: the review came from a Tools-menu verb, not
///     from the chat, so no turn holds it; it is raised only when findings are left.
/// </summary>
public sealed record AiChatApplied(string ReviewId, string Text, AiChatInstructionRequest? FollowUp)
{
    /// <summary>True for a menu verb's review: the chat reports it in the open conversation.</summary>
    public bool FromMenu { get; init; }
}

/// <summary>What the AI chat pane asks of the chart: run an instruction as proposals.</summary>
public interface IAiChatChartActions
{
    /// <summary>True while a chart pass (the chat's or a menu verb's) is in flight.</summary>
    bool IsRunning { get; }

    /// <summary>Raised on the UI thread when an FHP / fingering review was accepted and written, with
    /// what is left in its range — the chat's own reviews always, a menu verb's when something is
    /// left (<see cref="AiChatApplied.FromMenu" />).</summary>
    event EventHandler<AiChatApplied>? Applied;

    /// <summary>
    ///     Runs the AI chart pass on <see cref="AiChatInstructionRequest.Scope" /> with the
    ///     instruction and the recent conversation in the prompt, and HOLDS the proposals for
    ///     the review card. Never writes the chart; never throws for a run that fails (the
    ///     reply says why). Call on the UI thread.
    /// </summary>
    Task<AiChatInstructionReply> RunInstructionAsync(AiChatInstructionRequest request, CancellationToken ct);
}

/// <summary>
/// The AI chat's chart actions over <see cref="AiChartPassRun" />: the same pass,
///     reader, validator, rule gate, one retry and stale-chart checks as the menu verbs, the
///     proposals held in the same <see cref="AiChartPassRun.Review" />. Only anchors and
///     chord fingering can change — the pass's jobs, enforced by the reply schema and the
///     validator, not by the prompt — and notes are never touched.
/// </summary>
public sealed class AiChatInstructionRunner : IAiChatChartActions
{
    /// <summary>The most scope findings a typed message lists for the model; the rest are counted.</summary>
    internal const int MaxTypedTargetLines = 40;

    private readonly AiChartPassRun _run;
    private readonly ILocalizationService _localization;

    /// <summary>The ids of the reviews the chat's runs held: any other one a menu verb held.</summary>
    private readonly HashSet<string> _own = [];

    /// <summary>Builds the runner.</summary>
    /// <param name="run">The chart pass and its held review.</param>
    /// <param name="localization">The reply's words.</param>
    public AiChatInstructionRunner(AiChartPassRun run, ILocalizationService localization)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _run.ReviewApplied += OnReviewApplied;
    }

    /// <summary>Whether a typed FHP / fingering message takes the scope's findings as targets. True in
    /// the product; false only for the tests of other contracts whose fixture has findings.</summary>
    internal bool FindingsAsTargets { get; init; } = true;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    /// <inheritdoc />
    public event EventHandler<AiChatApplied>? Applied;

    /// <inheritdoc />
    public async Task<AiChatInstructionReply> RunInstructionAsync(AiChatInstructionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        var text = AiChatExcerpt.Instruction(request.Instruction);
        if (text.Length == 0) throw new ArgumentException("An instruction is required.", nameof(request));

        // a typed FHP / fingering message takes the findings in its scope the job can fix as
        // targets, as Fix with AI does; the model is shown them after the charter's words.
        var targets = request.Targets;
        var typedTargets = 0;
        if (targets is null && FindingsAsTargets && (request.Job.ProposesAnchors() || request.Job.ProposesFingers()) &&
            ScopeTargets(request) is { Count: > 0 } found)
        {
            targets = AiChatFindings.Targets(found);
            typedTargets = found.Count;
            text += "\n\n" + TargetBlock(found);
        }

        var instruction = new AiChartInstruction(text, AiChatExcerpt.Of(request.Conversation, request.Instruction));
        var run = new AiChartInstructedRun(request.Job, instruction, request.Scope.Arrangement, request.Scope.From,
            request.Scope.To, request.ModelId, request.Scope.ToneChanges);
        var outcome = await _run.RunInstructionAsync(run with { Targets = targets }, ct);
        if (outcome.Review is { } held) _own.Add(held.Id);

        var usage = outcome.Result is { } r && (r.InputTokens > 0 || r.OutputTokens > 0)
            ? new AiChatUsage(r.InputTokens, r.OutputTokens)
            : null;
        var reply = outcome is { Status: AiChartRunStatus.Held, Review: { } review }
            ? Summary(review)
            : outcome.Message;
        if (typedTargets > 0)
            reply += "\n" + _localization.Plural("AiChat_TypedTargets_One", "AiChat_TypedTargets_Many", typedTargets);

        // the findings the pass left in its range. With a review held they are what accepting all
        // of it would leave, and the fix is offered after Accept; with nothing held, now.
        AiChatInstructionRequest? followUp = null;
        if (outcome.Result?.Leftovers is { Count: > 0 } left && outcome.Beats is { } beats)
        {
            reply += "\n\n" + LeftoverText(left, beats) +
                     (outcome.Review is null ? "" : "\n" + _localization["AiChat_Leftovers_HeldHint"]);
            if (outcome.Review is null) followUp = FollowUp(left, request.Scope, request.ModelId, beats, request.Job);
        }

        return new AiChatInstructionReply(outcome.Status, reply, outcome.Review, usage, followUp);
    }

    /// <summary>The timed findings in the request's scope that its job can fix.</summary>
    private List<ArrangementIssue> ScopeTargets(AiChatInstructionRequest request) =>
    [
        .. _run.ScopeFindings(request.Job, request.Scope.Arrangement, request.Scope.From, request.Scope.To)
            .Where(i => i.TimeMs is not null && AiChatFindings.Fixes(request.Job, i.Type))
    ];

    /// <summary>The scope's findings as the model reads them, after the charter's words: English, like the
    /// rest of the prompt, with the prompt's own rule line per code.</summary>
    internal static string TargetBlock(IReadOnlyList<ArrangementIssue> found)
    {
        var sb = new StringBuilder("The chart checker flags these findings in this range. A section that leaves " +
                                   "one of them is refused, so fix them too:\n");
        foreach (var issue in found.Take(MaxTypedTargetLines))
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {issue.Code} at {issue.TimeMs!.Value / 1000.0:F2} s");
            if (issue.StringIndex is { } s) sb.Append(CultureInfo.InvariantCulture, $", string {s}");
            sb.Append(": ").Append(issue.Message).Append('\n');
        }

        if (found.Count > MaxTypedTargetLines)
            sb.Append(CultureInfo.InvariantCulture, $"- and {found.Count - MaxTypedTargetLines} more\n");
        foreach (var rule in found.Select(i => i.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal)
                     .Select(AiChartPrompt.RuleText).Where(t => t.Length > 0))
        {
            sb.Append(rule).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>"N findings are still left in this range:" and one line per finding.</summary>
    internal string LeftoverText(IReadOnlyList<ArrangementIssue> left, IList<BeatMarker> beats) =>
        _localization.Plural("AiChat_Leftovers_One", "AiChat_Leftovers_Many", left.Count) + "\n" +
        AiChatFindings.Lines(left, [.. beats], _localization);

    /// <summary>The fix request for <paramref name="left" />, on the same scope and model: the job that
    /// fixes them all, or the run's own FHP + fingering job, which fixes any of them.</summary>
    private AiChatInstructionRequest? FollowUp(IReadOnlyList<ArrangementIssue> left, AiChatScope scope, string? modelId,
        IList<BeatMarker> beats, AiChartJob ran)
    {
        var fixable = left.Where(AiChatFindings.CanFix).ToList();
        if (fixable.Count == 0 || AiChatFindings.JobFor(fixable) is not { } job) return null;
        if (ran == AiChartJob.FhpAndFingering) job = ran;
        var text = _localization["AiChat_Fix_Header"] + "\n" + AiChatFindings.Lines(fixable, [.. beats], _localization);
        return new AiChatInstructionRequest(text, scope, null, modelId, job) { Targets = AiChatFindings.Targets(fixable) };
    }

    private void OnReviewApplied(object? sender, AiChartApplied e)
    {
        var review = e.Review;
        var beats = review.Project.Beats;
        // a menu verb's review is reported too, but only when something is left to fix.
        var fromMenu = !_own.Remove(review.Id);
        if (e.Leftovers.Count == 0)
        {
            if (!fromMenu) Applied?.Invoke(this, new AiChatApplied(review.Id, _localization["AiChat_Leftovers_None"], null));
            return;
        }

        var scope = new AiChatScope(AiChatScopeKind.Section, review.Arrangement, review.PassFrom, review.PassTo);
        var text = LeftoverText(e.Leftovers, beats);
        if (fromMenu) text = _localization["AiChat_MenuPassApplied"] + "\n" + text;
        Applied?.Invoke(this, new AiChatApplied(review.Id, text, FollowUp(e.Leftovers, scope, null, beats, review.Job))
        {
            FromMenu = fromMenu
        });
    }

    /// <summary>
    ///     The reply for held proposals: how many sections, one line per section with what it
    ///     changes (the review card's own counts), the sections the checks refused, and where
    ///     to accept them.
    /// </summary>
    internal string Summary(AiChartReview review)
    {
        if (review.Items.Count > 0) return StructureSummary(review);
        if (review.Tones.Count > 0) return ToneSummary(review);
        // "\n", not AppendLine: the thread renders the text, not the platform.
        var sb = new StringBuilder();
        sb.Append(_localization.Plural("AiChat_Proposed_One", "AiChat_Proposed_Many", review.Chunks.Count)).Append('\n');
        foreach (var chunk in review.Chunks)
        {
            var name = string.IsNullOrWhiteSpace(chunk.Phrases) ? _localization["AiChat_SectionUnnamed"] : chunk.Phrases;
            sb.Append(_localization.Format("AiChat_ProposedSection", name, Clock(chunk.StartMs), Changes(chunk)))
                .Append('\n');
        }

        var refused = review.Result.Rejected.Count;
        if (refused > 0) sb.Append(_localization.Plural("AiChartReview_FailedOne", "AiChartReview_Failed", refused)).Append('\n');
        sb.Append(_localization["AiChat_ReviewHint"]);
        return sb.ToString();
    }

    /// <summary>The reply for held section and phrase changes: one line per change in the
    /// review card's own words (<see cref="AiStructureText" />), the ones the checks refused, and
    /// where to accept them.</summary>
    private string StructureSummary(AiChartReview review)
    {
        var sb = new StringBuilder();
        sb.Append(_localization.Plural("AiChat_StructureProposed_One", "AiChat_StructureProposed_Many", review.Items.Count))
            .Append('\n');
        foreach (var item in review.Items)
        {
            sb.Append(_localization.Format("AiChat_ProposedSection", AiStructureText.Label(item.Change, _localization),
                Clock(item.StartMs), AiStructureText.Detail(item, _localization))).Append('\n');
        }

        var refused = review.Result.Rejected.Count;
        if (refused > 0) sb.Append(_localization.Plural("AiStructure_FailedOne", "AiStructure_Failed", refused)).Append('\n');
        sb.Append(_localization["AiChat_ReviewHint"]);
        return sb.ToString();
    }

    /// <summary>The reply for held tones: one line per tone in the review card's own words
    /// (<see cref="AiToneText" />), the ones the checks refused, and where to accept them.</summary>
    private string ToneSummary(AiChartReview review)
    {
        var sb = new StringBuilder();
        sb.Append(_localization.Plural("AiChat_TonesProposed_One", "AiChat_TonesProposed_Many", review.Tones.Count))
            .Append('\n');
        foreach (var tone in review.Tones)
        {
            sb.Append(_localization.Format("AiChat_ProposedSection", tone.Name, Clock(tone.Window.PlacedMs),
                AiToneText.Chain(tone.Proposed))).Append('\n');
        }

        var refused = review.Result.Rejected.Count;
        if (refused > 0)
        {
            sb.Append(_localization.Plural("AiTones_FailedOne", "AiTones_Failed", refused)).Append('\n');
            foreach (var rejection in review.Result.Rejected) sb.Append("• ").Append(rejection.Reason).Append('\n');
        }

        sb.Append(_localization["AiChat_TonesReviewHint"]);
        return sb.ToString();
    }

    /// <summary>What one section changes, in the review card's own words and order
    /// (<c>AiChartReviewViewModel.RowDetail</c>): a count of zero is left out.</summary>
    private string Changes(AiChartReviewChunk chunk)
    {
        var parts = new List<string>(2);
        if (chunk.HandPositionsChanged > 0)
            parts.Add(_localization.Plural("AiChartReview_AnchorsOne", "AiChartReview_Anchors", chunk.HandPositionsChanged));
        if (chunk.Changes.ChordsRefingered > 0)
        {
            parts.Add(_localization.Plural("AiChartReview_FingeringsOne", "AiChartReview_Fingerings",
                chunk.Changes.ChordsRefingered));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>m:ss, the way the timeline reads a time.</summary>
    private static string Clock(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }
}
