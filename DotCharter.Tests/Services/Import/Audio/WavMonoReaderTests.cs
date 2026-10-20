using System.Buffers.Binary;
using DotCharter.Services.Import.Audio;

namespace DotCharter.Tests.Services.Import.Audio;

public sealed class WavMonoReaderTests
{
    private static byte[] BuildWav(short formatTag, short channels, int sampleRate, short bits, byte[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write(formatTag);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bits / 8)); // byte rate
        w.Write((short)(channels * (bits / 8))); // block align
        w.Write(bits);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Pcm16Stereo_MixesToMono()
    {
        // Two frames: L=+0.5/-0.5, R=0 → mono +0.25/-0.25.
        var data = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(6), 0);

        var (samples, rate) = WavMonoReader.Read(BuildWav(1, 2, 48000, 16, data));

        Assert.Equal(48000, rate);
        Assert.Equal(2, samples.Length);
        Assert.Equal(0.25f, samples[0], 3);
        Assert.Equal(-0.25f, samples[1], 3);
    }

    [Fact]
    public void Float32Mono_ReadsVerbatim()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), BitConverter.SingleToInt32Bits(0.75f));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), BitConverter.SingleToInt32Bits(-0.125f));

        var (samples, rate) = WavMonoReader.Read(BuildWav(3, 1, 44100, 32, data));

        Assert.Equal(44100, rate);
        Assert.Equal(new[] { 0.75f, -0.125f }, samples);
    }

    [Fact]
    public void Pcm24Mono_Decodes()
    {
        // +0.5 in 24-bit = 0x400000.
        var data = new byte[] { 0x00, 0x00, 0x40 };
        var (samples, _) = WavMonoReader.Read(BuildWav(1, 1, 48000, 24, data));
        Assert.Single(samples);
        Assert.Equal(0.5f, samples[0], 3);
    }

    [Fact]
    public void NonWav_Throws() => Assert.Throws<InvalidDataException>(() => WavMonoReader.Read([1, 2, 3, 4]));

    [Fact]
    public void UnsupportedBitDepth_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            WavMonoReader.Read(BuildWav(1, 1, 48000, 8, [0x80])));
    }

    /// <summary>
    ///     A chunk size near <see cref="int.MaxValue" /> must be clamped, not
    ///     wrapped. The bounds guard added `pos + chunkSize` in unchecked int, so
    ///     a header declaring 0x7FFFFFF0 at any offset past 15 made the sum
    ///     NEGATIVE, the guard did not fire, and the `Slice` threw
    ///     <see cref="ArgumentOutOfRangeException" /> — not the
    ///     <see cref="InvalidDataException" /> every caller of this reader
    ///     handles. Reached by chart-from-audio on any user-supplied .wav.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue - 15)] // 0x7FFFFFF0 — wraps the sum at this offset
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue / 2)]  // does NOT wrap; the plain clamp still has to hold
    public void AChunkSizeThatWouldOverflowTheBoundsCheck_IsClampedNotWrapped(int declaredChunkSize)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(56);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(declaredChunkSize); // the lie
        w.Write(new byte[44]);      // a plausible-looking but truncated fmt body

        var bytes = ms.ToArray();
        Assert.Equal(64, bytes.Length);

        Assert.Throws<InvalidDataException>(() => WavMonoReader.Read(bytes));
    }
}
