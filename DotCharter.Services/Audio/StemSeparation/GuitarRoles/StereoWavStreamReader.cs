using System.Buffers.Binary;

namespace DotCharter.Services.Audio.StemSeparation.GuitarRoles;

/// <summary>
///     Reads a RIFF/WAVE file a block at a time as two float channels, so a whole
///     stem can be measured or split without being held in memory
///     (<c>JuceSharp.AudioIo.WavIo.Read</c> decodes the whole file; a five-minute
///     stereo stem is ~106 MB of floats). PCM 16/24/32-bit and IEEE float 32/64,
///     plain or <c>WAVE_FORMAT_EXTENSIBLE</c>; a mono file reads as two equal
///     channels and reports <see cref="Channels" /> = 1; channels past the second
///     are ignored.
/// </summary>
public sealed class StereoWavStreamReader : IDisposable
{
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly Stream _stream;
    private readonly int _bytesPerSample;
    private readonly int _blockAlign;
    private readonly bool _float;
    private long _dataRemaining;
    private byte[] _scratch = [];

    private StereoWavStreamReader(Stream stream, int sampleRate, int channels, int bitsPerSample, bool isFloat,
        long dataBytes)
    {
        _stream = stream;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        _float = isFloat;
        _bytesPerSample = bitsPerSample / 8;
        _blockAlign = _bytesPerSample * channels;
        _dataRemaining = dataBytes - dataBytes % _blockAlign;
        Frames = _dataRemaining / _blockAlign;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public bool IsFloat => _float;
    public long Frames { get; }

    public static StereoWavStreamReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.SequentialScan);
        try
        {
            return Open(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Takes ownership of a seekable stream positioned at the RIFF header.</summary>
    public static StereoWavStreamReader Open(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(stream));
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12 ||
            !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        ushort format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        var haveFmt = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var bodyStart = stream.Position;
            var remaining = stream.Length - bodyStart;
            var size = declared == uint.MaxValue ? remaining : Math.Min(declared, remaining);

            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException("WAV 'fmt ' chunk is too short.");
                var n = (int)Math.Min(size, fmt.Length);
                stream.ReadExactly(fmt[..n]);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible)
                {
                    if (n < 26) throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE 'fmt ' chunk is too short.");
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }

                if (channels == 0 || sampleRate <= 0)
                    throw new InvalidDataException("WAV 'fmt ' chunk declares no channels or no sample rate.");
                haveFmt = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!haveFmt) throw new InvalidDataException("WAV 'data' chunk precedes 'fmt '.");
                var isFloat = format switch
                {
                    FormatIeeeFloat when bits is 32 or 64 => true,
                    FormatPcm when bits is 16 or 24 or 32 => false,
                    _ => throw new InvalidDataException($"Unsupported WAV format (tag={format}, {bits} bit)."),
                };
                stream.Position = bodyStart;
                return new StereoWavStreamReader(stream, sampleRate, channels, bits, isFloat, size);
            }

            stream.Position = bodyStart + size + (size & 1);
        }

        throw new InvalidDataException(haveFmt ? "WAV missing 'data' chunk." : "WAV missing 'fmt ' chunk.");
    }

    /// <summary>Reads up to the destination length of frames; returns the count, 0 at the end.</summary>
    public int Read(Span<float> left, Span<float> right)
    {
        var want = (int)Math.Min(Math.Min(left.Length, right.Length), _dataRemaining / _blockAlign);
        if (want <= 0) return 0;
        var bytes = want * _blockAlign;
        if (_scratch.Length < bytes) _scratch = new byte[bytes];
        var got = _stream.ReadAtLeast(_scratch.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
        var frames = got / _blockAlign;
        _dataRemaining -= (long)frames * _blockAlign;
        if (frames < want) _dataRemaining = 0; // the file is shorter than its header says

        var span = _scratch.AsSpan();
        for (var i = 0; i < frames; i++)
        {
            var at = i * _blockAlign;
            var l = Sample(span[at..]);
            left[i] = l;
            right[i] = Channels > 1 ? Sample(span[(at + _bytesPerSample)..]) : l;
        }

        return frames;
    }

    private float Sample(ReadOnlySpan<byte> b) => (_float, _bytesPerSample) switch
    {
        (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(b),
        (true, _) => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
        (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        (false, 3) => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
        _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
    };

    public void Dispose() => _stream.Dispose();
}
