using System.Buffers.Binary;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     Minimal RIFF/WAVE reader for analysis input (chart-from-audio): PCM
///     16/24/32-bit integer and 32-bit float, any channel count, mixed down
///     to mono by averaging. Stems and converted PSARC audio are WAV, which
///     makes this the whole decode surface the offline analyzer needs.
/// </summary>
public static class WavMonoReader
{
    public static (float[] Samples, int SampleRate) Read(string filePath)
        => Read(filePath, ImportSizeLimits.WavFileBytes);

    /// <summary>
    ///     Reads a WAV file, refusing one longer than <paramref name="maxBytes" />
    ///     BEFORE it is read: the whole file is held, then a float per frame is
    ///     allocated on top, so the length the file system reports is the only
    ///     number that can bound the cost. Throws <see cref="ImportFileTooLargeException" />.
    /// </summary>
    internal static (float[] Samples, int SampleRate) Read(string filePath, long maxBytes)
    {
        ImportSizeLimits.RequireWithin(new FileInfo(filePath).Length, maxBytes, "The WAV file");
        return Read(File.ReadAllBytes(filePath));
    }

    public static (float[] Samples, int SampleRate) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            // ReSharper disable once LocalizableElement -- developer-facing; the dialog wraps it.
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        short format = 0, channels = 0, bitsPerSample = 0;
        var sampleRate = 0;
        ReadOnlySpan<byte> data = default;

        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(pos, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + 4, 4));
            pos += 8;
            // Widened deliberately: `pos` and `chunkSize` are both int, and a
            // header declaring 0x7FFFFFF0 wraps their sum NEGATIVE, so the guard
            // did not fire, chunkSize stayed huge, and the Slice below threw
            // ArgumentOutOfRangeException instead of the InvalidDataException
            // every caller of this reader expects. Reachable from chart-from-audio
            // on any user-supplied .wav.
            if (chunkSize < 0 || (long)pos + chunkSize > bytes.Length) chunkSize = bytes.Length - pos;

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                var fmt = bytes.Slice(pos, chunkSize);
                format = BinaryPrimitives.ReadInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..]);
                // WAVE_FORMAT_EXTENSIBLE: the real format lives in the first
                // two bytes of the 16-byte SubFormat GUID at offset 24.
                if (format == -2 && chunkSize >= 26)
                    format = BinaryPrimitives.ReadInt16LittleEndian(fmt[24..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = bytes.Slice(pos, chunkSize);
            }

            pos += chunkSize + (chunkSize & 1); // chunks are word-aligned
        }

        if (channels <= 0 || sampleRate <= 0 || data.IsEmpty)
            throw new InvalidDataException("WAV file has no decodable fmt/data chunks.");

        return format switch
        {
            1 when bitsPerSample == 16 => (DecodePcm16(data, channels), sampleRate),
            1 when bitsPerSample == 24 => (DecodePcm24(data, channels), sampleRate),
            1 when bitsPerSample == 32 => (DecodePcm32(data, channels), sampleRate),
            3 when bitsPerSample == 32 => (DecodeFloat32(data, channels), sampleRate),
            _ => throw new InvalidDataException(
                $"Unsupported WAV format (tag={format}, {bitsPerSample} bit).")
        };
    }

    private static float[] DecodePcm16(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (2 * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += BinaryPrimitives.ReadInt16LittleEndian(data[((f * channels + c) * 2)..]) / 32768f;
            mono[f] = sum / channels;
        }

        return mono;
    }

    private static float[] DecodePcm24(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (3 * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * 3;
                var v = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                sum += v / 8388608f;
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    private static float[] DecodePcm32(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (4 * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += BinaryPrimitives.ReadInt32LittleEndian(data[((f * channels + c) * 4)..]) / 2147483648f;
            mono[f] = sum / channels;
        }

        return mono;
    }

    private static float[] DecodeFloat32(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (4 * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data[((f * channels + c) * 4)..]));
            }

            mono[f] = sum / channels;
        }

        return mono;
    }
}
