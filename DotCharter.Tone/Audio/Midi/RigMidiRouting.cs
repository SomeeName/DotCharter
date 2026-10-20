namespace DotCharter.Tone.Audio.Midi;

/// <summary>
///     What the rig does with a program change and the scene CC, read from
///     <c>RigConfig</c> (<c>RigMidiChannel</c>, <c>RigMidiProgramChange</c>,
///     <c>RigMidiSceneCc</c>). Pure, so every rule is pinned without a device.
/// </summary>
/// <param name="Channel">0 = every channel (omni), 1..16 = that channel only.</param>
/// <param name="ProgramChange">Whether a program change switches tones.</param>
/// <param name="SceneCc">The CC whose value 0..7 picks scene A..H; -1 = off.</param>
public sealed record RigMidiRouting(int Channel, bool ProgramChange, int SceneCc)
{
    /// <summary>The Line 6 Helix snapshot CC — what most pedal boards already send.</summary>
    public const int DefaultSceneCc = 69;

    public static RigMidiRouting Default { get; } = new(0, true, DefaultSceneCc);

    /// <summary>The routing a (possibly hand-edited) config asks for, every value
    /// forced into range.</summary>
    public static RigMidiRouting From(Infrastructure.Configuration.RigConfig config) => new(
        Math.Clamp(config.RigMidiChannel, 0, 16),
        config.RigMidiProgramChange,
        config.RigMidiSceneCc is >= 0 and <= 127 ? config.RigMidiSceneCc : -1);

    /// <summary>Whether a message on <paramref name="channel" /> (0..15) is for the rig.</summary>
    public bool Listens(int channel) => Channel == 0 || channel == Channel - 1;

    /// <summary>Whether this CC is the scene select (and not a knob assignment).</summary>
    public bool IsSceneSelect(int channel, int controller) =>
        SceneCc >= 0 && controller == SceneCc && Listens(channel);

    /// <summary>Whether a program change on <paramref name="channel" /> switches tones.</summary>
    public bool AcceptsProgram(int channel) => ProgramChange && Listens(channel);

    /// <summary>The scene a scene-CC value selects (0..7 = A..H); null for any other
    /// value, which is ignored rather than clamped — a pedal sending 127 as "on"
    /// must not jump to scene H.</summary>
    public static int? SceneFor(int value) => value is >= 0 and < 8 ? value : null;
}
