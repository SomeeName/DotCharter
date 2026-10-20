using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

public class WinMmMidiOutputTests
{
    [Fact]
    public void IsSupported_MatchesPlatform() => Assert.Equal(OperatingSystem.IsWindows(), WinMMMidiOutput.IsSupported);

    [Fact]
    public void EnumerateDevices_AlwaysIncludesMapper_OnWindows()
    {
        Assert.SkipUnless(WinMMMidiOutput.IsSupported, "WinMM MIDI is Windows-only.");

        var devs = WinMMMidiOutput.EnumerateDevices();
        Assert.NotEmpty(devs);
        Assert.Contains(devs, d => d.Index == -1);
    }

    [Fact]
    public void EnumerateDevices_ReturnsEmpty_OnNonWindows()
    {
        Assert.SkipWhen(WinMMMidiOutput.IsSupported, "This case only applies off Windows.");
        Assert.Empty(WinMMMidiOutput.EnumerateDevices());
    }

    [Fact]
    public void Construction_StoresDeviceIndex()
    {
        var m = new WinMMMidiOutput(-1);
        Assert.Equal(-1, m.DeviceIndex);
        Assert.False(m.IsOpen);
    }

    [Fact]
    public void SendingMessages_OnClosedHandle_DoesNotThrow()
    {
        var m = new WinMMMidiOutput(-1);
        // Never opened — every send should be a no-op.
        m.NoteOn(0, 60, 100);
        m.NoteOff(0, 60);
        m.ProgramChange(0, 24);
        m.ChannelVolume(0, 100);
        m.AllSoundOff(0);
        m.Dispose();
    }

    [Fact]
    public void Close_OnUnopenedDevice_IsNoOp()
    {
        var m = new WinMMMidiOutput(-1);
        m.Close();
        Assert.False(m.IsOpen);
    }
}
