namespace DotCharter.Services.Ai.Chart;

/// <summary>What the AI chart pass is asked to fix.</summary>
public enum AiChartJob
{
    /// <summary>Fret-hand positions AND chord fingering, as ONE job: they decide each
    /// other (when the anchor matches the finished chart the fingering matches
    /// 80% of the time, when it does not, 0%).</summary>
    FhpAndFingering = 0,

    /// <summary>Chord-template names, in a chosen <see cref="ChordNamingStyle" /> — the same
    /// style the chord panel's namer uses (Core).</summary>
    ChordNames = 1,

    /// <summary>Fret-hand positions ONLY: the model proposes anchors and is never asked for
    /// fingers. A FREE chord fingering under a moved anchor is then made consistent by the
    /// deterministic <c>ChordFingeringFollower</c>, in the same undo node; an authored one
    /// pins its anchor (a reply that contradicts it introduces E04 and is rejected).</summary>
    FhpOnly = 2,

    /// <summary>Chord fingering ONLY: the anchors are input and stay fixed; the model
    /// proposes fingers, and a fingering that contradicts the anchor in force (E04) rejects
    /// the chunk.</summary>
    FingeringOnly = 3,

    /// <summary>SECTIONS and PHRASES only (the AI chat): the model adds, renames, moves or
    /// removes section and phrase markers on the beat grid; notes, anchors and fingering are
    /// never offered. Not a chunked pass: one request over the scope
    /// (<see cref="AiStructurePass" />), its own schema, reader and applier.</summary>
    SectionsAndPhrases = 4,

    /// <summary>ASK / EXPLAIN (the AI chat): the model answers a question about the chart in
    /// TEXT ("why 1·3 here?", "what's wrong with bar 34?") from the scope's listing and the chart
    /// quality report's findings. It proposes nothing and nothing is held or written: one request
    /// (<see cref="AiExplainPass" />), a reply schema with one text field, any other field ignored.</summary>
    Explain = 5,

    /// <summary>TONES (the AI chat): a Rocksmith tone for each tone definition the scope's
    /// placed tone changes name, from the song's audio windows (cached stems only, never a
    /// separation) and the chat's words, through the W6 generator and its closed-catalogue gate
    /// (<c>Ai/Generate/</c>). Held per tone for the review card; Apply writes the accepted
    /// <see cref="ToneDefinition" /> bodies and nothing else.</summary>
    Tones = 6
}

/// <summary>What each <see cref="AiChartJob" /> lets the model change.</summary>
public static class AiChartJobs
{
    /// <summary>True when the model proposes anchors.</summary>
    public static bool ProposesAnchors(this AiChartJob job) => job is AiChartJob.FhpAndFingering or AiChartJob.FhpOnly;

    /// <summary>True when the model proposes chord fingers.</summary>
    public static bool ProposesFingers(this AiChartJob job)
        => job is AiChartJob.FhpAndFingering or AiChartJob.FingeringOnly;

    /// <summary>True when the reply lists chords at all (every chunked job but FHP only).</summary>
    public static bool ListsChords(this AiChartJob job)
        => job is not (AiChartJob.FhpOnly or AiChartJob.SectionsAndPhrases or AiChartJob.Explain or AiChartJob.Tones);
}

/// <summary>
///     One sound of the level, as the pass names it to the model.
///     <para>
///         <see cref="Id" /> is <c>s</c> + the sound's index in the level's time-sorted
///         sound list, so it is stable for the whole pass. <see cref="ChordIndex" /> is the
///         chord's index in <c>ChartLevel.Chords</c> (−1 for a note): a clone keeps that
///         order, which is how a plan computed on a clone finds the live chord again.
///     </para>
/// </summary>
public sealed record AiChartSound(
    string Id,
    FractionalPosition Position,
    int Ms,
    GuitarNote? Note,
    Chord? Chord,
    int ChordIndex);

/// <summary>
///     One request's worth of the level: the CORE sounds the reply may change, plus a
///     little context either side that the model sees and the pass never applies.
///     <para>
///         <see cref="Start" /> is where the chunk's anchors begin — the phrase marker when
///         the chunk opens a phrase, else its first sound — and <see cref="End" /> is where
///         they stop (exclusive): the next chunk's start, or, for the last chunk, the next
///         phrase marker or sound after it. Null means "to the end of the level". Every
///         anchor inside <c>[Start, End)</c> is replaced by the reply's; none outside it is
///         touched.
///     </para>
/// </summary>
public sealed record AiChartChunk(
    int Index,
    FractionalPosition Start,
    FractionalPosition? End,
    int StartMs,
    IReadOnlyList<AiChartSound> Core,
    IReadOnlyList<AiChartSound> Before,
    IReadOnlyList<AiChartSound> After,
    string Phrases)
{
    /// <summary>True when <paramref name="position" /> lies inside <c>[Start, End)</c>.</summary>
    public bool Contains(FractionalPosition position)
        => position >= Start && (End is not { } end || position < end);
}

