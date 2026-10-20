using DotCharter.Tone.Ai;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The best score a PERFECT model could earn on a row — i.e. what is left after
///     the truth chain has been through the design pipeline's own narrow gate.
///     <para>
///         <b>Why this exists.</b> A model can only answer in a
///         <see cref="ToneDraft" />, and
///         <see cref="ToneDraftValidator" /> then drops what this build cannot
///         apply: unknown modules, anything past
///         <see cref="ToneDraftValidator.MaxModules" />, and — by
///         <see cref="ToneDesignPromptBuilder.ToDraft(DotCharter.Tone.Audio.Dsp.Presets.DspTonePreset)" />'s own documented rule —
///         string params and branch tags, which a v1 draft cannot express at all.
///         The ground truth is under no such limit: it is whatever
///         <see cref="RsToneConverter" /> produced.
///     </para>
///     <para>
///         So "the model scored 78" means nothing until you know whether 100 was
///         reachable. If the ceiling is 94, a 78 is a decent answer with room left;
///         if the ceiling is 78, the model was perfect and the PIPELINE is what
///         costs the other 22 — a completely different bug, in a completely
///         different file. Measuring it is one round trip through pure code, so
///         there is no reason not to.
///     </para>
/// </summary>
public static class ToneEvalCeiling
{
    /// <summary>
    ///     Renders <paramref name="truth" /> as the draft a perfect model would have
    ///     written, puts it through the real serializer and the real validator, and
    ///     scores what comes back against the truth itself.
    /// </summary>
    public static ToneScore Of(DcRigPreset truth)
    {
        ArgumentNullException.ThrowIfNull(truth);

        // A perfect model writes a converted amp (Rocksmith's own chain, `rsamp`,
        // #88) as its designable archetype — the one the scorer judges it as.
        var designable = new DcRigPreset([
            .. truth.Modules.Select(m => m.ModuleTypeId == DcRigTargetCatalog.RsAmpChain ? ToneDesignScorer.RsAmpStandIn(m) : m)
        ]);
        var draft = ToneDesignPromptBuilder.ToDraft(AsPreset(designable));
        var validation = ToneDraftValidator.Validate(ToneDraftJson.Serialize(draft));

        return ToneDesignScorer.Score(
            validation.Preset?.Rig ?? new DcRigPreset(Array.Empty<DcRigModule>()), truth);
    }

    /// <summary>The ceiling's weighted total, 0..100.</summary>
    public static double TotalOf(DcRigPreset truth) => Of(truth).Total;

    /// <summary>Wraps a rig as the preset the prompt builder's renderer takes. The
    /// id/name/category are inert here — only <see cref="DspTonePreset.Rig" /> is
    /// read.</summary>
    private static DspTonePreset AsPreset(DcRigPreset rig)
        => new("eval-truth", "truth", DspToneCategory.Lead, "the converted RS tone", rig);
}
