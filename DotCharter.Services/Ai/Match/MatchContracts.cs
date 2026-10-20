using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Ai.Match;

// "Match from audio" — the contract between five parallel lanes:
//   eval    : IToneSimilarity + IToneFeatureExtractor + IRigRenderer + IStemIsolator
//             implementations, and the audio-level eval over the local corpus
//   data    : IRigSampler implementation + the dataset render tool (tools/rig-dataset)
//   search  : IToneMatcher implementation (retrieval library + CMA-ES refinement)
//   model   : IRigEstimator implementation (ONNX inference over a trained estimator)
//             + tools/rig-model (python training)
//   ui      : the "Match from audio" action in the tone-design panel
// Interfaces and records only; every lane codes against these and fakes the rest
// until the merge. Additive changes only — never rename or remove a member.

/// <summary>Mono PCM at a known rate. Lanes exchange audio as this, never as files.</summary>
public sealed record MonoAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

/// <summary>A fixed-length feature vector describing a tone independently of the
/// notes played (the "content-invariant" embedding: multi-scale spectral statistics,
/// MFCC statistics, dynamics). Same extractor on both sides of every comparison.</summary>
public sealed record ToneFeatures(float[] Vector, string ExtractorId);

public interface IToneFeatureExtractor
{
    string Id { get; }
    ToneFeatures Extract(MonoAudio audio);
}

/// <summary>Distance between two tones, 0 = identical. Lower is better everywhere.</summary>
public interface IToneSimilarity
{
    IToneFeatureExtractor Extractor { get; }
    double Distance(ToneFeatures a, ToneFeatures b);
}

/// <summary>Renders a dry DI recording through a rig, offline and deterministic
/// (wraps the engine's ProcessOffline). Never called on the audio thread.</summary>
public interface IRigRenderer
{
    Task<MonoAudio> RenderAsync(MonoAudio di, DcRigPreset rig, CancellationToken ct);
}

/// <summary>Isolates the guitar from a song (wraps the stem-separation services and
/// their cache). Returns the guitar stem for the requested span.</summary>
public interface IStemIsolator
{
    Task<MonoAudio> IsolateGuitarAsync(string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct);

    /// <summary>
    ///     Any of the <see cref="ToneStems" /> for the requested span. ADDITIVE
    ///     (sections lane): a section names its own stem, so a bass part
    ///     and a guitar part of one song are two targets out of one separation.
    ///     <para>
    ///         <b>The default implementation is deliberately narrow</b> — it forwards
    ///         <see cref="ToneStems.Guitar" /> to <see cref="IsolateGuitarAsync" />
    ///         and THROWS for anything else, rather than quietly handing back a
    ///         guitar. A fake, or an isolator that cannot reach the other stems, must
    ///         say so: a bass section scored against a guitar stem produces a
    ///         plausible distance and a wrong rig, which is the failure this lane
    ///         exists to avoid.
    ///     </para>
    /// </summary>
    /// <param name="songAudioPath">The song's audio file.</param>
    /// <param name="stem">One of <see cref="ToneStems" />.</param>
    /// <param name="from">Start of the span.</param>
    /// <param name="to">End of the span; must be after <paramref name="from" />.</param>
    /// <param name="status">Progress lines; may be null.</param>
    /// <param name="ct">Cancels the separation and the decode.</param>
    /// <exception cref="NotSupportedException">This isolator cannot reach that stem.</exception>
    Task<MonoAudio> IsolateAsync(
        string songAudioPath, string stem, TimeSpan from, TimeSpan to, IProgress<string>? status,
        CancellationToken ct) =>
        string.Equals(stem, ToneStems.Guitar, StringComparison.OrdinalIgnoreCase)
            ? IsolateGuitarAsync(songAudioPath, from, to, status, ct)
            : throw new NotSupportedException(
                $"This isolator only reaches '{ToneStems.Guitar}', not '{stem}'.");
}

