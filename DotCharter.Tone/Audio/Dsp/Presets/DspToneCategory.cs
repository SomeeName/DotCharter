namespace DotCharter.Tone.Audio.Dsp.Presets;

/// <summary>
/// Broad musical bucket a <see cref="DspTonePreset"/> belongs to. Drives the
/// pedalboard tone-picker's category tabs. Ordered roughly clean → dirty →
/// specialty so a default enumeration reads sensibly.
/// </summary>
public enum DspToneCategory
{
    Clean,
    Crunch,
    HighGain,
    Lead,
    Acoustic,
    Bass,
    Ambient,
    Experimental
}

/// <summary>
/// Where a <see cref="DspTonePreset"/> came from. <see cref="BuiltIn"/> presets
/// are the immutable factory library (never overwritten or deleted);
/// <see cref="User"/> presets are captured/saved by the player and persisted
/// over <c>RigConfig.UserTonePresets</c>.
/// </summary>
public enum DspToneOrigin
{
    BuiltIn,
    User
}
