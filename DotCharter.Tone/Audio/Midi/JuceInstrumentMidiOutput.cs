using JuceSharp;

namespace DotCharter.Tone.Audio.Midi;

/// <summary>
/// <see cref="IMidiOutput"/> that forwards MIDI short messages to the
/// internal instrument bus inside the native JUCE engine, so they're
/// rendered by a loaded VST3 instrument plugin in the audio thread.
/// </summary>
public sealed class JuceInstrumentMidiOutput : IMidiOutput
{
    private readonly JuceAudioEngine _engine;
    public bool IsOpen { get; private set; }

    public JuceInstrumentMidiOutput(JuceAudioEngine engine) => _engine = engine;

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public void NoteOn(int channel, int note, int velocity)
    {
        if (!IsOpen) return;
        _engine.SendMidi((byte)(0x90 | (channel & 0x0F)), (byte)(note & 0x7F), (byte)(velocity & 0x7F));
    }

    public void NoteOff(int channel, int note)
    {
        if (!IsOpen) return;
        _engine.SendMidi((byte)(0x80 | (channel & 0x0F)), (byte)(note & 0x7F), 0);
    }

    public void ProgramChange(int channel, int program)
    {
        if (!IsOpen) return;
        _engine.SendMidi((byte)(0xC0 | (channel & 0x0F)), (byte)(program & 0x7F), 0);
    }

    public void ChannelVolume(int channel, int level)
    {
        if (!IsOpen) return;
        _engine.SendMidi((byte)(0xB0 | (channel & 0x0F)), 7, (byte)(level & 0x7F));
    }

    public void AllSoundOff(int channel)
    {
        if (!IsOpen) return;
        _engine.SendMidi((byte)(0xB0 | (channel & 0x0F)), 120, 0);
    }

    /// <summary>
    ///     A raw 3-byte short at an absolute output frame (<c>dca_engine_send_midi_at</c>, ABI v34),
    ///     or as soon as possible when <paramref name="renderFrame" /> is negative. Both the
    ///     in-process instrument and the out-of-process sandbox (bridge v3) place
    ///     the event on that exact sample.
    /// </summary>
    public void Send(byte status, byte data1, byte data2, long renderFrame)
    {
        if (!IsOpen) return;
        if (renderFrame < 0) _engine.SendMidi(status, (byte)(data1 & 0x7F), (byte)(data2 & 0x7F));
        else _engine.SendMidi(status, (byte)(data1 & 0x7F), (byte)(data2 & 0x7F), renderFrame);
    }

    public void Dispose() => Close();
}