/// <summary>Draws realistic rigs: chain-length prior, family order prior, knob priors
/// per module that produce tones a player would use, optional NAM captures as the
/// amp. Deterministic for a seed. Used by the dataset tool AND the retrieval library.</summary>
public interface IRigSampler
{
    DcRigPreset Sample(Random rng, RigSamplingProfile profile);
}

/// <summary>What kind of rigs to draw.</summary>
public sealed record RigSamplingProfile(
    int MinModules = 1,
    int MaxModules = 7,
    bool AllowBass = true,
    bool AllowNamCaptures = false,
    IReadOnlyList<string>? OnlyTypeIds = null)
{
    /// <summary>Which player archetype to draw (see <see cref="RigProfileKinds"/>).
    /// <c>mixed</c> — the default — draws the kind itself from the archetype weights,
    /// which is what the training corpus wants. ADDITIVE (data lane):
    /// an init-only property, so the positional constructor is unchanged.</summary>
    public string Kind { get; init; } = RigProfileKinds.Mixed;

    /// <summary>Directory of <c>.nam</c> captures to draw <c>namamp.modelPath</c> from.
    /// Ignored unless <see cref="AllowNamCaptures"/> is true; when it is true and this
    /// is null or empty, <c>namamp</c> is not sampled at all — a capture-less
    /// <c>namamp</c> is a label the renderer cannot honour.</summary>
    public string? NamCaptureDirectory { get; init; }
}

/// <summary>The archetypes <see cref="IRigSampler"/> draws. Strings rather than an enum
/// so the dataset tool's <c>--profile</c> flag, the manifest and this vocabulary are one
/// thing, and a new archetype stays an additive change.</summary>
public static class RigProfileKinds
{
    /// <summary>Draw the archetype itself, from the corpus weights. The default.</summary>
    public const string Mixed = "mixed";

    /// <summary>Little or no gain: compressor/opto, a clean amp, a cab, ambience.</summary>
    public const string Clean = "clean";

    /// <summary>Edge of breakup to classic rock: one drive into a driven amp.</summary>
    public const string Crunch = "crunch";

    /// <summary>Modern high gain: gate, boost/distortion, a hot amp, a tight cab.</summary>
    public const string HighGain = "highgain";

    /// <summary>Bass: <c>bassamp</c>/<c>bassod</c>, never a guitar amp.</summary>
    public const string Bass = "bass";

    /// <summary>Post/ambient: modulation, delay and reverb carry the sound.</summary>
    public const string Ambient = "ambient";

    /// <summary>No amp at all — pedals straight to the desk, or an acoustic preamp.</summary>
    public const string Direct = "direct";

    /// <summary>Every archetype except <see cref="Mixed"/>.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Clean, Crunch, HighGain, Bass, Ambient, Direct];
}

/// <summary>A prior for the matcher: what the text model (or the user) thinks the rig
/// is. The matcher may keep it, replace it, or refine it.</summary>
public sealed record ToneMatchPrior(DcRigPreset Rig, string Source);

