using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace DotCharter.Tone.Audio.Vst.Sandbox;

/// <summary>
/// Managed parent-side client for the sandbox's shared-memory audio bridge.
/// The production audio path uses the native implementation inside
/// <c>juce_sharp.dll</c> (RemoteVstBridge); this class exists so tests and
/// diagnostics can exercise the child process end-to-end without an audio
/// device — it implements the exact same request/response protocol over the
/// same layout (<see cref="VstSandboxLayout"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VstSandboxSharedMemory : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _requestEvent;
    private readonly EventWaitHandle _responseEvent;
    private uint _seq;

    /// <param name="shmName">Name of the shared file mapping.</param>
    /// <param name="requestEventName">Name of the parent-to-child request event.</param>
    /// <param name="responseEventName">Name of the child-to-parent response event.</param>
    /// <param name="sampleRate">The parent's device sample rate.</param>
    /// <param name="blockFrames">The block size the parent has prepared for.
    /// Bridge v2 publishes it beside the rate; 0 leaves the child running with
    /// whatever it was launched with.</param>
    public VstSandboxSharedMemory(string shmName, string requestEventName, string responseEventName,
                                  int sampleRate, int blockFrames = 0)
    {
        _mapping = MemoryMappedFile.CreateOrOpen(shmName, VstSandboxLayout.TotalBytes);
        _view = _mapping.CreateViewAccessor(0, VstSandboxLayout.TotalBytes);
        _requestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, requestEventName);
        _responseEvent = new EventWaitHandle(false, EventResetMode.AutoReset, responseEventName);

        _view.Write(VstSandboxLayout.OffMagic, VstSandboxLayout.Magic);
        _view.Write(VstSandboxLayout.OffVersion, VstSandboxLayout.BridgeVersion);
        _view.Write(VstSandboxLayout.OffMaxBlockFrames, (uint)VstSandboxLayout.MaxBlockFrames);
        _view.Write(VstSandboxLayout.OffMaxChannels, (uint)VstSandboxLayout.MaxChannels);
        // Mirrors RemoteVstBridge::enable: rate and block go down TOGETHER and
        // the generation bump is what publishes them. The child latches the
        // generation in openBridge, so arming is not itself a device change.
        SetDevicePrepare(sampleRate, blockFrames);
        _seq = ReadUInt(VstSandboxLayout.OffRequestSeq);
    }

    /// <summary>
    /// Publish a new device configuration to the child (bridge v2), mirroring
    /// <c>RemoteVstBridge::setDevicePrepare</c>. Until v2 the rate was written
    /// once and read once, in the child's <c>openBridge</c>, so after a
    /// parent-side reconfigure every sandboxed VST3 went on running at the old
    /// rate and a plugin prepared for 256 frames could be handed 1024 — on the
    /// very thread the parent's audio callback is blocked waiting for. The
    /// child now re-prepares on ITS message thread and answers silence, which
    /// the parent already handles as "pass audio through dry", until it lands.
    /// <para><b>The ORDER is the contract</b>: both fields, then a barrier,
    /// then the generation. A child that read a torn pair would prepare for a
    /// configuration that never existed. A non-positive value leaves its field
    /// alone, exactly as the native side does.</para>
    /// </summary>
    public void SetDevicePrepare(int sampleRate, int blockFrames)
    {
        if (sampleRate > 0) _view.Write(VstSandboxLayout.OffSampleRate, sampleRate);
        if (blockFrames > 0) _view.Write(VstSandboxLayout.OffPrepareBlockFrames, (uint)blockFrames);

        // fetch_add(1, release) — monotonic, never a SET. The child compares
        // against the value it last latched, so re-publishing the SAME number
        // would be missed entirely.
        WriteUIntVolatile(VstSandboxLayout.OffPrepareGeneration,
                          ReadUInt(VstSandboxLayout.OffPrepareGeneration) + 1);
    }

    public uint Heartbeat => ReadUInt(VstSandboxLayout.OffHeartbeat);

    /// <summary>The generation currently published to the child. Every device
    /// change must move it, or the child never re-prepares.</summary>
    public uint PrepareGeneration => ReadUInt(VstSandboxLayout.OffPrepareGeneration);

    /// <summary>The block size currently published to the child.</summary>
    public uint PrepareBlockFrames => ReadUInt(VstSandboxLayout.OffPrepareBlockFrames);
    public uint ChildState => ReadUInt(VstSandboxLayout.OffChildState);

    /// <summary>
    /// One block round trip, mirroring the native audio-thread path.
    /// <paramref name="left"/>/<paramref name="right"/> are written to the
    /// input region and, when the child reports an active chain, replaced by
    /// the processed output. Returns false on response timeout (dry).
    /// <paramref name="midiOffsets"/> (bridge v3) is the sample offset of each
    /// <paramref name="midi"/> event inside this block — null places them all
    /// at sample 0 — and <paramref name="blockStartFrame"/> the render-clock
    /// frame of sample 0, as <c>RemoteVstBridge::process</c> writes them.
    /// </summary>
    public bool ProcessBlock(float[] left, float[] right, int frames,
                             uint[]? midi, int midiCount,
                             out bool chainActive, out bool instrumentActive,
                             int timeoutMs = 100, uint[]? midiOffsets = null, long blockStartFrame = 0)
    {
        chainActive = false;
        instrumentActive = false;
        if (frames is <= 0 or > VstSandboxLayout.MaxBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(frames));

        _view.WriteArray(VstSandboxLayout.InputChannelOffset(0), left, 0, frames);
        _view.WriteArray(VstSandboxLayout.InputChannelOffset(1), right, 0, frames);
        _view.Write(VstSandboxLayout.OffBlockFrames, (uint)frames);
        _view.Write(VstSandboxLayout.OffInputChannels, (uint)2);
        var n = Math.Min(midiCount, VstSandboxLayout.MaxMidiEvents);
        if (midi != null && n > 0)
            _view.WriteArray(VstSandboxLayout.OffMidiEvents, midi, 0, n);
        _view.Write(VstSandboxLayout.OffMidiCount, (uint)(midi != null ? n : 0));
        // Bridge v3: every slot gets an offset, zero included, so a previous
        // block's offsets can never leak into this one. Clamped into the
        // block exactly as the native writer clamps.
        _view.Write(VstSandboxLayout.OffMidiBlockStartFrame, blockStartFrame);
        for (var i = 0; i < n; i++)
        {
            var offset = midiOffsets != null && i < midiOffsets.Length ? midiOffsets[i] : 0u;
            _view.Write(VstSandboxLayout.OffMidiOffsets + i * sizeof(uint), Math.Min(offset, (uint)frames - 1));
        }

        var seq = ++_seq;
        WriteUIntVolatile(VstSandboxLayout.OffRequestSeq, seq);
        _requestEvent.Set();

        var deadline = Environment.TickCount64 + timeoutMs;
        while (ReadUInt(VstSandboxLayout.OffResponseSeq) != seq)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return false;
            _responseEvent.WaitOne((int)Math.Min(remaining, 20));
        }

        chainActive = ReadUInt(VstSandboxLayout.OffChainActive) != 0;
        instrumentActive = ReadUInt(VstSandboxLayout.OffInstrumentActive) != 0;
        if (chainActive)
        {
            _view.ReadArray(VstSandboxLayout.ChainOutChannelOffset(0), left, 0, frames);
            _view.ReadArray(VstSandboxLayout.ChainOutChannelOffset(1), right, 0, frames);
        }
        return true;
    }

    /// <summary>Read the instrument output region (valid after a round trip
    /// that reported <c>instrumentActive</c>).</summary>
    public void ReadInstrumentOutput(float[] left, float[] right, int frames)
    {
        _view.ReadArray(VstSandboxLayout.InstrOutChannelOffset(0), left, 0, frames);
        _view.ReadArray(VstSandboxLayout.InstrOutChannelOffset(1), right, 0, frames);
    }

    private uint ReadUInt(int offset)
    {
        _view.Read(offset, out uint value);
        return Volatile.Read(ref value); // the Read above is the real fence-relevant op on x64
    }

    private void WriteUIntVolatile(int offset, uint value)
    {
        _view.Write(offset, value);
        Thread.MemoryBarrier();
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
        _requestEvent.Dispose();
        _responseEvent.Dispose();
    }
}
