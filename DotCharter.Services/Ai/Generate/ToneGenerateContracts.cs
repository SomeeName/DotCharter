using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

// "Generate Rocksmith tones from placed tone changes + stems".
//
// The lane has two inputs and one output, and the seams between them are these
// interfaces:
//
//   audio  → IToneEvidenceSource  → ToneWindowFeatures   (measured, named, testable)
//   model  → IRsToneMapper        → RsTone               (features + context → GEAR)
//   both   → IRsToneGenerator     → ToneSetGenerationResult
//
// Additive only, and the reason to keep them apart is stated on each.

/// <summary>
///     <b>The seam a raw-audio arm arrives behind.</b> Today there is one source —
///     the measured <see cref="ToneWindowFeatureExtractor" /> — and the mapper is a
///     function of its named numbers. A multimodal model that listens to the window
///     itself would be a second implementation of this interface contributing its own
///     prompt text, without the mapper learning anything new.
///     <para>
///         Features-first is not a staging decision. Eleven measured numbers can be
///         asserted in a unit test, printed in a run report and disagreed with by a
///         human; "the model listened to it" cannot be any of the three.
///     </para>
/// </summary>
public interface IToneEvidenceSource
{
    /// <summary>A stable id naming this source in a tone's provenance.</summary>
    string Id { get; }

    /// <summary>Describes one window, or answers null when this source has nothing
    /// to say about it (too short, wrong instrument, unavailable model).</summary>
    /// <param name="window">The isolated stem for the window.</param>
    /// <param name="ct">Cancels the analysis.</param>
    Task<ToneWindowEvidence?> DescribeAsync(MonoAudio window, CancellationToken ct);
}

/// <summary>What one evidence source contributed: its id, the measured features when
/// it produced any, and the prompt text it wants carried.</summary>
/// <param name="SourceId">Which source.</param>
/// <param name="Features">The measured features, or null for a source that produces
/// prose only.</param>
/// <param name="PromptText">What the mapper should be told. Empty when
/// <paramref name="Features" /> carries it all.</param>
public sealed record ToneWindowEvidence(string SourceId, ToneWindowFeatures? Features, string PromptText);

/// <summary>The two generation sources, as a tone's provenance names them.</summary>
public static class ToneGenerationSources
{
    /// <summary>Measured from the isolated stem for the window.</summary>
    public const string Audio = "audio";

    /// <summary>The charter's own text: song, band, era, known rig, intent.</summary>
    public const string Context = "context";
}

/// <summary>What the mapper is asked to turn into gear.</summary>
/// <param name="Features">The measured features, or null when only context is
/// available.</param>
/// <param name="Context">The charter's prior-knowledge text, or null.</param>
/// <param name="ToneKey">The Rocksmith tone key the reply must carry.</param>
/// <param name="ToneName">The display name.</param>
/// <param name="Instrument">One of <see cref="ToneStems" />.</param>
/// <param name="SongTitle">For the model's own knowledge of the song; may be null.</param>
/// <param name="Artist">Likewise.</param>
/// <param name="Instruction">The charter's own words for these tones from the AI chat's Tones
/// task ("the chorus tone is a high-gain Mesa, darker"), or null. A prompt section of
/// its own, after the context.</param>
/// <param name="ArrangementName">The arrangement's name, so a tone NAMED after it is not taken
/// as a description of its sound (<see cref="RsToneNameHint" />); may be null.</param>
/// <param name="PreviousProposal">The Tone designer's AI creator, on Refine: the proposal being refined as
/// its manifest JSON, so the instruction changes THAT tone rather than starting over. Null otherwise.</param>
public sealed record RsToneMapRequest(
    ToneWindowFeatures? Features,
    string? Context,
    string ToneKey,
    string ToneName,
    string Instrument,
    string? SongTitle,
    string? Artist,
    string? Instruction = null,
    string? ArrangementName = null,
    string? PreviousProposal = null);

/// <summary>A mapper's answer: the tone, who produced it, and what it cost.</summary>
/// <param name="Tone">The gear. <b>Already validated against the catalogue</b> — a
/// mapper that cannot produce a valid tone throws rather than returning one.</param>
/// <param name="ModelId">The model that answered, for provenance.</param>
/// <param name="Usage">Tokens and cost.</param>
/// <param name="Sources">Which of <see cref="ToneGenerationSources" /> contributed.</param>
/// <param name="Rationale">The model's own one-line reason, or empty.</param>
public sealed record RsToneMapping(
    RsTone Tone,
    string ModelId,
    AiUsage Usage,
    IReadOnlyList<string> Sources,
    string Rationale);

