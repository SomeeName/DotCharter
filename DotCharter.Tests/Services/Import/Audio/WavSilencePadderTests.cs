using System.Buffers.Binary;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     #313: a WAVE song or stem is padded without a re-encode — zero frames around the untouched
///     sample bytes, every other chunk kept, the RIFF sizes rewritten.
/// </summary>
public sealed class WavSilencePadderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "wavpad-" + Guid.NewGuid().ToString("N"));

    public WavSilencePadderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>A WAVE file: optional LIST chunks before and after the data chunk.</summary>
    internal static byte[] Wav(int sampleRate, short channels, short bits, byte[] data, ushort tag = 1,
        bool listBefore = false, bool listAfter = false, uint? declaredDataSize = null, bool extensible = false)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(0u);
        w.Write("WAVE"u8);
        if (listBefore) WriteList(w);
        var blockAlign = (short)(channels * bits / 8);
        w.Write("fmt "u8);
        w.Write(extensible ? 40u : 16u);
        w.Write(extensible ? (ushort)0xFFFE : tag);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);
        w.Write(blockAlign);
        w.Write(bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write(bits);
            w.Write(3u);
            w.Write(tag);
            w.Write(new byte[14]);
        }

        w.Write("data"u8);
        w.Write(declaredDataSize ?? (uint)data.Length);
        w.Write(data);
        if ((data.Length & 1) != 0) w.Write((byte)0);
        if (listAfter) WriteList(w);
        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteList(BinaryWriter w)
    {
        w.Write("LIST"u8);
        w.Write(4u);
        w.Write("INFO"u8);
    }

    private static byte[] Samples(int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = (byte)(i % 250 + 1);
        return bytes;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Pads_16bit_stereo_with_whole_zero_frames_around_the_untouched_samples()
    {
        var samples = Samples(4 * 100);
        var source = Write("a.wav", Wav(1000, 2, 16, samples, listBefore: true, listAfter: true));
        var target = Path.Combine(_dir, "a-silence1.wav");

        Assert.True(WavSilencePadder.TryPad(source, target, 0.5, 0.25));

        var bytes = File.ReadAllBytes(target);
        using var stream = new MemoryStream(bytes);
        var layout = WavSilencePadder.ReadLayout(stream)!;
        Assert.Equal(1000, layout.SampleRate);
        Assert.Equal(4, layout.BlockAlign);
        Assert.Equal((500 + 100 + 250) * 4, layout.DataLength);
        var data = bytes.AsSpan((int)layout.DataOffset, (int)layout.DataLength);
        Assert.True(data[..2000].IndexOfAnyExcept((byte)0) < 0, "the lead is not silent");
        Assert.True(data.Slice(2000, samples.Length).SequenceEqual(samples), "the samples changed");
        Assert.True(data[(2000 + samples.Length)..].IndexOfAnyExcept((byte)0) < 0, "the trail is not silent");
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.True(bytes.AsSpan(bytes.Length - 12).SequenceEqual("LIST\u0004\0\0\0INFO"u8), "the chunk after the data was lost");
        Assert.Equal(File.ReadAllBytes(source), Wav(1000, 2, 16, samples, listBefore: true, listAfter: true));
    }

    [Fact]
    public void An_odd_frame_size_keeps_the_RIFF_word_alignment()
    {
        // Mono 24-bit: three bytes a frame, so the data chunk can be odd and needs its pad byte.
        var source = Write("b.wav", Wav(10, 1, 24, Samples(3 * 3), listAfter: true));
        var target = Path.Combine(_dir, "b-silence1.wav");

        Assert.True(WavSilencePadder.TryPad(source, target, 0.2, 0));

        var bytes = File.ReadAllBytes(target);
        using var stream = new MemoryStream(bytes);
        var layout = WavSilencePadder.ReadLayout(stream)!;
        Assert.Equal(15, layout.DataLength);
        Assert.Equal(layout.DataOffset + 16, layout.AfterDataOffset);
        Assert.True(bytes.AsSpan((int)layout.AfterDataOffset, 4).SequenceEqual("LIST"u8));
    }

    [Fact]
    public void Extensible_float_is_padded()
    {
        var source = Write("c.wav", Wav(100, 2, 32, Samples(8 * 10), tag: 3, extensible: true));
        var target = Path.Combine(_dir, "c-silence1.wav");

        Assert.True(WavSilencePadder.TryPad(source, target, 0.1, 0));

        using var stream = File.OpenRead(target);
        Assert.Equal(8 * 20, WavSilencePadder.ReadLayout(stream)!.DataLength);
    }

    [Theory]
    [InlineData(8, (ushort)1)]   // unsigned: zero is full negative, not silence
    [InlineData(16, (ushort)2)]  // ADPCM
    public void A_format_where_zero_is_not_silence_is_refused(short bits, ushort tag)
    {
        var source = Write("d.wav", Wav(100, 1, bits, Samples(40), tag: tag));
        var target = Path.Combine(_dir, "d-silence1.wav");

        Assert.False(WavSilencePadder.TryPad(source, target, 0.1, 0));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [Fact]
    public void A_data_size_past_the_end_reads_the_samples_that_are_there()
    {
        var source = Write("e.wav", Wav(100, 1, 16, Samples(20), declaredDataSize: 1_000_000));
        var target = Path.Combine(_dir, "e-silence1.wav");

        Assert.True(WavSilencePadder.TryPad(source, target, 0.1, 0));

        using var stream = File.OpenRead(target);
        Assert.Equal(20 + 20, WavSilencePadder.ReadLayout(stream)!.DataLength);
    }

    [Fact]
    public void An_existing_target_is_never_overwritten()
    {
        var source = Write("f.wav", Wav(100, 1, 16, Samples(20)));
        var target = Write("f-silence1.wav", [1, 2, 3]);

        Assert.False(WavSilencePadder.TryPad(source, target, 0.1, 0));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
    }
}
