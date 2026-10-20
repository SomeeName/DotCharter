using System.Buffers.Binary;

namespace DotCharter.Services.Library.Cache;

/// <summary>What a cached song WAV is, from its header alone.</summary>
internal enum DeviceRateWavKind
{
    /// <summary>An ordinary WAV (or not a RIFF/WAVE at all): no <c>dcps</c> chunk.</summary>
    Plain,

    /// <summary>A JuceSharp device-rate file whose chunk, <c>fmt </c> and <c>data</c> agree.</summary>
    DeviceRate,

    /// <summary>Carries a <c>dcps</c> chunk that does not add up. The engine refuses to open it.</summary>
    Damaged,
}

/// <summary>
/// The header of a cached song WAV, read the way the engine probes it
/// (JuceSharp <c>src/JuceSharp.Native/src/DeviceRateWav.h</c>, ABI v43): a
/// device-rate file is 16-bit PCM at the device's rate with a 24-byte <c>dcps</c>
/// chunk (<c>u32 version 1 | f32 step | u64 frames | u32 source rate | u32 0</c>)
/// before <c>data</c>. Reads chunk headers only and seeks past the samples, so it
/// costs one small read. Never throws.
/// </summary>
internal readonly record struct DeviceRateWavHeader(DeviceRateWavKind Kind, int SampleRate, float Step, string? Problem)
{
    private const int ChunkBytes = 24;
    private const float BaseStep = 1f / 32768f;

    public static DeviceRateWavHeader Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return new DeviceRateWavHeader(DeviceRateWavKind.Plain, 0, 0f, ex.Message);
        }
    }

    public static DeviceRateWavHeader Read(Stream stream)
    {
        var total = stream.Length;
        Span<byte> head = stackalloc byte[12];
        if (total < 12 || stream.Read(head) != 12 || !head[..4].SequenceEqual("RIFF"u8) || !head[8..].SequenceEqual("WAVE"u8))
            return new DeviceRateWavHeader(DeviceRateWavKind.Plain, 0, 0f, "not a RIFF/WAVE file");

        bool haveChunk = false, haveFmt = false, haveData = false, chunkShort = false;
        int formatTag = 0, channels = 0, bits = 0, rate = 0;
        uint version = 0;
        var step = 0f;
        ulong frames = 0;
        long dataStart = 0, dataBytes = 0;

        Span<byte> header = stackalloc byte[8];
        Span<byte> body = stackalloc byte[ChunkBytes];
        for (var i = 0; i < 64 && stream.Position + 8 <= total; i++)
        {
            if (stream.Read(header) != 8) break;
            var id = header[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var start = stream.Position;

            if (id.SequenceEqual("dcps"u8))
            {
                haveChunk = true;
                if (size < ChunkBytes || start + ChunkBytes > total || stream.Read(body) != ChunkBytes)
                {
                    chunkShort = true;
                    break;
                }

                version = BinaryPrimitives.ReadUInt32LittleEndian(body);
                step = BinaryPrimitives.ReadSingleLittleEndian(body[4..]);
                frames = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
            }
            else if (id.SequenceEqual("fmt "u8) && size >= 16 && stream.Read(body[..16]) == 16)
            {
                haveFmt = true;
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), int.MaxValue);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                haveData = true;
                dataStart = start;
                dataBytes = size;
            }

            var next = start + size + (size & 1);
            if (next > total) break;
            stream.Position = next;
        }

        if (!haveChunk)
            return new DeviceRateWavHeader(DeviceRateWavKind.Plain, haveFmt ? rate : 0, BaseStep, null);

        string? problem = null;
        if (chunkShort) problem = "the dcps chunk is short";
        else if (version != 1) problem = $"unknown dcps version {version}";
        else if (!float.IsFinite(step) || step < BaseStep || step >= 1f) problem = "the stored step is out of range";
        else if (!haveFmt || formatTag != 1 || bits != 16 || channels <= 0 || rate <= 0) problem = "the fmt chunk is not 16-bit PCM";
        else if (!haveData) problem = "there is no data chunk";
        else if (frames == 0 || (ulong)dataBytes != frames * (ulong)channels * 2) problem = "the data chunk does not hold the stored frame count";
        else if (dataStart + dataBytes > total) problem = "the data chunk is truncated";

        return problem == null
            ? new DeviceRateWavHeader(DeviceRateWavKind.DeviceRate, rate, step, null)
            : new DeviceRateWavHeader(DeviceRateWavKind.Damaged, rate, step, problem);
    }
}