public sealed record ToneMatchRequest(
    MonoAudio Target,
    MonoAudio ReferenceDi,
    ToneMatchPrior? Prior,
    int Candidates = 5,
    TimeSpan? Budget = null)
{
    /// <summary>
    ///     Which instrument the target IS, when the caller knows — one of
    ///     <see cref="ToneStems" />, or null when it does not. ADDITIVE (sections
    ///     lane): an init-only property, so the positional constructor is
    ///     unchanged and every existing caller keeps compiling.
    ///     <para>
    ///         <b>It biases retrieval; it never filters the answer.</b> A bass part
    ///         and a guitar part occupy different regions of the feature space, and
    ///         the sampled library is drawn from the MIXED archetype — so the nearest
    ///         rows to a bass stem are frequently guitar amps that happen to be dark.
    ///         A hint adds the best bass-family rows as extra SEEDS rather than
    ///         removing anything, because a hint is knowledge about the instrument
    ///         and not a measurement of the tone.
    ///     </para>
    /// </summary>
    public string? StemHint { get; init; }

    /// <summary>
    ///     Whether <see cref="Target" /> is a RENDER of <see cref="ReferenceDi" />
    ///     through some rig, as opposed to a separated stem of a recording. ADDITIVE
    ///     (search lane): an init-only property defaulting to
    ///     <c>false</c> — the STEM case, which is what the panel and the set matcher
    ///     pass.
    ///     <para>
    ///         <b>It gates the raw-audio term.</b> The matcher adds
    ///         <c>SpectralWeight × MultiScaleSpectralDistance(wet, target)</c> beside
    ///         the feature distance, and that loss is a sample-aligned spectral
    ///         comparison: it is only meaningful between two renders of the SAME dry
    ///         take. <c>ToneSimilarityTests.MultiScaleSpectral_SeparatesRigsOnOneTake<!--
    ///         -->AndIsMeaninglessAcrossTakes</c> demonstrates the failure directly —
    ///         it rates the same rig on a different riff as WORSE than a different rig
    ///         on the same riff. Against a separated stem of a record nobody played
    ///         into this app, the term is noise with a weight on it, and the first
    ///         end-to-end eval carried it on every row.
    ///     </para>
    ///     <para>
    ///         True only where the caller KNOWS it rendered the target itself: the
    ///         hidden-rig recovery evals, and any A/B that renders both sides.
    ///     </para>
    /// </summary>
    public bool TargetIsRender { get; init; }
}

/// <summary>One candidate: the rig, its distance to the target, and how it was found.
/// <para>
/// <see cref="Distance" /> is the MEASURED audio distance and nothing else — it is
/// the number that shares a scale with
/// <see cref="ToneMatchResult.TargetSelfDistanceFloor" />, and the one a UI or an
/// eval may compare against it. What ORDERS the list is
/// <see cref="RankingDistance" />.
/// </para></summary>
public sealed record ToneMatchCandidate(DcRigPreset Rig, double Distance, string Origin)
{
    private readonly double? _ranking;

    /// <summary>
    ///     How far this rig is from the distribution <see cref="IRigSampler" /> draws
    ///     from, in <c>[0,1]</c> — 0 for a board a player would have built, 1 for one
    ///     the sampler could not produce at all. ADDITIVE (search lane):
    ///     an init-only property, so the positional constructor is unchanged.
    /// </summary>
    public double PlausibilityPenalty { get; init; }

    /// <summary>
    ///     What the candidates are SORTED by:
    ///     <c><see cref="Distance" /> × (1 + w × <see cref="PlausibilityPenalty" />)</c>.
    ///     Defaults to <see cref="Distance" />, so a candidate built by a caller that
    ///     knows nothing about plausibility ranks on audio alone.
    ///     <para>
    ///         <b>Two numbers rather than one, deliberately.</b> The audio distance
    ///         has a SCALE — the target's own self-distance floor — and every UI
    ///         percentage, every eval table and every "did refinement beat its start
    ///         point?" comparison rests on it. Folding a structural prior into that
    ///         number would make a rig look further from the target than it measured,
    ///         which is not what the prior is saying. The prior belongs to the
    ///         CHOICE, not to the measurement.
    ///     </para>
    /// </summary>
    public double RankingDistance
    {
        get => _ranking ?? Distance;
        init => _ranking = value;
    }

    /// <summary>
    ///     The honest SCORE: a percentage against two measured anchors, or a named
    ///     reason there is none. ADDITIVE: an init-only property, so
    ///     the positional constructor is unchanged and a caller that builds a candidate
    ///     by hand gets <c>null</c>.
    ///     <para>
    ///         <b>Null means the run did not score this candidate</b> — a fake matcher,
    ///         a hand-built candidate — and is NOT the same as
    ///         <c>Score.Percent is null</c>, which means the run tried and could not
    ///         justify a number. A UI must tell those apart; see
    ///         <c>Similarity.MatchScale</c> for why there are three states.
    ///     </para>
    /// </summary>
    public Similarity.MatchScore? Score { get; init; }