/// <summary>
///     <b>The MODEL boundary of this lane.</b> Features + context in, a
///     catalogue-valid <see cref="RsTone" /> out. Everything above it — windows,
///     stems, measurement, provenance, writing — is deterministic and testable
///     without a provider; everything below it is one API call.
/// </summary>
public interface IRsToneMapper
{
    /// <summary>One line naming the provider and model, for a report header.</summary>
    string Describe();

    /// <summary>Maps one window's evidence to gear.</summary>
    /// <param name="request">The evidence.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="RsToneCatalogException">The reply named gear or a value the
    /// game does not have. <b>Never repaired</b>.</exception>
    Task<RsToneMapping> MapAsync(RsToneMapRequest request, CancellationToken ct);
}

/// <summary>A whole arrangement's tone changes, to be generated in one go.</summary>
/// <param name="SongAudioPath">The song's audio; the isolator separates it, cache-first.</param>
/// <param name="ArrangementName">For the tone keys and the report.</param>
/// <param name="Instrument">The stem this arrangement's tones are measured from, one
/// of <see cref="ToneStems" />.</param>
/// <param name="Windows">The planned windows, from <see cref="ToneWindowPlanner" />.</param>
/// <param name="Context">The charter's prior-knowledge text, or null.</param>
/// <param name="SongTitle">For the model and the report.</param>
/// <param name="Artist">Likewise.</param>
/// <param name="ExistingKeys">Tone keys already in the project, so a generated key
/// never collides with one.</param>
/// <param name="ReferenceDi">A dry DI the generated tone is RENDERED over to score
/// it. Null means every tone comes back unrated, which is honest and cheap.</param>
/// <param name="Instruction">The AI chat's instruction for these tones, or null for the
/// menu verb. Carried to every window's prompt as its own section.</param>
public sealed record ToneGenerateRequest(
    string SongAudioPath,
    string ArrangementName,
    string Instrument,
    IReadOnlyList<ToneWindow> Windows,
    string? Context,
    string? SongTitle,
    string? Artist,
    IReadOnlyList<string> ExistingKeys,
    MonoAudio? ReferenceDi = null,
    string? Instruction = null);

/// <summary>Everything known about how one tone came to exist.</summary>
/// <param name="Window">The window it was generated from, including the measure
/// arithmetic.</param>
/// <param name="Stem">Which separated stem was measured.</param>
/// <param name="Sources">Which of <see cref="ToneGenerationSources" /> contributed.</param>
/// <param name="ModelId">The model that mapped it.</param>
/// <param name="Rationale">The model's own reason.</param>
/// <param name="ConfidencePercent">The W5b score of this tone against the window's
/// own audio, or null when it is <paramref name="UnratedReason" />.</param>
/// <param name="UnratedReason">Why there is no percentage; null when there is one.</param>
/// <param name="Features">What was measured, so a reader can disagree with the
/// mapping rather than only with the result.</param>
public sealed record ToneGenerationProvenance(
    ToneWindow Window,
    string Stem,
    IReadOnlyList<string> Sources,
    string ModelId,
    string Rationale,
    int? ConfidencePercent,
    string? UnratedReason,
    ToneWindowFeatures? Features);

/// <summary>One tone change's outcome. <paramref name="Tone" /> is null when
/// <paramref name="Error" /> says why — a set never fails as a whole because one
/// change could not be isolated.</summary>
/// <param name="Window">Which change.</param>
/// <param name="Tone">The generated tone, catalogue-valid, or null.</param>
/// <param name="Provenance">How it came to exist; null when it did not.</param>
/// <param name="Error">Why it did not.</param>
public sealed record GeneratedTone(
    ToneWindow Window,
    RsTone? Tone,
    ToneGenerationProvenance? Provenance,
    string? Error);

/// <summary>What a whole run produced.</summary>
/// <param name="Tones">One entry per window, in window order.</param>
/// <param name="Elapsed">Wall time.</param>
/// <param name="Notes">The human-readable trail.</param>
public sealed record ToneSetGenerationResult(
    IReadOnlyList<GeneratedTone> Tones,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes)
{
    /// <summary>How many windows produced a tone.</summary>
    public int Generated => Tones.Count(t => t.Tone is not null);
}

/// <summary>
///     Generates one Rocksmith tone per placed tone change, from that change's
///     window of the matching stem plus whatever context the charter supplied.
/// </summary>
public interface IRsToneGenerator
{
    /// <summary>Runs the whole set, sequentially.</summary>
    /// <param name="request">The arrangement's windows and context.</param>
    /// <param name="status">Progress lines; may be null.</param>
    /// <param name="ct">Cancels the separation, the calls and the renders.</param>
    /// <param name="fraction">How much of the set is done, 0..1: the share of
    /// windows started, then 1 when the set ends. May be null.</param>
    Task<ToneSetGenerationResult> GenerateAsync(
        ToneGenerateRequest request, IProgress<string>? status, CancellationToken ct,
        IProgress<double>? fraction = null);
}
