using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>
///     The HOST side of tonelab's shared-memory exchange, driven OFFLINE: a dry take goes into the mono input ring as fast
///     as the game takes it, and the stereo blocks it renders come back (protocol v1: the game renders one block per
///     unread input block, with no device clock). Not a real-time path: it runs on a worker thread, and only while the
///     native insert is closed (<see cref="ITonelabSession.TryLeaseAudio" />), so the two never share the rings.
///     <para>
///         Layout: a 64-byte header (magic <c>TONELAB\0</c>, version, rate, block frames, ring blocks, in/out channel
///         counts, then <c>inWrite</c>, <c>inRead</c>, <c>outWrite</c>, <c>outRead</c> and four counters), then the
///         input ring and the output ring, each 64-byte aligned. The host writes <c>inWrite</c> and <c>outRead</c>;
///         the game writes the other two.
///     </para>
/// </summary>
public sealed class TonelabAudioRing : IDisposable
{
    internal const int InWriteOffset = 32;
    internal const int InReadOffset = 36;
    internal const int OutWriteOffset = 40;
    internal const int OutReadOffset = 44;
    private const int HeaderBytes = 64;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private TonelabAudioRing(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
    }

    /// <summary>How long the game may take no block before a render gives up.</summary>
    public static TimeSpan StallTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>How long <see cref="TryOpen" /> waits for the game's first rendered block (its block size is 0 until then).</summary>
    public static TimeSpan FirstBlockTimeout { get; } = TimeSpan.FromSeconds(2);

    public int BlockFrames { get; private set; }

    public int RingBlocks { get; private set; }

    public int OutChannels { get; private set; }

    private int InOffset => Align(HeaderBytes);

    private int OutOffset => Align(InOffset + (RingBlocks * BlockFrames * 4));

    /// <summary>Opens the exchange named <paramref name="name" />, or answers null with what is wrong in <paramref name="fault" />.</summary>
    public static TonelabAudioRing? TryOpen(string name, out string fault)
    {
        if (!OperatingSystem.IsWindows())
        {
            fault = "the exchange is a Windows named mapping";
            return null;
        }

        MemoryMappedFile file;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            fault = $"no shared memory '{name}' ({ex.Message})";
            return null;
        }

        var ring = new TonelabAudioRing(file, file.CreateViewAccessor());
        fault = ring.ReadHeader();
        if (fault.Length == 0) return ring;
        ring.Dispose();
        return null;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>
    ///     Pushes <paramref name="input" /> (mono, 48 kHz) through the game and returns as many output frames, the two
    ///     channels averaged. Null when the game stalls for <see cref="StallTimeout" />. Blocking; call it off the UI thread.
    /// </summary>
    public float[]? Render(float[] input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var block = BlockFrames;
        var blocks = (input.Length + block - 1) / block;
        var output = new float[blocks * block];
        var inBlock = new float[block];
        var outBlock = new float[block * OutChannels];

        // Output the game rendered before this render is not ours: skip it.
        var outRead = _view.ReadUInt32(OutWriteOffset);
        _view.Write(OutReadOffset, outRead);
        var inWrite = _view.ReadUInt32(InWriteOffset);
        int sent = 0, received = 0;
        var idle = Stopwatch.StartNew();
        while (received < blocks)
        {
            ct.ThrowIfCancellationRequested();
            var progressed = false;
            // At most RingBlocks - 1 in flight, so the game never finds its output ring full.
            while (sent < blocks && sent - received < RingBlocks - 1 && unchecked(inWrite - _view.ReadUInt32(InReadOffset)) < RingBlocks)
            {
                Array.Clear(inBlock);
                Array.Copy(input, sent * block, inBlock, 0, Math.Min(block, input.Length - (sent * block)));
                _view.WriteArray(InOffset + ((int)(inWrite % (uint)RingBlocks) * block * 4), inBlock, 0, block);
                Interlocked.MemoryBarrier();
                _view.Write(InWriteOffset, ++inWrite);
                sent++;
                progressed = true;
            }

            while (received < blocks && _view.ReadUInt32(OutWriteOffset) != outRead)
            {
                Interlocked.MemoryBarrier();
                _view.ReadArray(OutOffset + ((int)(outRead % (uint)RingBlocks) * block * 4 * OutChannels), outBlock, 0, outBlock.Length);
                for (var i = 0; i < block; i++)
                {
                    var sum = 0f;
                    for (var c = 0; c < OutChannels; c++) sum += outBlock[(i * OutChannels) + c];
                    output[(received * block) + i] = sum / OutChannels;
                }

                _view.Write(OutReadOffset, ++outRead);
                received++;
                progressed = true;
            }

            if (progressed) idle.Restart();
            else if (idle.Elapsed > StallTimeout) return null;
            else Thread.Sleep(1);
        }

        return output.Length == input.Length ? output : output[..input.Length];
    }

    internal static int Align(int offset) => (offset + 63) & ~63;

    private string ReadHeader()
    {
        var magic = new byte[8];
        _view.ReadArray(0, magic, 0, 8);
        if (Encoding.ASCII.GetString(magic, 0, 7) != TonelabProtocol.HeaderMagic || magic[7] != 0) return "the exchange's magic is not TONELAB";
        var version = _view.ReadUInt32(8);
        if (version != TonelabProtocol.Version) return $"the exchange is version {version}, this host speaks {TonelabProtocol.Version}";
        var rate = _view.ReadUInt32(12);
        if (rate != TonelabProtocol.SampleRate) return $"the exchange runs at {rate} Hz, not {TonelabProtocol.SampleRate}";
        var waited = Stopwatch.StartNew();
        while (_view.ReadUInt32(16) == 0)
        {
            if (waited.Elapsed > FirstBlockTimeout) return "the game has not rendered its first block (block size 0)";
            Thread.Sleep(5);
        }

        BlockFrames = (int)_view.ReadUInt32(16);
        RingBlocks = (int)_view.ReadUInt32(20);
        OutChannels = (int)_view.ReadUInt32(28);
        if (RingBlocks < 2 || _view.ReadUInt32(24) != 1 || OutChannels is < 1 or > 2)
            return $"unexpected ring shape: {RingBlocks} blocks, {_view.ReadUInt32(24)} in / {OutChannels} out channels";
        return _view.Capacity < OutOffset + ((long)RingBlocks * BlockFrames * 4 * OutChannels)
            ? "the exchange is smaller than its header says"
            : "";
    }
}
