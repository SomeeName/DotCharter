using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

/// <summary>#389: the MIDI output was opened by WinMM index, which shifts when a device is plugged or unplugged.</summary>
public sealed class MidiDeviceResolverTests
{
    private static readonly MidiOutputDeviceInfo Mapper = new(-1, "Default MIDI Mapper");

    [Fact]
    public void TheSavedNameWinsOverAnIndexThatNowPointsAtAnotherDevice()
    {
        // Saved when "Roland UM-ONE" was index 1; a device was unplugged, so it is index 0 now.
        IReadOnlyList<MidiOutputDeviceInfo> devices = [Mapper, new(0, "Roland UM-ONE"), new(1, "loopMIDI Port")];

        var r = MidiDeviceResolver.Resolve(savedIndex: 1, savedName: "Roland UM-ONE", devices);

        Assert.Equal(0, r.Index);
        Assert.Null(r.FallbackReason);
    }

    [Fact]
    public void ANameMatchIgnoresCase()
        => Assert.Equal(2, MidiDeviceResolver.Resolve(0, "LOOPMIDI port", [Mapper, new(0, "A"), new(2, "loopMIDI Port")]).Index);

    [Fact]
    public void AMissingNamedDeviceFallsBackToTheMapperWithAReason()
    {
        var r = MidiDeviceResolver.Resolve(1, "Unplugged Synth", [Mapper, new(0, "Roland UM-ONE"), new(1, "loopMIDI Port")]);

        Assert.Equal(-1, r.Index);
        Assert.Contains("Unplugged Synth", r.FallbackReason);
    }

    [Fact]
    public void AConfigWithoutAnyNameKeepsItsIndexWhileThatDeviceExists()
    {
        IReadOnlyList<MidiOutputDeviceInfo> devices = [Mapper, new(0, "A"), new(1, "B")];

        Assert.Equal(1, MidiDeviceResolver.Resolve(1, "", devices).Index);
        Assert.Equal(-1, MidiDeviceResolver.Resolve(-1, "", devices).Index);
        Assert.Equal(-1, MidiDeviceResolver.Resolve(7, "", devices).Index);
    }
}
