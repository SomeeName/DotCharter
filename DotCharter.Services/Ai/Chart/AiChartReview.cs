using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Ai.Chart;

/// <summary>What the charter decided about one proposed section.</summary>
public enum AiChartDecision
{
    /// <summary>Not decided yet: marked, not written.</summary>
    Pending = 0,

    /// <summary>Written by the next apply.</summary>
    Accepted = 1,

    /// <summary>Never written; its marks go away.</summary>
    Rejected = 2
}

/// <summary>
///     One section the pass proposes to change, as the review lists it: the plan, where it
///     sits in the song, what it alone would change on the live chart, and the charter's
///     decision.
/// </summary>
public sealed class AiChartReviewChunk
{
    internal AiChartReviewChunk(AiChartChunkPlan plan, double startMs, double? endMs, string phrases,
        AiChartApplySummary changes, AiChartApplyTargets targets)
    {
        Plan = plan;
        StartMs = startMs;
        EndMs = endMs;
        Phrases = phrases;
        Changes = changes;
        HandPositionsChanged = Math.Max(targets.RemovedAnchors.Count, targets.AddedAnchors.Count);
    }

    /// <summary>The accepted plan.</summary>
    public AiChartChunkPlan Plan { get; }

    /// <summary>Where the section starts, in milliseconds.</summary>
    public double StartMs { get; }

    /// <summary>Where it ends (exclusive), or null for "to the end of the level".</summary>
    public double? EndMs { get; }

    /// <summary>The phrase names the section covers, or empty.</summary>
    public string Phrases { get; }

    /// <summary>What this section alone would change on the live chart, counted when the
    /// review was held.</summary>
    public AiChartApplySummary Changes { get; }

    /// <summary>The hand positions this section alone changes BY VALUE (an anchor the reply
    /// restated is not one), as the row counts them.</summary>
    public int HandPositionsChanged { get; }

    /// <summary>The charter's decision.</summary>
    public AiChartDecision Decision { get; internal set; }
}

/// <summary>
/// One proposed section or phrase change, as the review lists it: the change, where it
///     shows in the song, and the charter's decision.
/// </summary>
public sealed class AiStructureReviewItem
{
    internal AiStructureReviewItem(AiStructureChange change, double startMs, double? fromMs)
    {
        Change = change;
        StartMs = startMs;
        FromMs = fromMs;
    }

    /// <summary>The validated change.</summary>
    public AiStructureChange Change { get; }

    /// <summary>Where it shows (an add's or a move's destination, else the marker), in milliseconds.</summary>
    public double StartMs { get; }

    /// <summary>Where the marker it acts on is now (rename, move, remove), or null for an add.</summary>
    public double? FromMs { get; }

    /// <summary>The charter's decision.</summary>
    public AiChartDecision Decision { get; internal set; }
}

/// <summary>
/// The AI chart pass's proposals, HELD for review before anything is written.
///     <para>
///         Every section starts <see cref="AiChartDecision.Pending" />. The marks
///         (<see cref="Marks" />) are what an apply of every section the charter has not
///         rejected would change; the write (<see cref="AiChartPassRun.ApplyReview" />) is
///         every ACCEPTED section, as ONE undo node. Both are read off the SAME
///         <see cref="AiChartApplier" /> composition, so a mark never disagrees with the
///         write. Any subset is a valid write: each section's anchors begin at its own start
///         and are replaced only inside its own span, so leaving a neighbour out leaves that
///         neighbour's span exactly as the chart has it.
///     </para>
///     <para>
///         The review holds the LIVE arrangement and level. The chart stays editable while it
///         is open; a section whose chords or hand positions were edited meanwhile is stale
///         and left out of both the marks and the write (the plan's guards).
///     </para>
///     <para>
/// <b>The structure job</b> holds <see cref="Items" /> instead of <see cref="Chunks" />:
///         one section or phrase change each, decided one by one, marked through
///         <see cref="StructureMarks" /> and written by <see cref="AiStructureApplier" /> off the same
///         composition. Both lists are never filled at once.
///     </para>
/// </summary>
public sealed class AiChartReview
{
    private readonly IFingeringGenerationService? _fingering;

    private AiChartReview(AiChartJob job, ChartProject project, Arrangement arrangement, ChartLevel level,
        AiChartPassResult result, IReadOnlyList<AiChartReviewChunk> chunks, IFingeringGenerationService? fingering,
        IReadOnlyList<AiStructureReviewItem>? items = null, IReadOnlyList<AiToneReviewItem>? tones = null)
    {
        Job = job;
        Project = project;
        Arrangement = arrangement;
        Level = level;
        Result = result;
        Chunks = chunks;
        Items = items ?? [];
        Tones = tones ?? [];
        _fingering = fingering;
    }

    /// <summary>The review's id: what an AI chat turn links to (<c>AiChatTurn.ReviewId</c>).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The job that made the proposals.</summary>
    public AiChartJob Job { get; }

    /// <summary>The pass's range (inclusive), null = open: where the findings left after an Accept
    /// are looked for.</summary>
    public FractionalPosition? PassFrom { get; internal set; }

