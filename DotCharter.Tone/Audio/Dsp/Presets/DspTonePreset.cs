using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// A named, ready-to-apply tone: a stable kebab-case <see cref="Id"/>, a display
/// <see cref="Name"/>, its <see cref="Category"/>, a one-line
/// <see cref="Description"/> and the ordered <see cref="DcRigPreset"/> rig it maps
/// to. <see cref="Origin"/> distinguishes the immutable factory library
/// (<see cref="DspToneOrigin.BuiltIn"/>) from player-saved profiles
/// (<see cref="DspToneOrigin.User"/>). Applied via
/// <see cref="RsToneConversion.DcRigApplier"/> onto the live
/// <see cref="IDspChainService"/>.
/// </summary>
public sealed record DspTonePreset(
    string Id,
    string Name,
    DspToneCategory Category,
    string Description,
    DcRigPreset Rig,
    DspToneOrigin Origin = DspToneOrigin.BuiltIn)
{
    /// <summary>True for player-saved presets (mutable/deletable); false for the
    /// immutable factory library.</summary>
    public bool IsUser => Origin == DspToneOrigin.User;
}
