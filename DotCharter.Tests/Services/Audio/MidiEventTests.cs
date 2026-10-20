using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

public class MidiEventTests
{
    [Fact]
    public void NoteOn_PacksStatusByteWithChannel()
    {
        var e = MidiEvent.NoteOn(123.0, channel: 3, note: 60, velocity: 100);
        Assert.Equal(123.0, e.TimeMs);
        Assert.Equal((byte)(0x90 | 3), e.Status);
        Assert.Equal((byte)60, e.Data1);
        Assert.Equal((byte)100, e.Data2);
    }

    [Fact]
    public void NoteOff_UsesStatus0x80()
    {
        var e = MidiEvent.NoteOff(0, channel: 0, note: 64);
        Assert.Equal((byte)0x80, e.Status);
        Assert.Equal((byte)64, e.Data1);
        Assert.Equal((byte)0, e.Data2);
    }

    [Fact]
    public void ProgramChange_UsesStatus0xC0()
    {
        var e = MidiEvent.ProgramChange(0, channel: 0, program: 24);
        Assert.Equal((byte)0xC0, e.Status);
        Assert.Equal((byte)24, e.Data1);
        Assert.Equal((byte)0, e.Data2);
    }

    [Fact]
    public void NoteOn_MasksChannelTo4Bits()
    {
        var e = MidiEvent.NoteOn(0, channel: 0x1F, note: 60, velocity: 64);
        Assert.Equal((byte)(0x90 | 0x0F), e.Status);
    }

    [Fact]
    public void NoteOn_MasksDataTo7Bits()
    {
        var e = MidiEvent.NoteOn(0, channel: 0, note: 200, velocity: 200);
        Assert.Equal((byte)(200 & 0x7F), e.Data1);
        Assert.Equal((byte)(200 & 0x7F), e.Data2);
    }
}
