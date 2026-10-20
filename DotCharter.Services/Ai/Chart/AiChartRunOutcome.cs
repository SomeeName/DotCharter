namespace DotCharter.Services.Ai.Chart;

/// <summary>How a run of <see cref="AiChartPassRun" /> ended.</summary>
public enum AiChartRunStatus
{
    /// <summary>Proposals are HELD in <see cref="AiChartPassRun.Review" /> for the review card.</summary>
    Held = 0,

    /// <summary>Another run was in flight; this one did nothing.</summary>
    Busy,

    /// <summary>No project, arrangement or level to run on.</summary>
    NoProject,

    /// <summary>The project has no beat map.</summary>
    NoBeatMap,

    /// <summary>No AI provider is configured (Settings ▸ AI).</summary>
    NotConfigured,

    /// <summary>The scope holds nothing the job can change.</summary>
    NothingToDo,

    /// <summary>The project, arrangement or level went away while the model was thinking.</summary>
    ProjectChanged,

    /// <summary>The checks refused every section.</summary>
    AllRejected,

    /// <summary>Every accepted section restated the chart.</summary>
    NoChange,

    /// <summary>Cancelled by the rail's pill or the caller.</summary>
    Cancelled,

    /// <summary>The run threw; <see cref="AiChartRunOutcome.Message" /> says why.</summary>
    Failed,

    /// <summary>An ask / explain run answered: <see cref="AiChartRunOutcome.Message" /> is the
    /// model's text. Nothing is held and nothing is written.</summary>
    Answered
}

/// <summary>
///     What a run did: its status, the line it put on the status rail (localized; the AI
///     chat repeats it as its reply when nothing is held), and, when proposals are held, the
///     pass and the review the card shows. Nothing is ever written by a run.
/// </summary>
public sealed record AiChartRunOutcome(
    AiChartRunStatus Status,
    string Message,
    AiChartPassResult? Result = null,
    AiChartReview? Review = null)
{
    /// <summary>The beat map the pass ran on (the bars of the findings it left); null when no
    /// pass ran.</summary>
    public IList<BeatMarker>? Beats { get; init; }
}

/// <summary>
/// An instructed run: the AI chat's instruction on a scope the chat chose rather
///     than the editor's selection. Same pass, checks, retry and review as the menu verbs.
/// </summary>
/// <param name="Job">FHP + fingering, FHP only, fingering only, sections and phrases, or
/// <see cref="AiChartJob.Explain" /> (a text answer, no proposals); never the name job.</param>
/// <param name="Instruction">The instruction and the conversation excerpt.</param>
/// <param name="Arrangement">The arrangement to run on (one of the open project's), or null
/// for the active one.</param>
/// <param name="From">First sound position of the scope (inclusive), or null for the start.</param>
/// <param name="To">Last sound position of the scope (inclusive), or null for the end.</param>
/// <param name="ModelId">The model the chat picked, or null for the one Settings ▸ AI names.</param>
/// <param name="ToneChanges">The placed tone changes the Tones job generates for; null for
/// every other job.</param>
public sealed record AiChartInstructedRun(
    AiChartJob Job,
    AiChartInstruction Instruction,
    Arrangement? Arrangement = null,
    FractionalPosition? From = null,
    FractionalPosition? To = null,
    string? ModelId = null,
    IReadOnlyList<ToneChange>? ToneChanges = null)
{
    /// <summary>Fix with AI: findings each chunk reaching them must remove; null for none.</summary>
    public IReadOnlyList<AiChartFindingTarget>? Targets { get; init; }
}

/// <summary>
/// A review was ACCEPTED and written: the review, and the gate's findings still in its pass's
///     range on the live chart afterwards — what the AI chat reports back with "Ask the AI to fix these".
/// </summary>
public sealed record AiChartApplied(AiChartReview Review, IReadOnlyList<ArrangementIssue> Leftovers);