    /// <summary>See <see cref="PassFrom" />.</summary>
    public FractionalPosition? PassTo { get; internal set; }

    /// <summary>The project the proposals were made for.</summary>
    public ChartProject Project { get; }

    /// <summary>The LIVE arrangement they write to.</summary>
    public Arrangement Arrangement { get; }

    /// <summary>The LIVE level they write to.</summary>
    public ChartLevel Level { get; }

    /// <summary>The pass that made them (the checks' rejections, tokens, retries).</summary>
    public AiChartPassResult Result { get; }

    /// <summary>The sections that would change something, in song order.</summary>
    public IReadOnlyList<AiChartReviewChunk> Chunks { get; }

    /// <summary>The structure job's section and phrase changes, in song order; empty for
    /// every other job.</summary>
    public IReadOnlyList<AiStructureReviewItem> Items { get; }

    /// <summary>The Tones job's proposals, one per tone definition, in song order of the window they
    /// were generated from; empty for every other job.</summary>
    public IReadOnlyList<AiToneReviewItem> Tones { get; }

    /// <summary>Sections (or structure changes) the charter accepted.</summary>
    public int AcceptedCount => Count(AiChartDecision.Accepted);

    /// <summary>Sections (or structure changes) the charter rejected.</summary>
    public int RejectedCount => Count(AiChartDecision.Rejected);

    /// <summary>Sections (or structure changes) not decided yet.</summary>
    public int PendingCount => Count(AiChartDecision.Pending);

    private int Count(AiChartDecision decision)
        => Chunks.Count(c => c.Decision == decision) + Items.Count(i => i.Decision == decision) +
           Tones.Count(t => t.Decision == decision);

