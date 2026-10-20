using System.Buffers.Binary;

namespace DotCharter.Services.Import.Audio;

/// <summary>
///     Pads a RIFF/WAVE file with silent frames at the start and the end without re-encoding it:
/// the samples are copied byte for byte and whole zero frames are written around them,
///     so a padded stem is the stem, later. Zero is silence for signed PCM (16/24/32-bit) and IEEE
///     float, the formats separated stems and decoded songs use. 8-bit PCM (unsigned, silent at
///     0x80), compressed WAVE and RF64 are refused; the caller re-encodes those instead.
/// </summary>
public static class WavSilencePadder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>What the padder needs to know about a WAVE file.</summary>
    /// <param name="SampleRate">Frames per second.</param>
    /// <param name="BlockAlign">Bytes per frame, all channels.</param>
    /// <param name="DataOffset">Where the sample bytes start (just past the data chunk header).</param>
    /// <param name="DataLength">Sample bytes, a whole number of frames.</param>
    /// <param name="AfterDataOffset">Where the chunks after the data chunk start (past its pad byte).</param>
    public sealed record WavLayout(int SampleRate, int BlockAlign, long DataOffset, long DataLength, long AfterDataOffset);

    /// <summary>The layout of a WAVE the padder can pad, or null for anything else.</summary>
    public static WavLayout? ReadLayout(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[12];
        if (stream.Length < 12 || stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12) return null;
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) return null;

        int? sampleRate = null;
        var blockAlign = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var start = stream.Position;
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || start + size > stream.Length) return null;
                var fmt = new byte[size];
                stream.ReadExactly(fmt);
                if (!IsZeroSilent(fmt)) return null;
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (sampleRate is not > 0 || blockAlign <= 0 || size == uint.MaxValue) return null;
                // A writer that never closed the file leaves a size past the end: the samples
                // that are there are the data.
                var length = Math.Min(size, stream.Length - start);
                length -= length % blockAlign;
                var after = Math.Min(stream.Length, start + size + (size & 1));
                return new WavLayout(sampleRate.Value, blockAlign, start, length, after);
            }

            stream.Position = start + size + (size & 1);
        }

        return null;
    }

    /// <summary>
    ///     Writes <paramref name="targetPath" />: the source with <paramref name="leadingSeconds" /> of
    ///     silence before its samples and <paramref name="trailingSeconds" /> after. False, and no
    ///     target, when the source is not a WAVE this can pad or the result would not fit a RIFF.
    /// </summary>
    public static bool TryPad(string sourcePath, string targetPath, double leadingSeconds, double trailingSeconds)
    {
        var partial = targetPath + ".partial";
        try
        {
            using (var source = File.OpenRead(sourcePath))
            {
                var layout = ReadLayout(source);
                if (layout is null) return false;

                var leadBytes = Frames(leadingSeconds, layout.SampleRate) * layout.BlockAlign;
                var trailBytes = Frames(trailingSeconds, layout.SampleRate) * layout.BlockAlign;
                var dataLength = leadBytes + layout.DataLength + trailBytes;
                var tail = source.Length - layout.AfterDataOffset;
                var riffLength = layout.DataOffset - 8 + dataLength + (dataLength & 1) + tail;
                if (dataLength > uint.MaxValue || riffLength > uint.MaxValue) return false;

                using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                source.Position = 0;
                Copy(source, target, layout.DataOffset - 8);
                target.Write("data"u8);
                WriteUInt32(target, (uint)dataLength);
                WriteZeros(target, leadBytes);
                source.Position = layout.DataOffset;
                Copy(source, target, layout.DataLength);
                WriteZeros(target, trailBytes);
                if ((dataLength & 1) != 0) target.WriteByte(0);
                source.Position = layout.AfterDataOffset;
                Copy(source, target, tail);
                target.Position = 4;
                WriteUInt32(target, (uint)riffLength);
            }

            File.Move(partial, targetPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(partial)) BestEffortDelete.File(partial);
        }
    }

    /// <summary>Whole frames of silence for <paramref name="seconds" /> (none for zero or less).</summary>
    public static long Frames(double seconds, int sampleRate) =>
        seconds > 0 ? (long)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero) : 0;

    /// <summary>True when a zero byte pattern is silence in this format.</summary>
    private static bool IsZeroSilent(ReadOnlySpan<byte> fmt)
    {
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        // WAVE_FORMAT_EXTENSIBLE carries the real format in the first two bytes of its sub-format GUID.
        if (tag == FormatExtensible && fmt.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        return tag switch
        {
            FormatPcm => bits is 16 or 24 or 32,
            FormatFloat => bits is 32 or 64,
            _ => false
        };
    }

    private static void Copy(Stream source, Stream target, long count)
    {
        var buffer = new byte[CopyBufferBytes];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static void WriteZeros(Stream target, long count)
    {
        var zeros = new byte[(int)Math.Min(CopyBufferBytes, Math.Max(1, count))];
        while (count > 0)
        {
            var n = (int)Math.Min(zeros.Length, count);
            target.Write(zeros, 0, n);
            count -= n;
        }
    }

    private static void WriteUInt32(Stream target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.Write(bytes);
    }
}