/// <summary>
/// What the charter asked for in the AI chat: the instruction and a bounded
///     excerpt of the conversation before it, sent to the model as its own prompt section
///     after the chunk listing (<see cref="AiChartPrompt.InstructionSection" />). It can only
///     STEER the job: the reply schema, the validator and the rule gate still decide what may
///     change, so an instruction to move a note is refused like any other such reply.
/// </summary>
/// <param name="Text">The instruction, as the charter typed it.</param>
/// <param name="Conversation">The recent turns, one per line and oldest first, or null.</param>
public sealed record AiChartInstruction(string Text, string? Conversation = null);

/// <summary>An anchor the reply asks for: <paramref name="At" /> is <c>"start"</c> or a core
/// sound id.</summary>
public sealed record AiChartReplyAnchor(string At, int Fret, int Width);

/// <summary>One chord of the reply. <paramref name="Frets" /> and <paramref name="Ms" /> are
/// ECHOES the validator compares with the chart; <paramref name="Fingers" /> is null for the
/// name job and <paramref name="Name" /> null for the fingering job.</summary>
public sealed record AiChartReplyChord(
    string Id,
    int Ms,
    IReadOnlyList<int> Frets,
    IReadOnlyList<int>? Fingers,
    string? Name);

/// <summary>A decoded reply. <paramref name="Anchors" /> is null for the name job.</summary>
public sealed record AiChartReply(
    IReadOnlyList<AiChartReplyAnchor>? Anchors,
    IReadOnlyList<AiChartReplyChord> Chords);

/// <summary>A new anchor of an accepted chunk.</summary>
public sealed record AiAnchorPlacement(FractionalPosition Position, int Fret, int Width);

/// <summary>
///     One chord whose template changes. The chord is found again by
///     <paramref name="ChordIndex" /> and checked against <paramref name="Position" />,
///     <paramref name="FretSignature" /> and <paramref name="OldTemplateId" /> before
///     anything is written, so a chart edited while the model was thinking is never
///     written over.
/// </summary>
public sealed record AiChordChange(
    int ChordIndex,
    FractionalPosition Position,
    string FretSignature,
    int OldTemplateId,
    IReadOnlyDictionary<int, int> Fingers,
    string Name);

/// <summary>A core chord the plan did not change but depends on (FHP only: the follower
/// re-fingers it from the new anchors). Re-checked like an <see cref="AiChordChange" />
/// before the live write, so a chord edited while the model was thinking makes the plan
/// stale.</summary>
public sealed record AiChordGuard(int ChordIndex, FractionalPosition Position, string FretSignature, int TemplateId);

/// <summary>
///     What an ACCEPTED chunk changes. <paramref name="Anchors" /> is the complete
///     replacement set for <c>[Start, End)</c>, or null when the anchors stay as they are
///     (the name and fingering-only jobs, or a reply that restated them).
///     <paramref name="FingersFollowAnchors" /> (FHP only) makes the apply run
///     <c>ChordFingeringFollower</c> over the chords the new anchors govern.
///     <paramref name="SeenAnchors" /> are the anchors the model was shown inside
///     <c>[Start, End)</c> (every job but the name job): the live write re-checks them like
///     <paramref name="Guards" />, so a hand position the charter moved while the proposal
/// waited for review makes the plan stale instead of being written over. Null = no
///     check.
/// </summary>
public sealed record AiChartChunkPlan(
    int ChunkIndex,
    FractionalPosition Start,
    FractionalPosition? End,
    IReadOnlyList<AiAnchorPlacement>? Anchors,
    IReadOnlyList<AiChordChange> Chords,
    bool FingersFollowAnchors = false,
    IReadOnlyList<AiChordGuard>? Guards = null,
    IReadOnlyList<AiAnchorPlacement>? SeenAnchors = null);

/// <summary>Why a chunk was REJECTED (never repaired): a stable code for counting and a
/// sentence for the log.</summary>
public sealed record AiChartRejection(int ChunkIndex, string Code, string Reason);

/// <summary>The rejection codes, stable for the log, the summary and the eval.</summary>
public static class AiChartRejectionCodes
{
    public const string Malformed = "malformed";
    public const string UnknownSound = "unknown-sound";
    public const string ContextSound = "context-sound";
    public const string ChordCount = "chord-count";
    public const string FretsChanged = "frets-changed";
    public const string TimingChanged = "timing-changed";
    public const string FingerInvalid = "finger-invalid";
    public const string FingerOnOpenString = "finger-on-open-string";
    public const string FingerMissing = "finger-missing";
    public const string AnchorPlacement = "anchor-placement";
    public const string AnchorAtStart = "anchor-start";
    public const string AnchorBelowCapo = "anchor-below-capo";
    public const string AnchorRange = "anchor-range";
    public const string AnchorContradicted = "anchor-contradicted";
    public const string NameInvalid = "name-invalid";
    public const string Rules = "rules";

