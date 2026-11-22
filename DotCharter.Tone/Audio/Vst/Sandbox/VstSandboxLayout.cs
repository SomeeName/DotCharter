namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Managed mirror of the shared-memory bridge layout defined in
/// <c>JuceSharp/src/JuceSharp.Native/include/dotcharter_vst_bridge.h</c>
/// (bridge version 4). The native side pins these numbers with
/// <c>static_assert</c>s; the managed test-suite pins them here — if either
/// side drifts, one of the two builds breaks instead of the bridge silently
/// corrupting audio.
/// <para>
/// <b>v2 (JuceSharp).</b> The header is the SAME 576 bytes and
/// every pre-existing offset is unchanged — the two reserved words became
/// <see cref="OffPrepareBlockFrames"/> and <see cref="OffPrepareGeneration"/>.
/// The version still had to move, because the CONTRACT changed: a v2 child
/// re-reads the device configuration whenever the generation counter moves,
/// where a v1 child read it once at launch and then ran a reconfigured device
/// at the old rate with plugins prepared for the old block size. A version
/// mismatch is refused by <c>VstSandboxHost</c> (degrading to in-process
/// hosting), which is a visible fallback rather than a silent mis-prepare.
/// </para>
/// <para>
/// <b>v3.</b> MIDI to a sandboxed instrument is
/// sample-accurate: a MIDI-timing block is APPENDED after the instrument region
/// (<see cref="OffMidiTiming"/>) — the block's render-clock frame and one sample
/// offset per <see cref="OffMidiEvents"/> slot. Every v2 offset is unchanged;
/// <see cref="TotalBytes"/> grows by <see cref="MidiTimingBytes"/>. A v2 child
/// would ignore the offsets and play every scheduled note at sample 0 of the
/// block that carried it, so the version moved and both sides refuse the mix.
/// </para>
/// <para>
/// <b>v4.</b> Sandboxed chain plugins belong to a seat: a seat-routing
/// block is APPENDED after the MIDI-timing block (<see cref="OffSeatRouting"/>).
/// The parent writes the seat each request is for, the child publishes which
/// seats hold a chain plugin, and the child runs only the requested seat's
/// chain (the instrument only on seat 0). Every v3 offset is unchanged;
/// <see cref="TotalBytes"/> includes <see cref="SeatRoutingBytes"/>. Both sides
/// reject a layout that cannot route effects to the owning seat.
/// </para>
/// </summary>
public static class VstSandboxLayout
{
    public const uint Magic = 0x44435642; // 'DCVB'
    public const uint BridgeVersion = 4;

    public const int MaxChannels = 2;
    public const int MaxBlockFrames = 8192;
    public const int MaxMidiEvents = 128;

    /// <summary>v4: seats the bridge routes (the engine's player cap).</summary>
    public const int MaxSeats = 4;

    // Header field offsets (bytes from the start of the mapping).
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffMaxBlockFrames = 8;
    public const int OffMaxChannels = 12;
    public const int OffRequestSeq = 16;

    /// <summary>
    /// Mirror of <c>dcvb::kRequestWriting</c>: the value the parent stores in
    /// <see cref="OffRequestSeq"/> BEFORE the first payload byte of a request and
    /// replaces with the new sequence after the last, so the child can tell a
    /// payload it copied whole from one overwritten mid-copy. It is never a
    /// sequence (<see cref="NextRequestSeq"/>), so no response can equal it.
    /// </summary>
    public const uint RequestWriting = 0xFFFFFFFFu;

    /// <summary>
    /// Mirror of <c>dcvb::nextRequestSeq</c>: the sequence after
    /// <paramref name="seq"/>, never 0 (a mapping nobody posted to) and never
    /// <see cref="RequestWriting"/>.
    /// </summary>
    public static uint NextRequestSeq(uint seq)
    {
        unchecked { seq++; }
        return seq is 0u or RequestWriting ? 1u : seq;
    }
    public const int OffResponseSeq = 20;
    public const int OffHeartbeat = 24;
    public const int OffSampleRate = 28;
    public const int OffBlockFrames = 32;
    public const int OffInputChannels = 36;
    public const int OffMidiCount = 40;