    /// <summary>
    ///     Holds <paramref name="result" />'s accepted plans for review on the LIVE
    ///     <paramref name="level" />. A plan that changes nothing there (it restated the chart,
    ///     or it went stale while the model was thinking) is not listed. Null when nothing is
    ///     left to review.
    /// </summary>
    /// <param name="project">The project the pass ran on.</param>
    /// <param name="arrangement">The live arrangement.</param>
    /// <param name="level">The live level.</param>
    /// <param name="beats">The beat map, for the section times.</param>
    /// <param name="result">The pass.</param>
    /// <param name="chunks">The chunks the pass cut, for the phrase names; may be empty.</param>
    /// <param name="fingering">The template resolver the write uses.</param>
    public static AiChartReview? Hold(
        ChartProject project, Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        AiChartPassResult result, IReadOnlyList<AiChartChunk> chunks, IFingeringGenerationService fingering)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fingering);

        var phrases = chunks.ToDictionary(c => c.Index, c => c.Phrases);
        var listed = new List<AiChartReviewChunk>();
        foreach (var plan in result.Accepted.OrderBy(p => p.Start))
        {
            var targets = AiChartApplier.Preview(arrangement, level, [plan], fingering, out var changes);
            if (changes.IsEmpty) continue;
            listed.Add(new AiChartReviewChunk(plan, plan.Start.GetPositionMs(beats), plan.End?.GetPositionMs(beats),
                phrases.GetValueOrDefault(plan.ChunkIndex) ?? "", changes, targets));
        }

        return listed.Count == 0
            ? null
            : new AiChartReview(result.Job, project, arrangement, level, result, listed, fingering);
    }

    /// <summary>
    /// Holds the structure job's accepted changes (<see cref="AiChartPassResult.Structure" />)
    ///     for review on the LIVE <paramref name="arrangement" />. A change that does nothing there (it
    ///     went stale while the model was thinking) is not listed. Null when nothing is left.
    /// </summary>
    public static AiChartReview? HoldStructure(
        ChartProject project, Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats, AiChartPassResult result)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(result);
        var items = new List<AiStructureReviewItem>();
        foreach (var change in (result.Structure ?? []).OrderBy(c => c.At))
        {
            AiStructureApplier.Preview(arrangement, [change], out var applied);
            if (applied == 0) continue;
            items.Add(new AiStructureReviewItem(change, change.At.GetPositionMs(beats), change.From?.GetPositionMs(beats)));
        }

        return items.Count == 0
            ? null
            : new AiChartReview(result.Job, project, arrangement, level, result, [], null, items);
    }

    /// <summary>
    /// Holds the Tones job's proposals for review. Nothing is written: the project's tone
    ///     definitions stay exactly as they are until <see cref="AiChartPassRun.ApplyReview" />. Null
    ///     when there is nothing to review.
    /// </summary>
    /// <param name="project">The live project.</param>
    /// <param name="arrangement">The arrangement whose placed tone changes were read.</param>
    /// <param name="level">The live level (the review's level; tones touch none).</param>
    /// <param name="result">The run (its rejections are the tones the gate refused).</param>
    /// <param name="tones">The proposals.</param>
    public static AiChartReview? HoldTones(ChartProject project, Arrangement arrangement, ChartLevel level,
        AiChartPassResult result, IReadOnlyList<AiToneReviewItem> tones)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(tones);
        return tones.Count == 0
            ? null
            : new AiChartReview(result.Job, project, arrangement, level, result, [], null, null,
                [.. tones.OrderBy(t => t.Window.PlacedMs)]);
    }

    /// <summary>Records the charter's decision on one proposed tone.</summary>
    /// <param name="tone">One of <see cref="Tones" />.</param>
    /// <param name="decision">The decision.</param>
    public void Decide(AiToneReviewItem tone, AiChartDecision decision)
    {
        ArgumentNullException.ThrowIfNull(tone);
        if (!Tones.Contains(tone)) throw new ArgumentException("The tone is not one of this review's.", nameof(tone));
        tone.Decision = decision;
    }

    /// <summary>The tones the next apply writes.</summary>
    public IReadOnlyList<AiToneReviewItem> AcceptedTones => Tones.Where(t => t.Decision == AiChartDecision.Accepted).ToList();

    /// <summary>Records the charter's decision on <paramref name="chunk" />.</summary>
    /// <param name="chunk">One of <see cref="Chunks" />.</param>
    /// <param name="decision">The decision.</param>
    public void Decide(AiChartReviewChunk chunk, AiChartDecision decision)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!Chunks.Contains(chunk)) throw new ArgumentException("The section is not one of this review's.", nameof(chunk));
        chunk.Decision = decision;
    }

    /// <summary>Records the charter's decision on one structure change.</summary>
    /// <param name="item">One of <see cref="Items" />.</param>
    /// <param name="decision">The decision.</param>
    public void Decide(AiStructureReviewItem item, AiChartDecision decision)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item)) throw new ArgumentException("The change is not one of this review's.", nameof(item));
        item.Decision = decision;
    }

    /// <summary>Accepts every section not decided yet; a rejected one stays rejected.</summary>
    public void AcceptAll()
    {
        foreach (var chunk in Chunks.Where(c => c.Decision == AiChartDecision.Pending))
            chunk.Decision = AiChartDecision.Accepted;
        foreach (var item in Items.Where(i => i.Decision == AiChartDecision.Pending))
            item.Decision = AiChartDecision.Accepted;
        foreach (var tone in Tones.Where(t => t.Decision == AiChartDecision.Pending))
            tone.Decision = AiChartDecision.Accepted;
    }

    /// <summary>Rejects every section not decided yet; an accepted one stays accepted —
    /// the mirror of <see cref="AcceptAll" />, so neither bulk verb undoes a row decided by hand.</summary>
    public void RejectAll()
    {
        foreach (var chunk in Chunks.Where(c => c.Decision == AiChartDecision.Pending))
            chunk.Decision = AiChartDecision.Rejected;
        foreach (var item in Items.Where(i => i.Decision == AiChartDecision.Pending))
            item.Decision = AiChartDecision.Rejected;
        foreach (var tone in Tones.Where(t => t.Decision == AiChartDecision.Pending))
            tone.Decision = AiChartDecision.Rejected;
    }

    /// <summary>The structure changes the next apply writes, in song order.</summary>
    public IReadOnlyList<AiStructureChange> AcceptedChanges =>
        Items.Where(i => i.Decision == AiChartDecision.Accepted).Select(i => i.Change).ToList();

    /// <summary>The structure changes still on the table (accepted or pending), in song order.</summary>
    public IReadOnlyList<AiStructureChange> ProposedChanges =>
        Items.Where(i => i.Decision != AiChartDecision.Rejected).Select(i => i.Change).ToList();

    /// <summary>What an apply of every structure change still on the table would change on the
    /// live ribbon: the review's section and phrase marks. None once the arrangement is gone.</summary>
    public AiStructureApplyTargets StructureMarks() => Items.Count > 0 && Project.Arrangements.Contains(Arrangement)
        ? AiStructureApplier.Preview(Arrangement, ProposedChanges, out _)
        : AiStructureApplyTargets.None;

    /// <summary>The plans the next apply writes, in song order.</summary>
    public IReadOnlyList<AiChartChunkPlan> AcceptedPlans =>
        Chunks.Where(c => c.Decision == AiChartDecision.Accepted).Select(c => c.Plan).ToList();

    /// <summary>The plans still on the table (accepted or pending), in song order.</summary>
    public IReadOnlyList<AiChartChunkPlan> ProposedPlans =>
        Chunks.Where(c => c.Decision != AiChartDecision.Rejected).Select(c => c.Plan).ToList();

    /// <summary>
    ///     What an apply of every section still on the table would change on the live chart:
    ///     the objects the review marks. <see cref="AiChartApplyTargets.None" /> once the
    ///     review's level is gone.
    /// </summary>
    public AiChartApplyTargets Marks() => _fingering is not null && IsLive(Project, Arrangement, Level)
        ? AiChartApplier.Preview(Arrangement, Level, ProposedPlans, _fingering, out _)
        : AiChartApplyTargets.None;

    /// <summary>True while <paramref name="level" /> is still part of the open
    /// <paramref name="project" />.</summary>
    internal static bool IsLive(ChartProject project, Arrangement arrangement, ChartLevel level)
        => project.Arrangements.Contains(arrangement) && arrangement.Levels.Contains(level);
}
