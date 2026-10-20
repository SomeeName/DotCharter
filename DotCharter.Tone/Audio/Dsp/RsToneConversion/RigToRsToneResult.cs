using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>What a rig module lost on its way into a Rocksmith tone. Each is shown by
/// name before the tone is accepted; none is ever silent.</summary>
public enum RigToneLossKind
{
    /// <summary>No Rocksmith gear converts to this module: it is left out.</summary>
    NoCounterpart,

    /// <summary>A NAM capture played the amp: a catalogue amp stands in at default knobs.</summary>
    NamCapture,

    /// <summary>A NAM capture that was not the first amp: left out.</summary>
    NamCaptureDropped,

    /// <summary>The cabinet loaded an external IR file: the catalogue cabinet of its model stands in.</summary>
    ExternalIr,

    /// <summary>The module was bypassed in the rig: left out.</summary>
    Bypassed,

    /// <summary>The user chose to leave this gear out.</summary>
    LeftOut,

    /// <summary>No gear lands on this module's model: the nearest one of the same module type.</summary>
    NearestModel,

    /// <summary>The gear's knobs cannot reproduce the module's params closely (see the detail).</summary>
    KnobsApproximate,

    /// <summary>The rig has no amp: a catalogue amp stands in at default knobs.</summary>
    NoAmp,

    /// <summary>The rig has no cabinet: the default cabinet stands in.</summary>
    NoCabinet,

    /// <summary>A second amp: Rocksmith has one amp slot.</summary>
    ExtraAmp,

    /// <summary>A second cabinet: Rocksmith has one cabinet slot.</summary>
    ExtraCabinet,

    /// <summary>A rack unit placed elsewhere in the rig: Rocksmith's racks sit after the cabinet.</summary>
    MovedToRacks,

    /// <summary>A split/merge region: Rocksmith's chain is serial, the branches play one after the other.</summary>
    ParallelFlattened
}

/// <summary>One loss. <see cref="Module" /> is the rig module's type id, <see cref="GearKey" />
/// the catalogue gear involved (null when none), <see cref="Detail" /> a file name or a
/// closeness percentage when the kind has one.</summary>
public sealed record RigToneLoss(RigToneLossKind Kind, int ModuleIndex, string Module, string? GearKey, string? Detail);

/// <summary>
///     One piece of catalogue gear the rig became. <see cref="ModuleIndex" /> is the first
///     rig module it covers (the key the caller uses to leave it out), <see cref="Modules" />
///     the module type ids it covers. <see cref="SlotIndex" /> is 1–4 on a rail, 0 for the
///     amp and cabinet and for gear that is not <see cref="Included" />.
///     <see cref="Closeness" /> is 0–1 over the module params, through the forward converter.
/// </summary>
public sealed record RigToneSlot(
    int ModuleIndex,
    IReadOnlyList<string> Modules,
    RsGearRole Role,
    int SlotIndex,
    string GearKey,
    string GearName,
    float Closeness,
    bool Included,
    bool IsStandIn);

/// <summary>A rail that holds more gear than Rocksmith's four slots.</summary>
public sealed record RigToneRailOverflow(RsGearRole Rail, int Count);

/// <summary>
///     The closest catalogue-valid Rocksmith tone for a rig. <see cref="Tone" /> is null when
///     the rig is <see cref="IsRefused" /> (a rail over four slots): the caller names the
///     overflow and lets the user leave gear out. <see cref="Closeness" /> is the mean module
///     closeness, a dropped module counting zero.
/// </summary>
public sealed record RigToRsToneResult(
    RsTone? Tone,
    IReadOnlyList<RigToneSlot> Slots,
    IReadOnlyList<RigToneLoss> Losses,
    IReadOnlyList<RigToneRailOverflow> Overflows,
    float Closeness)
{
    public bool IsRefused => Overflows.Count > 0;
}