    /// <summary>v2, was <c>reserved0</c>: the block size the PARENT has
    /// prepared for. Published together with <see cref="OffSampleRate"/> and
    /// released by <see cref="OffPrepareGeneration"/>.</summary>
    public const int OffPrepareBlockFrames = 44;

    public const int OffMidiEvents = 48;
    public const int OffChildState = 560;
    public const int OffChainActive = 564;
    public const int OffInstrumentActive = 568;

    /// <summary>
    /// v2, was <c>reserved1</c>: a monotonic counter the parent INCREMENTS
    /// after writing <see cref="OffSampleRate"/> and
    /// <see cref="OffPrepareBlockFrames"/>. It is the release that publishes
    /// the pair — a child that saw a torn pair would re-prepare its plugins
    /// for a configuration that never existed — and the child re-prepares on
    /// its message thread whenever the value it last latched changes.
    /// <para>It is PARENT-written despite sitting in the child's response
    /// block: it was the only 4-byte slot left in a header whose offsets are
    /// pinned. The child latches it when it opens the bridge, so ARMING the
    /// bridge is not itself mistaken for a device change.</para>
    /// </summary>
    public const int OffPrepareGeneration = 572;

    public const int HeaderBytes = 576;

    public const int ChannelBytes = MaxBlockFrames * sizeof(float);
    public const int RegionBytes = MaxChannels * ChannelBytes;

    public const int InputOffset = HeaderBytes;
    public const int ChainOutOffset = InputOffset + RegionBytes;
    public const int InstrOutOffset = ChainOutOffset + RegionBytes;

    /// <summary>v3: start of the MIDI-timing block (parent writes, child reads),
    /// right after the instrument region so no v2 offset moved.</summary>
    public const int OffMidiTiming = InstrOutOffset + RegionBytes;

    /// <summary>v3: <c>int64</c> render-clock frame of sample 0 of the request's block.</summary>
    public const int OffMidiBlockStartFrame = OffMidiTiming;

    /// <summary>v3: <c>uint32[MaxMidiEvents]</c>, the sample offset inside the block of
    /// each event in <see cref="OffMidiEvents"/>.</summary>
    public const int OffMidiOffsets = OffMidiTiming + sizeof(long);

    public const int MidiTimingBytes = sizeof(long) + MaxMidiEvents * sizeof(uint);

    /// <summary>v4: start of the seat-routing block, right after the MIDI-timing block.</summary>
    public const int OffSeatRouting = OffMidiTiming + MidiTimingBytes;

    /// <summary>v4: <c>uint32</c>, the seat whose chain the request runs (parent writes).</summary>
    public const int OffRequestSeat = OffSeatRouting;

    /// <summary>v4: <c>uint32</c> bit mask, bit s set when seat s holds a chain plugin (child writes).</summary>
    public const int OffSeatChainMask = OffSeatRouting + sizeof(uint);

    public const int SeatRoutingBytes = 2 * sizeof(uint);
    public const int TotalBytes = OffSeatRouting + SeatRoutingBytes;

    public const uint ChildStarting = 0;
    public const uint ChildReady = 1;

    /// <summary>Byte offset of channel <paramref name="ch"/> in the parent-to-child input region.</summary>
    public static int InputChannelOffset(int ch) => InputOffset + ch * ChannelBytes;
    /// <summary>Byte offset of channel <paramref name="ch"/> in the processed-chain output region.</summary>
    public static int ChainOutChannelOffset(int ch) => ChainOutOffset + ch * ChannelBytes;
    /// <summary>Byte offset of channel <paramref name="ch"/> in the instrument output region.</summary>
    public static int InstrOutChannelOffset(int ch) => InstrOutOffset + ch * ChannelBytes;

    /// <summary>Pack a 3-byte MIDI short the way the bridge carries it.</summary>
    public static uint PackMidi(byte status, byte data1, byte data2)
        => status | ((uint)data1 << 8) | ((uint)data2 << 16);
}
