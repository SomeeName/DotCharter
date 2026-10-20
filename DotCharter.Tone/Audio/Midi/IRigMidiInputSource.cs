namespace DotCharter.Tone.Audio.Midi;

/// <summary>Control messages only; no managed audio or note scheduling.</summary>
public interface IRigMidiInputSource
{
    IReadOnlyList<string> GetRigMidiInputs();
    bool SelectRigMidiInput(string? deviceName);
    string? RigMidiInputError { get; }
    int RigMidiInputGeneration { get; }
    bool RigMidiInputConnected { get; }
    event Action<int, int, int, int>? RigMidiControlReceived;
    event Action? RigMidiInputStatusChanged;

    /// <summary>
    ///     A PROGRAM CHANGE on the rig's MIDI input: (generation, channel 0..15,
    ///     program 0..127). Raised on the MIDI thread like
    ///     <see cref="RigMidiControlReceived" />; a subscriber queues it for the UI
    ///     and never touches the engine or a control here. The default members keep
    ///     a source that cannot decode one silent.
    /// </summary>
    event Action<int, int, int>? RigMidiProgramReceived
    {
        add { }
        remove { }
    }

    /// <summary>
    ///     A NOTE-ON with a non-zero velocity on the same input: (generation,
    ///     channel 0..15, note 0..127, velocity 1..127) — a pad or a key PRESSED.
    ///     The editor's MIDI bindings read it; the rig does not. Raised on the MIDI
    ///     thread; a subscriber queues it and never touches the UI here.
    /// </summary>
    event Action<int, int, int, int>? RigMidiNoteReceived
    {
        add { }
        remove { }
    }
}

/// <summary>A three-byte NOTE-ON (<c>0x9n nn vv</c>) with a velocity above zero.
/// A note-on at velocity 0 is a note-off by the MIDI spec, so it is not one.</summary>
public static class RigMidiNoteMessage
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out int channel, out int note, out int velocity)
    {
        channel = note = velocity = 0;
        if (bytes.Length != 3 || (bytes[0] & 0xf0) != 0x90 || bytes[1] > 127 || bytes[2] is 0 or > 127)
            return false;
        channel = bytes[0] & 0x0f;
        note = bytes[1];
        velocity = bytes[2];
        return true;
    }
}

/// <summary>A two-byte PROGRAM CHANGE (<c>0xCn pp</c>) — what a pedal board sends
/// to pick a preset. Anything else (a CC, a note, SysEx) is not one.</summary>
public static class RigMidiProgramMessage
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out int channel, out int program)
    {
        channel = program = 0;
        if (bytes.Length != 2 || (bytes[0] & 0xf0) != 0xc0 || bytes[1] > 127)
            return false;
        channel = bytes[0] & 0x0f;
        program = bytes[1];
        return true;
    }
}

public static class RigMidiControlMessage
{
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out int channel, out int controller, out int value)
    {
        channel = controller = value = 0;
        if (bytes.Length != 3 || (bytes[0] & 0xf0) != 0xb0 || bytes[1] > 127 || bytes[2] > 127)
            return false;
        channel = bytes[0] & 0x0f;
        controller = bytes[1];
        value = bytes[2];
        return true;
    }
}
