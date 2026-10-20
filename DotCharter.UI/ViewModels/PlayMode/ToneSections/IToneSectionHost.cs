using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>
///     What "match by section" needs from the tone-design panel it lives inside.
///
///     <para>
///         <b>It exists so the section flow reuses the panel's OWN seams rather
///         than inventing a second set.</b> The reference DI, the chain summary,
///         the result block and the library save are all already solved on
///         <c>ToneDesignViewModel</c>; a section that saved its tone through a
///         private path would put a rig in the library that the drawer's card
///         grid never rebuilt for, and a candidate published anywhere but
///         <c>Result</c> would need its own Hear it / Discard — which is exactly
///         the second door onto the rig this whole surface is designed to avoid.
///     </para>
///     <para>
///         It is an INTERFACE rather than a reference to the panel so the
///         section flow can be tested without one, and so a future host (the
///         chart editor's own tone surface) can supply the same five answers
///         differently.
///     </para>
/// </summary>
public interface IToneSectionHost
{
    /// <summary>The song, its chart tone-change spans and the playhead — the
    /// panel's <c>MatchSourceProvider</c> answer, already degraded to the
    /// transport's own loaded file when the host assigned none. Null when
    /// nothing is loaded.</summary>
    ToneMatchSource? ResolveMatchSource();

    /// <summary>The dry guitar every candidate is rendered over: the clip the
    /// player recorded if there is one, else the bundled one, else null — which
    /// is a MESSAGE ("a match needs a dry guitar"), never an exception.</summary>
    /// <param name="ct">Cancels the load.</param>
    Task<MonoAudio?> EnsureReferenceDiAsync(CancellationToken ct);

    /// <summary>"Rat → Power Amp → Cab" in the module catalogue's own words.</summary>
    /// <param name="rig">The rig to name.</param>
    string ChainSummary(DcRigPreset rig);

    /// <summary>Song title and artist, for the set matcher's prior and the
    /// narrator. Both null when the host does not know — the chart editor's rig
    /// host has no flow song, and a section still matches without one.</summary>
    (string? Title, string? Artist) SongContext();

    /// <summary>Publishes a chosen candidate as the panel's <c>Result</c>, so
    /// Hear it, Save as tone, Discard and Tweak operate on it without ever
    /// learning it came from a section.</summary>
    /// <param name="result">The candidate shaped as a design result.</param>
    /// <param name="chainSummary">Its chain, already summarised.</param>
    void PublishResult(ToneDesignResult result, string chainSummary);

    /// <summary>The drawer's save seam — the same
    /// <c>IDspTonePresetService.SaveUser</c> path "Save as tone" commits, so a
    /// section's tone lands in My Tones visibly rather than merely on disk.</summary>
    /// <param name="name">The tone's name; a section's label.</param>
    /// <param name="category">Which rail it files under.</param>
    /// <param name="description">The description stored with it.</param>
    /// <param name="rig">The matched rig.</param>
    DspTonePreset SaveTone(string name, DspToneCategory category, string description, DcRigPreset rig);
}
