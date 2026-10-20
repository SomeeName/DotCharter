using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>
///     #431: the export's in-process Ogg reader (<see cref="PcmAudioLoader.ReadOgg" />)
///     is the native engine's Vorbis decoder, no longer NVorbis. The reference is
///     NVorbis's float decode of the committed fixture, captured once into
///     <c>tone-440hz-0.3s.nvorbis-f32.bin</c> (channels, rate, sample count, floats;
///     little-endian) while NVorbis was still referenced, so this test outlives it.
///     Measured on the fixture: the same length, max |float diff| 1e-7 order, 16-bit
///     samples within one LSB.
/// </summary>
public sealed class PcmAudioOggNativeDecodeTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Services", "Parsing", "Fixtures");
    private static readonly string OggFixture = Path.Combine(Fixtures, "tone-440hz-0.3s.ogg");

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-ogg-native-").FullName;
    private readonly ITestOutputHelper _output;

    public PcmAudioOggNativeDecodeTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ReadOgg_matches_the_NVorbis_reference_within_one_lsb()
    {
        var (channels, sampleRate, reference) = ReadReference();

        var pcm = PcmAudioLoader.ReadOgg(OggFixture);

        Assert.Equal(channels, pcm.Channels);
        Assert.Equal(sampleRate, pcm.SampleRate);
        Assert.Equal(reference.Length, pcm.Samples.Length);
        var mismatches = 0;
        var maxLsb = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var lsb = Math.Abs(pcm.Samples[i] - ToPcm16(reference[i]));
            if (lsb == 0) continue;
            mismatches++;
            maxLsb = Math.Max(maxLsb, lsb);
        }

        _output.WriteLine($"{reference.Length} samples, {mismatches} differ, max {maxLsb} LSB");
        Assert.True(maxLsb <= 1, $"native Ogg PCM differs from NVorbis by {maxLsb} LSB");
    }

    [Fact]
    public void The_native_float_decode_matches_NVorbis_within_float_rounding()
    {
        var (channels, sampleRate, reference) = ReadReference();

        var native = JuceSharp.AudioIo.JuceAudioDecoder.Decode(OggFixture);

        Assert.Equal(channels, native.Channels);
        Assert.Equal(sampleRate, native.SampleRate);
        Assert.Equal(reference.Length, native.Interleaved.Length);
        double maxDiff = 0;
        for (var i = 0; i < reference.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs((double)Math.Clamp(native.Interleaved[i], -1f, 1f) - reference[i]));

        _output.WriteLine($"max |native - NVorbis| = {maxDiff:G3}");
        Assert.True(maxDiff < 1e-5, $"native Ogg decode differs from NVorbis by {maxDiff:G3}");
    }

    [Fact]
    public void ReadOgg_names_the_file_it_cannot_decode()
    {
        var bogus = Path.Combine(_dir, "not-vorbis.ogg");
        File.WriteAllBytes(bogus, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

        // The loader's own message, whatever the engine threw (a Decode status, another
        // status, or no native DLL at all).
        var ex = Assert.Throws<InvalidDataException>(() => PcmAudioLoader.ReadOgg(bogus));

        Assert.StartsWith($"Cannot decode the Ogg file '{bogus}'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The loader's float-to-16-bit formula (PcmAudioLoader.FloatToPcm16).</summary>
    private static int ToPcm16(float value) => Math.Clamp((int)Math.Round(value * 32767f), short.MinValue, short.MaxValue);

    private static (int Channels, int SampleRate, float[] Samples) ReadReference()
    {
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(Fixtures, "tone-440hz-0.3s.nvorbis-f32.bin")));
        var channels = reader.ReadInt32();
        var sampleRate = reader.ReadInt32();
        var samples = new float[reader.ReadInt32()];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = reader.ReadSingle();
        return (channels, sampleRate, samples);
    }
}
