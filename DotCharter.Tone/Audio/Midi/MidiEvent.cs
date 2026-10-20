namespace DotCharter.Tone.Audio.Midi;

/// <summary>
/// Pre-baked MIDI event scheduled at an absolute timeline position (ms).
/// Status / Data1 / Data2 follow the standard MIDI byte layout, e.g.
/// 0x90 = NoteOn channel 0; Data1 = note; Data2 = velocity.
/// </summary>
public readonly record struct MidiEvent(double TimeMs, byte Status, byte Data1, byte Data2)
{
    public static MidiEvent NoteOn(double timeMs, int channel, int note, int velocity)
        => new(timeMs, (byte)(0x90 | (channel & 0x0F)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));

    public static MidiEvent NoteOff(double timeMs, int channel, int note)
        => new(timeMs, (byte)(0x80 | (channel & 0x0F)), (byte)(note & 0x7F), 0);

    public static MidiEvent ProgramChange(double timeMs, int channel, int program)
        => new(timeMs, (byte)(0xC0 | (channel & 0x0F)), (byte)(program & 0x7F), 0);
}