    /// <summary>Fix with AI: the reply leaves a finding it was asked to remove.</summary>
    public const string FindingLeft = "finding-left";
    public const string ModelFailed = "model-failed";

    /// <summary>The structure job: the reply names something other than a section or a
    /// phrase (a note, a fret, an anchor, an unknown field or operation).</summary>
    public const string NotStructure = "not-structure";

    /// <summary>A section name outside Rocksmith's vocabulary (<c>RsSectionCatalog</c>).</summary>
    public const string SectionName = "section-name";

    /// <summary>A phrase name Rocksmith would rewrite (<c>RsPhraseName.IsLegal</c>).</summary>
    public const string PhraseName = "phrase-name";

    /// <summary>A position off the beat grid: no such bar or beat, or a section not on a bar line.</summary>
    public const string OffGrid = "off-grid";

    /// <summary>A change outside the scope the chat chose.</summary>
    public const string OutOfScope = "out-of-scope";

    /// <summary>An id the listing never named.</summary>
    public const string UnknownMarker = "unknown-marker";

    /// <summary>A phrase from the imported phrase list (<c>Arrangement.PhraseIterations</c>), which
    /// the structure job lists but may not change.</summary>
    public const string ImportedMarker = "imported-marker";

    /// <summary>Two changes on one marker, or a new marker where one of its kind already is.</summary>
    public const string Conflict = "conflict";
}

/// <summary>What one pass did, before anything is written. <paramref name="Retried" /> counts
/// the chunks asked a second time, with the first reply's rejection in the prompt;
/// <paramref name="Rejected" /> holds the ones whose retry was rejected too.
/// <paramref name="Structure" /> is the structure job's accepted changes, null for every
/// other job; its rejections are in <paramref name="Rejected" />, indexed by change.
/// <c>Leftovers</c> are the gate's findings still in the pass's range with every
/// accepted chunk applied: what accepting the whole review would leave. Null when not computed.</summary>
public sealed record AiChartPassResult(
    AiChartJob Job,
    int ChunkCount,
    IReadOnlyList<AiChartChunkPlan> Accepted,
    IReadOnlyList<AiChartRejection> Rejected,
    int InputTokens,
    int OutputTokens,
    string ModelId,
    TimeSpan Elapsed,
    int Retried = 0,
    IReadOnlyList<AiStructureChange>? Structure = null)
{
    /// <summary>See the record's summary: what accepting every held chunk would leave in range.</summary>
    public IReadOnlyList<ArrangementIssue>? Leftovers { get; init; }
}

/// <summary>
/// A finding a pass is asked to REMOVE (Fix with AI): its code, time and string, the way
///     the rule gate matches findings. A chunk that reaches it and leaves it is rejected
///     (<see cref="AiChartRejectionCodes.FindingLeft" />).
/// </summary>
/// <param name="Code">The finding's code ("I20").</param>
/// <param name="TimeMs">Its time, or null for an untimed finding (I26, "no END phrase": only the
/// sections &amp; phrases job, which judges the whole arrangement, can aim at one).</param>
/// <param name="StringIndex">Its string, or null when it names none.</param>
public sealed record AiChartFindingTarget(string Code, double? TimeMs, int? StringIndex = null)
{
    /// <summary>The target for <paramref name="issue" />.</summary>
    public static AiChartFindingTarget Of(ArrangementIssue issue) => new(issue.Code, issue.TimeMs, issue.StringIndex);
}

/// <summary>What an apply changed: the numbers the status rail reports.</summary>
public sealed record AiChartApplySummary(
    int AnchorsChanged,
    int ChordsRefingered,
    int NamesChanged,
    int TemplatesMinted,
    int HandShapesRepointed,
    int StaleChunks)
{
    /// <summary>True when the apply would change nothing at all.</summary>
    public bool IsEmpty => AnchorsChanged == 0 && ChordsRefingered == 0 && NamesChanged == 0;
}

/// <summary>
/// The live objects an apply would change — what the review marks on the 2D
///     timeline and the song map. Read off the SAME composition <see cref="AiChartApplier.Build" />
///     writes, so the marks cannot disagree with the write.
///     <para>
///         <paramref name="Chords" /> and <paramref name="HandShapes" /> are the live chords
///         and hand shapes repointed at another template. <paramref name="RemovedAnchors" />
///         are the live anchors the write removes and does not put back identically (same
///         position, fret and width); <paramref name="AddedAnchors" /> are the new anchors no
///         removed one already was — proposals, not yet on the chart.
///     </para>
/// </summary>
public sealed record AiChartApplyTargets(
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes,
    IReadOnlyList<FretAnchor> RemovedAnchors,
    IReadOnlyList<FretAnchor> AddedAnchors)
{
    /// <summary>Nothing to mark.</summary>
    public static readonly AiChartApplyTargets None = new([], [], [], []);

    /// <summary>How many objects change.</summary>
    public int Count => Chords.Count + HandShapes.Count + RemovedAnchors.Count + AddedAnchors.Count;
}