    /// <summary>The candidate render's feature vector, for the persisted artefact
    /// and for inspecting a match after the fact. ADDITIVE (W5b); null when the
    /// producer did not keep it.</summary>
    public float[]? Features { get; init; }
}

/// <summary>What a match produced: the candidates (ordered by
/// <see cref="ToneMatchCandidate.RankingDistance" />), the floor, the wall time and
/// the notes.</summary>
/// <param name="Candidates">Best first, capped at the request's count.</param>
/// <param name="TargetSelfDistanceFloor">
///     What the target scores against ITSELF through the same pipeline — the best a
///     match can physically reach, and the SCALE
///     <see cref="ToneMatchCandidate.Distance" /> is read against.
///     <para>
///         <b><see cref="double.NaN" /> means "no floor known"</b> and a consumer
///         must fall back rather than divide: the span was too short to hold two
///         disjoint windows of the matcher's self-distance window. It is never 0 for that
///         case, which is a legal floor value meaning "perfectly
///         self-similar" — the wrong answer for a right-looking reason.
///     </para>
/// </param>
/// <param name="Elapsed">Wall time for the whole match.</param>
/// <param name="Notes">Human-readable trail: the plan, the budget spent, which
/// candidates were marked down and why.</param>
public sealed record ToneMatchResult(
    IReadOnlyList<ToneMatchCandidate> Candidates,
    double TargetSelfDistanceFloor,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    ///     The TARGET's feature vector — the other half of every distance in this
    ///     result. ADDITIVE (W5b). Null when the producer did not keep it.
    /// </summary>
    public float[]? TargetFeatures { get; init; }

    /// <summary>
    ///     The information-free reference rig's feature vector — the 0 % anchor, kept
    ///     so a stored result can be RE-SCORED rather than merely re-read. ADDITIVE
    ///     (W5b). Null when no anchor render was affordable.
    /// </summary>
    public float[]? ReferenceFeatures { get; init; }

    /// <summary>
    ///     What the information-free reference rig (<c>Similarity.MatchScale
    ///     .ReferenceRig</c>) scored against the same target — the 0 % end of the
    ///     scale. NaN when no anchor render was affordable. ADDITIVE (W5b).
    /// </summary>
    public double ReferenceDistance { get; init; } = double.NaN;

    /// <summary>
    ///     Where the run's feature vectors and per-block breakdown were written, so a
    ///     result can be inspected after the fact. Null when nothing was persisted
    ///     (no store, or the write failed — a match is never lost to its own
    ///     bookkeeping). ADDITIVE (W5b).
    /// </summary>
    public string? ArtifactPath { get; init; }
}

/// <summary>Finds rigs that make the reference DI sound like the target: retrieval
/// over a pre-rendered library, then knob refinement over the real renderer.</summary>
public interface IToneMatcher
{
    Task<ToneMatchResult> MatchAsync(ToneMatchRequest request, IProgress<string>? status, CancellationToken ct);
}

/// <summary>A trained estimator's one-shot guess from wet audio: per-family presence,
/// a chain, and knob values — used as a prior / initialiser for the matcher.</summary>
public sealed record RigEstimate(DcRigPreset Rig, IReadOnlyDictionary<string, float> FamilyPresence, float Confidence, string ModelId);

public interface IRigEstimator
{
    bool IsAvailable { get; }
    Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct);
}

/// <summary>The dataset manifest row the data tool writes and the trainer reads:
/// one wet clip, its DI source, the exact rig, and how it was augmented. Stored as
/// JSON lines next to the audio; paths relative to the manifest.</summary>
public sealed record RigDatasetRow(
    string ClipPath,
    string DiPath,
    DcRigPreset Rig,
    IReadOnlyList<string> Augmentations,
    int SampleRate,
    float Seconds,
    long Seed);
