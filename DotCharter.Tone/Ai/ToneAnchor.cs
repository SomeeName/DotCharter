using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Grounding stage 2: what the player's OWN song library already knows about
///     the song they are asking about.
///
///     <para>
///         Stage 1 is the catalogue in the system prompt — what the modules ARE.
///         This is what a professional did with them for this exact song: the
///         Rocksmith tone that shipped with it, run through DotCharter's own
///         <see cref="RsToneConverter" /> so it is expressed in the same module ids
///         the model has to answer in. The model is asked to ADAPT it, not to start
///         from nothing.
///     </para>
///     <para>
///         <b>It is retrieval, not truth.</b> A Rocksmith tone is one interpretation
///         and the conversion is lossy; the anchor is labelled as such in the prompt
///         and the model is told it may depart from it. And it is OPT-OUT
///         (<see cref="AiProviderSettings.UseLibraryTones" />) — a player who wants
///         the model's own idea of a sound must be able to have it.
///     </para>
/// </summary>
/// <param name="ToneName">The Rocksmith tone's own name, e.g. "Lead" or
/// "Clean Verse". Shown to the model, because the name is itself a hint.</param>
/// <param name="Rig">The converted chain.</param>
public sealed record LibraryToneCandidate(string ToneName, DcRigPreset Rig);

/// <summary>One song in the player's library that carries usable Rocksmith
/// tones.</summary>
/// <param name="Title">Song title as the library has it.</param>
/// <param name="Artist">Artist as the library has it.</param>
/// <param name="Year">Release year, or 0 when the package does not carry one. A
/// Rocksmith package has NO genre — year is the only "era" signal there is, which
/// is why the nearest-song fallback ranks on it.</param>
/// <param name="Tones">Every convertible tone in the package, in package order.</param>
public sealed record LibrarySongTones(
    string Title, string Artist, int Year, IReadOnlyList<LibraryToneCandidate> Tones);

/// <summary>
///     The library seam the retriever reads. A narrow interface on purpose: the
///     real implementation walks psarc archives and is expensive, the tests hand in
///     a list, and nothing in the ranking depends on which one it got.
/// </summary>
public interface IToneAnchorLibrary
{
    /// <summary>
    ///     Songs from the player's library that could ground a request for
    ///     <paramref name="title" /> / <paramref name="artist" /> — the song itself
    ///     when it is there, plus whatever is nearby. **Ranking is NOT done here**;
    ///     <see cref="ToneAnchorRetriever" /> decides which of these is the anchor
    ///     and which are analogies, so the decision is unit-testable without a
    ///     library.
    /// </summary>
    /// <returns>An empty list when nothing is available. Never throws for a missing
    /// library, an unreadable archive or an unconfigured path: a grounding stage
    /// that cannot run must leave the request ungrounded, never fail it.</returns>
    Task<IReadOnlyList<LibrarySongTones>> LookupAsync(
        string? title, string? artist, CancellationToken ct);
}

/// <summary>Which tone of a song a request is asking about, inferred from its
/// words. <see cref="Unknown" /> means "the description did not say", and the
/// retriever then takes the RICHEST tone — the longest chain is the one that
/// carries the most of what made the song sound like itself.</summary>
public enum ToneAnchorRole
{
    /// <summary>The description named no role.</summary>
    Unknown = 0,
    /// <summary>Clean.</summary>
    Clean = 1,
    /// <summary>Lead / solo.</summary>
    Lead = 2,
    /// <summary>Rhythm.</summary>
    Rhythm = 3,
    /// <summary>Bass.</summary>
    Bass = 4
}

/// <summary>One retrieved reference, ready to render into the prompt.</summary>
/// <param name="Title">Song title.</param>
/// <param name="Artist">Song artist.</param>
/// <param name="ToneName">The Rocksmith tone's name.</param>
/// <param name="Draft">The chain as a <see cref="ToneDraft" /> — the SAME shape the
/// model must answer in, so the example teaches the format as well as the sound.</param>
public sealed record ToneAnchor(string Title, string Artist, string ToneName, ToneDraft Draft);

/// <summary>What retrieval found for one request.</summary>
/// <param name="Anchor">The song's OWN tone, when the song is in the library.
/// Null otherwise.</param>
/// <param name="Analogies">Up to two nearby songs, used only when
/// <paramref name="Anchor" /> is null. Labelled as analogies in the prompt — a
/// model told "this is how the song was rigged" about a DIFFERENT song is being
/// lied to, and will quote the wrong song back in its rationale.</param>
public sealed record ToneAnchorSet(ToneAnchor? Anchor, IReadOnlyList<ToneAnchor> Analogies)
{
    /// <summary>Nothing was retrieved; the prompt is built exactly as it was before
    /// this stage existed.</summary>
    public static ToneAnchorSet Empty { get; } = new(null, []);

    /// <summary>True when there is anything at all to put in the prompt.</summary>
    public bool HasAny => Anchor is not null || Analogies.Count > 0;
}
