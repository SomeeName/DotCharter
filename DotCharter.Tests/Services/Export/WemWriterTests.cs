using System.Buffers.Binary;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Pins the Wwise-free .wem writer against the container layout taken from
///     real Rocksmith wems, and pins the IMA-ADPCM codec against actual signal
///     reconstruction — not merely against itself.
/// </summary>
public class WemWriterTests
{
    private const int SampleRate = 48000;

    // Field offsets inside the RIFF header. These are the contract; if one moves,
    // Rocksmith's Wwise runtime reads a different field.
    private const int OffFmtSize = 0x10;
    private const int OffFormatTag = 0x14;
    private const int OffChannels = 0x16;
    private const int OffSampleRate = 0x18;
    private const int OffAvgBytes = 0x1C;
    private const int OffBlockAlign = 0x20;
    private const int OffBitsPerSample = 0x22;
    private const int OffCbSize = 0x24;
    private const int OffChannelConfig = 0x28; // == 40 decimal

    [Fact]
    public void Pcm_wem_has_the_rocksmith_container_layout()
    {
        var pcm = Tone(frames: 1000, channels: 2);
        var bytes = WriteWem(WemCodec.Pcm, pcm, channels: 2);

        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal("fmt "u8.ToArray(), bytes[12..16]);

        // RIFF size counts everything after the 8-byte preamble.
        Assert.Equal((uint)(bytes.Length - 8), U32(bytes, 4));

        Assert.Equal(0x18u, U32(bytes, OffFmtSize));
        Assert.Equal(0x0001, U16(bytes, OffFormatTag)); // WAVE_FORMAT_PCM
        Assert.Equal(2, U16(bytes, OffChannels));
        Assert.Equal((uint)SampleRate, U32(bytes, OffSampleRate));
        Assert.Equal(4, U16(bytes, OffBlockAlign));      // 2ch * 16-bit
        Assert.Equal(16, U16(bytes, OffBitsPerSample));
        Assert.Equal((uint)(SampleRate * 4), U32(bytes, OffAvgBytes));
        Assert.Equal(6, U16(bytes, OffCbSize));

        // The whole reason Rocksmith2014.NET post-patches a wem: the old runtime
        // reads offset 40 as a legacy channel MASK, and 3 means stereo.
        Assert.Equal(3u, U32(bytes, OffChannelConfig));
    }

