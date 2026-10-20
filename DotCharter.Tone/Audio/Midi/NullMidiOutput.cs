namespace DotCharter.Tone.Audio.Midi;

/// <summary>
/// No-op MIDI output. Used when no hardware MIDI device is available, on
/// non-Windows platforms, and as the default in tests.
/// </summary>
public sealed class NullMidiOutput : IMidiOutput
{
    public bool IsOpen { get; private set; }
    public int NoteOnCount { get; private set; }
    public int NoteOffCount { get; private set; }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void NoteOn(int channel, int note, int velocity) { if (IsOpen) NoteOnCount++; }
    public void NoteOff(int channel, int note) { if (IsOpen) NoteOffCount++; }
    public void ProgramChange(int channel, int program) { }
    public void ChannelVolume(int channel, int level) { }
    public void AllSoundOff(int channel) { }
    public void Dispose() => Close();
}
