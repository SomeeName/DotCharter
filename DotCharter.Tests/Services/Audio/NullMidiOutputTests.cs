using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

public class NullMidiOutputTests
{
    [Fact]
    public void Open_SetsIsOpen()
    {
        var m = new NullMidiOutput();
        Assert.False(m.IsOpen);
        m.Open();
        Assert.True(m.IsOpen);
    }

    [Fact]
    public void Close_ClearsIsOpen()
    {
        var m = new NullMidiOutput();
        m.Open();
        m.Close();
        Assert.False(m.IsOpen);
    }

    [Fact]
    public void NoteOn_OnlyCountedWhileOpen()
    {
        var m = new NullMidiOutput();
        m.NoteOn(0, 60, 100); // closed → ignored
        Assert.Equal(0, m.NoteOnCount);

        m.Open();
        m.NoteOn(0, 60, 100);
        m.NoteOn(0, 62, 100);
        Assert.Equal(2, m.NoteOnCount);

        m.Close();
        m.NoteOn(0, 64, 100); // ignored again
        Assert.Equal(2, m.NoteOnCount);
    }

    [Fact]
    public void NoteOff_OnlyCountedWhileOpen()
    {
        var m = new NullMidiOutput();
        m.Open();
        m.NoteOff(0, 60);
        m.NoteOff(0, 60);
        m.NoteOff(0, 60);
        Assert.Equal(3, m.NoteOffCount);
    }

    [Fact]
    public void OtherMethods_DoNotThrow()
    {
        var m = new NullMidiOutput();
        m.Open();
        m.ProgramChange(0, 24);
        m.ChannelVolume(0, 100);
        m.AllSoundOff(0);
    }

    [Fact]
    public void Dispose_ClosesTheOutput()
    {
        var m = new NullMidiOutput();
        m.Open();
        m.Dispose();
        Assert.False(m.IsOpen);
    }
}
