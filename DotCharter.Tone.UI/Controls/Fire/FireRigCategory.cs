using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The bridge between the rig's ELEVEN engineering families
///     (<see cref="DspModuleCategory" />, which decides a pedal's enclosure,
///     knob hardware and accent on the classic rack) and the Fire design
///     system's EIGHT fixed gear identities (<see cref="FireUnitCategory" />).
///
///     <para>
///         It is a pure total function so the mapping is one table in one file:
///         the alternative — a per-module id list on the Fire screen — would be
///         a SECOND catalogue that drifts the moment a module is added natively,
///         and the failure mode is a unit rendering in the wrong identity colour,
///         which the spec calls out as load-bearing rather than decorative.
///     </para>
///
///     <para>
///         Three of the eleven fold, and each fold is a decision, not an
///         accident:
///         <list type="bullet">
///             <item>
///                 <see cref="DspModuleCategory.Filter" /> (wah, envfilter,
///                 formant, acousticsim) →
///                 <see cref="FireUnitCategory.Eq" />. Every one of them is a
///                 filter that shapes tone; none of them delays, drives or
///                 amplifies. The spec has no ninth colour for them and
///                 inventing one is forbidden.
///             </item>
///             <item>
///                 <see cref="DspModuleCategory.Pitch" /> (octaver, harmonizer,
///                 doubler, whammy, subharmonic) →
///                 <see cref="FireUnitCategory.Modulation" />. The spec's own
///                 wording for that identity is "Chorus / phaser / flanger /
///                 <b>detune</b>", so the doubler the mockup puts on the board
///                 belongs there by name.
///             </item>
///             <item>
///                 <see cref="DspModuleCategory.Routing" /> and
///                 <see cref="DspModuleCategory.Utility" /> →
///                 <see cref="FireUnitCategory.UtilityRouting" />, which is the
///                 spec's own combined identity ("Utility / Routing").
///             </item>
///         </list>
///     </para>
/// </summary>
public static class FireRigCategory
{
    /// <summary>
    ///     The eight identities in the order the gear panel prints them — the
    ///     mockup's filter row, left to right: DYNAMICS · AMPS · CABS · DRIVE ·
    ///     EQ · MOD · REVERB · UTILITY.
    /// </summary>
    public static readonly IReadOnlyList<FireUnitCategory> DisplayOrder =
    [
        FireUnitCategory.Amps,
        FireUnitCategory.Cabinets,
        FireUnitCategory.Drive,
        FireUnitCategory.Dynamics,
        FireUnitCategory.Eq,
        FireUnitCategory.Modulation,
        FireUnitCategory.DelayReverb,
        FireUnitCategory.UtilityRouting
    ];

    /// <summary>
    ///     Folds an engineering family onto its Fire identity. Total over the
    ///     enum: a new <see cref="DspModuleCategory" /> value will not compile
    ///     until it is placed.
    /// </summary>
    public static FireUnitCategory For(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Dynamics => FireUnitCategory.Dynamics,
        DspModuleCategory.Eq => FireUnitCategory.Eq,
        DspModuleCategory.Filter => FireUnitCategory.Eq,
        DspModuleCategory.Drive => FireUnitCategory.Drive,
        DspModuleCategory.Amp => FireUnitCategory.Amps,
        DspModuleCategory.Cab => FireUnitCategory.Cabinets,
        DspModuleCategory.Modulation => FireUnitCategory.Modulation,
        DspModuleCategory.Pitch => FireUnitCategory.Modulation,
        DspModuleCategory.Time => FireUnitCategory.DelayReverb,
        DspModuleCategory.Routing => FireUnitCategory.UtilityRouting,
        DspModuleCategory.Utility => FireUnitCategory.UtilityRouting,
        _ => FireUnitCategory.UtilityRouting
    };

    /// <summary>The identity of a module type id, straight off the catalogue.</summary>
    public static FireUnitCategory ForModule(string moduleTypeId)
        => For(DspModuleVisuals.Categorize(moduleTypeId));

    /// <summary>
    ///     The localization key of an identity's gear-panel label. The labels
    ///     are the mockup's own filter chips, kept short because they sit on a
    ///     336 px rail.
    /// </summary>
    public static string LabelKeyFor(FireUnitCategory category) => category switch
    {
        FireUnitCategory.Dynamics => "Rig_Cat_Dynamics",
        FireUnitCategory.Amps => "Rig_Cat_Amps",
        FireUnitCategory.Cabinets => "Rig_Cat_Cabinets",
        FireUnitCategory.Drive => "Rig_Cat_Drive",
        FireUnitCategory.Eq => "Rig_Cat_Eq",
        FireUnitCategory.Modulation => "Rig_Cat_Modulation",
        FireUnitCategory.DelayReverb => "Rig_Cat_DelayReverb",
        FireUnitCategory.UtilityRouting => "Rig_Cat_Utility",
        _ => "Rig_Cat_Utility"
    };
}