    [Fact]
    public void Pcm_wem_payload_is_the_original_samples_byte_for_byte()
    {
        var pcm = Tone(frames: 777, channels: 2);
        var bytes = WriteWem(WemCodec.Pcm, pcm, channels: 2);

        var (dataOffset, dataLength) = FindDataChunk(bytes);
        Assert.Equal(pcm.Length * sizeof(short), dataLength);

        for (var i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + (i * 2), 2)));
    }

    /// <summary>Rocksmith 2014 does not play an IMA-ADPCM wem: the song loads and its
    /// timeline never advances (owner-confirmed in-game, #717). The writer must
    /// refuse it outright rather than produce a package that cannot be played.
    /// The codec stays readable (<see cref="InvokeDecode" />) for wems built before.</summary>
    [Fact]
    public void Adpcm_is_refused_because_the_game_does_not_play_it()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(() =>
            WemWriter.Write(output, WemCodec.ImaAdpcm, Tone(frames: 500, channels: 2), 2, SampleRate));
        Assert.Contains("#717", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    ///     The one that matters. A round-trip through my own encoder+decoder would
    ///     pass even if the quantizer were wrong, so this asserts the decoded signal
    ///     actually resembles the INPUT: &gt;20 dB SNR on a real waveform. A broken
    ///     predictor or step-table walk lands far below that.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Adpcm_reconstructs_the_signal(int channels)
    {
        var pcm = Tone(frames: 4096, channels);

        var encoded = InvokeEncode(pcm, channels);
        var decoded = InvokeDecode(encoded, channels);

        Assert.True(decoded.Length >= pcm.Length, "decoder returned fewer samples than were encoded");

        double signal = 0, noise = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            double s = pcm[i];
            double e = pcm[i] - decoded[i];
            signal += s * s;
            noise += e * e;
        }

        var snrDb = 10.0 * Math.Log10(signal / Math.Max(noise, 1e-9));
        Assert.True(snrDb > 20.0, $"ADPCM reconstruction SNR was {snrDb:F1} dB (expected > 20 dB)");
    }

    /// <summary>
    ///     The codec adds no delay: a sound encoded at sample N decodes at sample N.
    ///     The chart is timed against the PCM handed in (the editor's decode —
    ///     <c>PcmAudioLoaderEditorParityTests</c>), so any shift here would move every
    ///     note against the music in-game. A burst starting mid-block, in both
    ///     channel layouts.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Adpcm_keeps_every_sound_on_its_own_sample(int channels)
    {
        const int frames = 48000, burstAt = 24_001, burstLength = 1440;
        var pcm = new short[frames * channels];
        for (var f = burstAt; f < burstAt + burstLength; f++)
        {
            for (var c = 0; c < channels; c++)
                pcm[f * channels + c] = (short)(Math.Sin(2 * Math.PI * 1000 * f / 48000.0) * 20000);
        }

        var decoded = InvokeDecode(InvokeEncode(pcm, channels), channels);

        // Cross-correlation, not a threshold crossing: ADPCM's quantiser starts
        // small after silence, so the first LOUD sample can come a few samples
        // late with no delay at all. A delay moves the whole burst.
        var bestLag = 0;
        var best = double.MinValue;
        for (var lag = -64; lag <= 64; lag++)
        {
            double sum = 0;
            for (var f = burstAt; f < burstAt + burstLength; f++)
                sum += pcm[f * channels] * (double)decoded[(f + lag) * channels];
            if (sum > best) { best = sum; bestLag = lag; }
        }

        Assert.True(bestLag == 0, $"the ADPCM decode is shifted by {bestLag} sample(s) against what was encoded");
    }

    [Fact]
    public void Adpcm_tracks_a_full_scale_step_without_diverging()
    {
        // A hard transient is where a mistuned step index runs away.
        var pcm = new short[512];
        for (var i = 256; i < pcm.Length; i++) pcm[i] = short.MaxValue;

        var decoded = InvokeDecode(InvokeEncode(pcm, 1), 1);

        // It converges within a block rather than oscillating or clipping to silence.
        Assert.True(decoded[511] > 30000, $"failed to reach full scale (got {decoded[511]})");
    }

    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public void Written_wem_is_detected_as_its_own_codec(WemCodec codec)
    {
        var bytes = WriteWem(codec, Tone(frames: 256, channels: 2), channels: 2);
        using var stream = new MemoryStream(bytes);

        Assert.Equal(codec, WemCodecInfoDetect(stream));
        Assert.Equal(0L, stream.Position); // detection must not disturb the stream
    }

    /// <summary>Vorbis needs no Wwise any more: JuceSharp's encoder (ABI v44, #317)
    /// writes it, and the native decoder reads it back frame-exact as the
    /// aoTuV-library, mod-packet layout every Rocksmith wem uses.</summary>
    [Fact]
    public void Vorbis_is_written_without_wwise_and_decodes_frame_exact()
    {
        var pcm = Tone(frames: 48_000 + 77, channels: 2);
        var bytes = WriteWem(WemCodec.Vorbis, pcm, channels: 2);

        Assert.Equal(0xFFFF, U16(bytes, OffFormatTag));
        Assert.Equal(3u, U32(bytes, OffChannelConfig));
        using var decoder = JuceSharp.JuceWemDecoder.OpenMemory(bytes);
        Assert.Equal(JuceSharp.JuceWemVariant.AoTuV, decoder.Info.Variant);
        Assert.True(decoder.Info.ModPackets);
        Assert.Equal(48_000 + 77, decoder.Info.Frames);
    }

    [Theory]
    [InlineData(44_100, 2)]
    [InlineData(48_000, 1)]
    public void Vorbis_is_refused_for_anything_but_48_khz_stereo(int rate, int channels)
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            WemWriter.Write(output, WemCodec.Vorbis, new short[channels * 64], channels, rate));
    }

    /// <summary>A real Rocksmith wem must still read as Vorbis — guards the detector
    /// against a change that would make us mislabel existing audio as PCM and emit a
    /// bank pointing at the wrong decoder. Skipped when TestData is absent (it is
    /// git-ignored).</summary>
    [Fact]
    public void Real_rocksmith_wem_is_detected_as_vorbis()
    {
        var reference = FindReferenceWem();
        if (reference is null)
        {
            Assert.Skip("No real Rocksmith .wem: TestData/ is absent (it is git-ignored) — the only " +
                        "guard against mislabelling real Vorbis audio as PCM is DARK on this machine.");
        }

        using var stream = File.OpenRead(reference);
        Assert.Equal(WemCodec.Vorbis, WemCodecInfoDetect(stream));
    }

    /// <summary>
    ///     The strongest structural claim available without the game: our
    ///     PCM and ADPCM containers walk chunk-for-chunk like a wem the game
    ///     DOES load. Every one of the ~22,500 wems Rocksmith ships is Vorbis,
    ///     so the reference can only vouch for the CONTAINER (RIFF preamble,
    ///     `fmt ` first, the `cbSize` extension present, the legacy channel MASK
    ///     at offset 40, `data` last and exactly filling the file) — never for
    ///     the codec. Whether the runtime accepts a PCM/ADPCM payload is
    ///     ACCEPTED UNVERIFIED (verified only by
    ///     desk-check): the two test packages are installed and a rejected codec
    ///     is SILENT in-game, so only ears decide it. Skips when TestData is
    ///     absent (it is git-ignored).
    /// </summary>
    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public void Our_wem_walks_chunk_for_chunk_like_a_real_rocksmith_vorbis_wem(WemCodec codec)
    {
        var reference = FindReferenceWem();
        if (reference is null)
            Assert.Skip("No real Rocksmith .wem: TestData/ is absent (it is git-ignored).");

        var real = File.ReadAllBytes(reference);
        var ours = WriteWem(codec, Tone(frames: 4096, channels: 2), channels: 2);

        var realChunks = WalkChunks(real);
        var ourChunks = WalkChunks(ours);

        // Same preamble, same first chunk, and RIFF's size counts the whole
        // file after the 8-byte preamble in both.
        Assert.Equal("RIFF"u8.ToArray(), real[..4]);
        Assert.Equal("WAVE"u8.ToArray(), real[8..12]);
        Assert.Equal((uint)(real.Length - 8), U32(real, 4));
        Assert.Equal((uint)(ours.Length - 8), U32(ours, 4));
        Assert.Equal("fmt ", realChunks[0].Id);
        Assert.Equal("fmt ", ourChunks[0].Id);

        // `fmt ` carries the WAVEFORMATEX extension (cbSize > 0) in both — the
        // real one's codec-specific block is larger, but the field the old
        // runtime reads as a channel MASK sits at the same offset and says
        // "stereo" in both.
        Assert.True(U16(real, OffCbSize) > 0);
        Assert.True(U16(ours, OffCbSize) > 0);
        Assert.Equal(2, U16(real, OffChannels));
        Assert.Equal(U32(real, OffChannelConfig), U32(ours, OffChannelConfig));

        // `data` is the LAST chunk and ends exactly at end of file in both — a
        // trailing chunk or a short data chunk is what a decoder trips on.
        Assert.Equal("data", realChunks[^1].Id);
        Assert.Equal("data", ourChunks[^1].Id);
        Assert.Equal(real.Length, realChunks[^1].Offset + 8 + realChunks[^1].Length);
        Assert.Equal(ours.Length, ourChunks[^1].Offset + 8 + ourChunks[^1].Length);

        // Everything the real file carries between fmt and data is Vorbis-only
        // (the codec's own setup); a PCM/ADPCM wem legitimately has nothing
        // there. What must hold is that OUR walk is a strict subset in order.
        var ourIds = ourChunks.Select(c => c.Id).ToList();
        var realIds = realChunks.Select(c => c.Id).ToList();
        var cursor = 0;
        foreach (var id in ourIds)
        {
            cursor = realIds.IndexOf(id, cursor);
            Assert.True(cursor >= 0, $"our '{id}' chunk has no counterpart in the real wem's order: {string.Join(",", realIds)}");
            cursor++;
        }
    }

    private static List<(string Id, int Offset, int Length)> WalkChunks(byte[] wem)
    {
        var chunks = new List<(string, int, int)>();
        var i = 12;
        while (i + 8 <= wem.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wem, i, 4);
            var length = (int)U32(wem, i + 4);
            chunks.Add((id, i, length));
            i += 8 + length + (length & 1); // RIFF pads odd chunks
        }
        Assert.NotEmpty(chunks);
        return chunks;
    }

    // ---- helpers ------------------------------------------------------------

    private static byte[] WriteWem(WemCodec codec, short[] pcm, int channels)
    {
        using var output = new MemoryStream();
        var written = WemWriter.Write(output, codec, pcm, channels, SampleRate);
        Assert.Equal(output.Length, written);
        return output.ToArray();
    }

    /// <summary>A decaying mixed-harmonic tone — a real-ish waveform, not a ramp:
    /// ADPCM flatters constant slopes.</summary>
    private static short[] Tone(int frames, int channels)
    {
        var pcm = new short[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var t = f / (double)SampleRate;
            var env = Math.Exp(-3.0 * t * 10);
            var v = Math.Sin(2 * Math.PI * 220 * t) * 0.6
                    + Math.Sin(2 * Math.PI * 660 * t) * 0.3
                    + Math.Sin(2 * Math.PI * 1750 * t) * 0.1;
            var s = (short)Math.Clamp(v * env * 20000, short.MinValue, short.MaxValue);
            for (var c = 0; c < channels; c++)
                pcm[(f * channels) + c] = c == 0 ? s : (short)(s * 0.8);
        }

        return pcm;
    }

    private static (int Offset, int Length) FindDataChunk(byte[] wem)
    {
        for (var i = 12; i + 8 <= wem.Length; i += 2)
        {
            if (wem[i] == (byte)'d' && wem[i + 1] == (byte)'a' && wem[i + 2] == (byte)'t' && wem[i + 3] == (byte)'a')
                return (i + 8, (int)U32(wem, i + 4));
        }

        throw new InvalidOperationException("no data chunk");
    }

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    private static int U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2));

    // WwiseImaAdpcm and WemCodecInfo are internal; DotCharter.Tests has
    // InternalsVisibleTo, so these just keep the call sites readable.
    private static byte[] InvokeEncode(short[] pcm, int channels) =>
        DotCharter.Services.Export.Audio.WwiseImaAdpcm.Encode(pcm, channels);

    private static short[] InvokeDecode(byte[] data, int channels) =>
        DotCharter.Services.Export.Audio.WwiseImaAdpcm.Decode(data, channels);

    private static WemCodec? WemCodecInfoDetect(Stream s) =>
        DotCharter.Services.Export.Audio.WemCodecInfo.Detect(s);

    private static string? FindReferenceWem()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "TestData")))
            dir = dir.Parent;

        if (dir is null) return null;
        var testData = Path.Combine(dir.FullName, "TestData", "EOF");
        if (!Directory.Exists(testData)) return null;

        return Directory.EnumerateFiles(testData, "guitar.wem", SearchOption.AllDirectories).FirstOrDefault();
    }
}
