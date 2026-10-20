using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     The tonal family a rig module belongs to, for scoring "did the model reach
///     for the right KIND of pedal".
///     <para>
///         Deliberately DERIVED from the shipped
///         <see cref="DspModuleVisuals.Categorize" /> table rather than re-typed:
///         that table is the one place in the app that already owns a type-id →
///         family mapping, and <c>PedalboardVisualsTests</c> fails the gate if a
///         catalogue id is missing from it. A second hand-maintained list here
///         would be a parallel list that rots the day a module is added.
///     </para>
///     <para>
///         The ONE refinement is that <see cref="DspModuleCategory.Time" /> is
///         split into <see cref="Delay" /> and <see cref="Reverb" />. The board
///         paints both teal because they sit together visually; a tone eval must
///         tell them apart, because "this song wants a slapback" and "this song
///         wants a hall" are different answers.
///     </para>
/// </summary>
public enum ToneFamily
{
    /// <summary>gain, gate, compressor, sustainer, limiter, ducker, … — level control.</summary>
    Dynamics,

    /// <summary>eq, graphiceq — tone shaping with no voice of its own.</summary>
    Eq,

    /// <summary>wah, envfilter, formant, acousticsim, svf, … — resonant filtering.</summary>
    Filter,

    /// <summary>overdrive, distortion, fuzz, klon, bigmuff, rat, ds1, … — the dirt.</summary>
    Drive,

    /// <summary>amp, namamp, bassamp, poweramp, … — the amplifier stage.</summary>
    Amp,

    /// <summary>cab, micpos — the speaker/mic stage.</summary>
    Cab,

    /// <summary>chorus, flanger, phaser, tremolo, rotary, vibrato, univibe, …</summary>
    Modulation,

    /// <summary>delay, tapedelay, slapback, multitap, granular, … — repeats.</summary>
    Delay,

    /// <summary>reverb, platereverb, springreverb, hallreverb, shimmer, … — space.</summary>
    Reverb,

    /// <summary>octaver, harmonizer, doubler, whammy, detune, … — pitch.</summary>
    Pitch,

    /// <summary>splitter, merger — structure, not voice. Excluded from the family score.</summary>
    Routing,

    /// <summary>Anything unmapped. Excluded from the family score.</summary>
    Utility
}

/// <summary>Pure helpers over <see cref="ToneFamily" />. No Avalonia app instance
/// required — <see cref="DspModuleVisuals" /> is a plain static table.</summary>
public static class ToneFamilies
{
    /// <summary>
    ///     The families a tone is SCORED on. <see cref="ToneFamily.Routing" /> and
    ///     <see cref="ToneFamily.Utility" /> are out: a splitter carries no tonal
    ///     identity, and scoring an unmapped id would make the number move when a
    ///     module is added to the engine rather than when the model got better.
    /// </summary>
    public static readonly IReadOnlyList<ToneFamily> Scored = new[]
    {
        ToneFamily.Dynamics, ToneFamily.Eq, ToneFamily.Filter, ToneFamily.Drive,
        ToneFamily.Amp, ToneFamily.Cab, ToneFamily.Modulation, ToneFamily.Delay,
        ToneFamily.Reverb, ToneFamily.Pitch
    };

    /// <summary>True when <paramref name="family" /> takes part in the family score.</summary>
    public static bool IsScored(ToneFamily family)
        => family is not ToneFamily.Routing and not ToneFamily.Utility;

    /// <summary>Maps a native module type id onto its tonal family.</summary>
    public static ToneFamily Of(string moduleTypeId)
    {
        var category = DspModuleVisuals.Categorize(moduleTypeId);
        if (category != DspModuleCategory.Time)
        {
            return category switch
            {
                DspModuleCategory.Dynamics => ToneFamily.Dynamics,
                DspModuleCategory.Eq => ToneFamily.Eq,
                DspModuleCategory.Filter => ToneFamily.Filter,
                DspModuleCategory.Drive => ToneFamily.Drive,
                DspModuleCategory.Amp => ToneFamily.Amp,
                DspModuleCategory.Cab => ToneFamily.Cab,
                DspModuleCategory.Modulation => ToneFamily.Modulation,
                DspModuleCategory.Pitch => ToneFamily.Pitch,
                DspModuleCategory.Routing => ToneFamily.Routing,
                _ => ToneFamily.Utility
            };
        }

        // The Time split. "reversedelay" deliberately does NOT contain "reverb".
        return moduleTypeId.Contains("reverb", StringComparison.Ordinal) ||
               moduleTypeId == DcRigTargetCatalog.Shimmer
            ? ToneFamily.Reverb
            : ToneFamily.Delay;
    }
}
