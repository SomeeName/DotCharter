using DotCharter.Tone.Audio.Vst.Sandbox;

namespace DotCharter.Tests.Services.Audio.VstSandbox;

/// <summary>
/// Pins the managed mirror of the shared-memory bridge layout to the exact
/// numbers the native side asserts in
/// <c>JuceSharp/src/JuceSharp.Native/include/dotcharter_vst_bridge.h</c>
/// (bridge version 3). If any of these fail, the native header changed
/// without bumping the bridge version — the two processes would read each
/// other's memory at the wrong offsets.
/// </summary>
public class VstSandboxLayoutTests
{
    [Fact]
    public void HeaderLayoutIsPinnedToBridgeVersion3()
    {
        Assert.Equal(0x44435642u, VstSandboxLayout.Magic); // 'DCVB'
        Assert.Equal(3u, VstSandboxLayout.BridgeVersion);
        Assert.Equal(2, VstSandboxLayout.MaxChannels);
        Assert.Equal(8192, VstSandboxLayout.MaxBlockFrames);
        Assert.Equal(128, VstSandboxLayout.MaxMidiEvents);

        Assert.Equal(16, VstSandboxLayout.OffRequestSeq);
        Assert.Equal(20, VstSandboxLayout.OffResponseSeq);
        Assert.Equal(24, VstSandboxLayout.OffHeartbeat);
        Assert.Equal(28, VstSandboxLayout.OffSampleRate);
        Assert.Equal(32, VstSandboxLayout.OffBlockFrames);
        Assert.Equal(36, VstSandboxLayout.OffInputChannels);
        Assert.Equal(40, VstSandboxLayout.OffMidiCount);
        Assert.Equal(44, VstSandboxLayout.OffPrepareBlockFrames); // v2: was reserved0
        Assert.Equal(48, VstSandboxLayout.OffMidiEvents);
        Assert.Equal(560, VstSandboxLayout.OffChildState);
        Assert.Equal(564, VstSandboxLayout.OffChainActive);
        Assert.Equal(568, VstSandboxLayout.OffInstrumentActive);
        Assert.Equal(572, VstSandboxLayout.OffPrepareGeneration); // v2: was reserved1
        Assert.Equal(576, VstSandboxLayout.HeaderBytes);
    }

    /// <summary>
    /// v2 reused the two RESERVED words rather than growing the header, which
    /// is the whole reason the mapping is still 576 bytes and every other
    /// offset is untouched. Both new fields must therefore land in the gaps
    /// the old layout left — between midiCount and midi[], and between
    /// instrumentActive and the end — and nowhere else. Getting either wrong
    /// overlaps a live field and corrupts audio across a process boundary.
    /// </summary>
    [Fact]
    public void TheTwoV2FieldsSitInTheOldReservedGapsAndGrowNothing()
    {
        Assert.Equal(VstSandboxLayout.OffMidiCount + sizeof(uint),
                     VstSandboxLayout.OffPrepareBlockFrames);
        Assert.Equal(VstSandboxLayout.OffPrepareBlockFrames + sizeof(uint),
                     VstSandboxLayout.OffMidiEvents);

        Assert.Equal(VstSandboxLayout.OffInstrumentActive + sizeof(uint),
                     VstSandboxLayout.OffPrepareGeneration);
        Assert.Equal(VstSandboxLayout.OffPrepareGeneration + sizeof(uint),
                     VstSandboxLayout.HeaderBytes);
    }

    [Fact]
    public void RegionOffsetsMatchNativeStaticAsserts()
    {
        // Mirrors the static_asserts in dotcharter_vst_bridge.h exactly.
        Assert.Equal(576, VstSandboxLayout.InputOffset);
        Assert.Equal(66112, VstSandboxLayout.ChainOutOffset);
        Assert.Equal(131648, VstSandboxLayout.InstrOutOffset);
        Assert.Equal(197184, VstSandboxLayout.OffMidiTiming);   // v3
        Assert.Equal(197704, VstSandboxLayout.TotalBytes);      // v3: + 520-byte MIDI-timing block
    }

    /// <summary>
    /// v3 (JuceSharp #28) APPENDED the MIDI-timing block after the last audio
    /// region instead of growing the header, so no v2 offset moved. Mirrors
    /// <c>static_assert(sizeof(MidiTiming) == 520)</c> and its two members: an
    /// int64 block-start frame, then one uint32 offset per MIDI slot.
    /// </summary>
    [Fact]
    public void TheV3MidiTimingBlockIsAppendedAfterTheAudioRegions()
    {
        Assert.Equal(VstSandboxLayout.InstrOutOffset + VstSandboxLayout.RegionBytes, VstSandboxLayout.OffMidiTiming);
        Assert.Equal(VstSandboxLayout.OffMidiTiming, VstSandboxLayout.OffMidiBlockStartFrame);
        Assert.Equal(VstSandboxLayout.OffMidiTiming + 8, VstSandboxLayout.OffMidiOffsets);
        Assert.Equal(520, VstSandboxLayout.MidiTimingBytes);
        Assert.Equal(VstSandboxLayout.OffMidiOffsets + VstSandboxLayout.MaxMidiEvents * sizeof(uint),
                     VstSandboxLayout.TotalBytes);
        Assert.Equal(0, VstSandboxLayout.OffMidiTiming % 8); // the int64 is naturally aligned
    }

    [Fact]
    public void RegionsDoNotOverlap()
    {
        Assert.True(VstSandboxLayout.InputOffset >= VstSandboxLayout.HeaderBytes);
        Assert.Equal(VstSandboxLayout.InputOffset + VstSandboxLayout.RegionBytes,
                     VstSandboxLayout.ChainOutOffset);
        Assert.Equal(VstSandboxLayout.ChainOutOffset + VstSandboxLayout.RegionBytes,
                     VstSandboxLayout.InstrOutOffset);
        Assert.Equal(VstSandboxLayout.InstrOutOffset + VstSandboxLayout.RegionBytes,
                     VstSandboxLayout.OffMidiTiming);

        // Channel accessors stay inside their region.
        Assert.Equal(VstSandboxLayout.ChainOutOffset - VstSandboxLayout.ChannelBytes,
                     VstSandboxLayout.InputChannelOffset(VstSandboxLayout.MaxChannels - 1));
        // MIDI array stays inside the header.
        Assert.True(VstSandboxLayout.OffMidiEvents + VstSandboxLayout.MaxMidiEvents * sizeof(uint)
                    <= VstSandboxLayout.OffChildState);
    }

    [Fact]
    public void MidiPackingMatchesNativeOrder()
    {
        // Native: status | d1 << 8 | d2 << 16.
        Assert.Equal(0x00_40_3C_90u, VstSandboxLayout.PackMidi(0x90, 0x3C, 0x40));
        Assert.Equal(0x00_00_00_80u, VstSandboxLayout.PackMidi(0x80, 0, 0));
    }
}
