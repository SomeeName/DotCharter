using DotCharter.Services.Parsing.Audio;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     #426/#431: the NATIVE WEM decoder (JuceSharp <c>JuceWemDecoder</c>, ABI v42) makes
///     a library song's playable WAV; vgmstream and ffmpeg are its only fallbacks (the
///     managed ww2ogg + NVorbis path is gone), and <c>TryConvertWemToPlayable</c> stays
///     the one producer. The order is pinned through the converter's seam; the last
///     case decodes a REAL Wwise RIFF Vorbis file —
///     <c>Fixtures/tone-stereo48k-inline-mod.wem</c>, built from JuceSharp's synthetic
///     <c>stereo48k.ogg</c> by its <c>WemFixtureWriter</c> (inline codebooks, mod
///     packets) — through the real DLL.
/// </summary>
public sealed class AudioFormatConverterNativeTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Services", "Parsing", "Fixtures");
    private static readonly string WemFixture = Path.Combine(Fixtures, "tone-stereo48k-inline-mod.wem");

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-wem-native-").FullName;
    private readonly FakeAppLogger _log = new();
    private readonly string _wem;
    private readonly string _wav;

    public AudioFormatConverterNativeTests()
    {
        _wem = Path.Combine(_dir, "123.wem");
        _wav = Path.ChangeExtension(_wem, ".wav");
        File.WriteAllBytes(_wem, new byte[512]);
        File.SetLastWriteTimeUtc(_wem, DateTime.UtcNow.AddMinutes(-10));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void The_native_decoder_makes_the_wav_through_a_staging_file()
    {
        var natives = 0;
        var converter = new AudioFormatConverter(_log, (wem, staging, _) =>
        {
            natives++;
            Assert.Equal(_wem, wem);
            Assert.Contains(".part-", staging, StringComparison.Ordinal);
            File.WriteAllBytes(staging, new byte[4096]);
            return true;
        });

        Assert.True(converter.TryConvertWemToPlayable(_wem, out var playable, TestContext.Current.CancellationToken));

        Assert.Equal(1, natives);
        Assert.Equal(_wav, playable);
        Assert.Equal(4096, new FileInfo(_wav).Length);
        Assert.Contains(_log.Entries, e => e.Level == "INFO" && e.Message.Contains("natively", StringComparison.Ordinal));
        AssertOnly(".wem", ".wav");
    }

    /// <summary>A WEM nobody can decode (512 zero bytes: not a RIFF file for the
    /// native decoder, vgmstream or ffmpeg) is a logged failure that leaves nothing
    /// behind — there is no managed decoder left to try.</summary>
    [Fact]
    public void A_failed_native_decode_logs_why_and_leaves_no_wav()
    {
        var converter = new AudioFormatConverter(_log, (_, staging, _) =>
        {
            File.WriteAllBytes(staging, new byte[4096]);
            throw new InvalidDataException("not a wem");
        });

        Assert.False(converter.TryConvertWemToPlayable(_wem, out var playable, TestContext.Current.CancellationToken));

        Assert.Equal("", playable);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("not a wem", StringComparison.Ordinal));
        AssertOnly(".wem");
    }

    [Fact]
    public void A_fresh_wav_is_never_decoded_again()
    {
        File.WriteAllBytes(_wav, new byte[100]);
        var converter = new AudioFormatConverter(_log, NeverNative);

        Assert.True(converter.TryConvertWemToPlayable(_wem, out _, TestContext.Current.CancellationToken));
        Assert.Equal(100, new FileInfo(_wav).Length);
    }

    [Fact]
    public void A_cancel_inside_the_native_decode_leaves_no_wav_and_no_staging_file()
    {
        using var cts = new CancellationTokenSource();
        var converter = new AudioFormatConverter(_log, (_, staging, ct) =>
        {
            File.WriteAllBytes(staging, new byte[4096]);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return true;
        });

        Assert.ThrowsAny<OperationCanceledException>(() => converter.TryConvertWemToPlayable(_wem, out _, cts.Token));
        AssertOnly(".wem");
    }

    /// <summary>
    ///     The real decoder on a real Wwise RIFF Vorbis file: the WAV has the header
    ///     every cached WAV has (PCM16, the WEM's channels and rate, a data chunk of
    ///     exactly its frames), and every sample is the cache's WAV formula applied to
    ///     the decoder's own float output (clamp, -1 → -32768, else round-half-even of
    ///     <c>sample * 32767</c>) — bit for bit.
    /// </summary>
    [Fact]
    public void The_real_native_decode_writes_the_cache_wav_formula_bit_for_bit()
    {
        var wem = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName, "song.wem");
        File.Copy(WemFixture, wem);

        Assert.True(new AudioFormatConverter(_log).TryConvertWemToPlayable(wem, out var wavPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_log.Entries, e => e.Level == "WARN");

        var floats = JuceWemDecoder.DecodeFile(wem, out var info);
        var wav = File.ReadAllBytes(wavPath);
        Assert.Equal(2, info.Channels);
        Assert.Equal(48000, info.SampleRate);
        Assert.True(info.Frames > 48000, $"The fixture decodes to {info.Frames} frames; it is 1.5 s of stereo.");
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));
        Assert.Equal(info.Channels, BitConverter.ToInt16(wav, 22));
        Assert.Equal(info.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(info.Frames * info.Channels * 2, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + floats.Length * 2, wav.Length);
        for (var i = 0; i < floats.Length; i++)
        {
            var expected = CacheWavFormula(floats[i]);
            var actual = BitConverter.ToInt16(wav, 44 + i * 2);
            if (actual != expected) Assert.Fail($"sample {i}: {floats[i]:R} wrote {actual}, the cache formula gives {expected}");
        }
    }

    /// <summary>The 16-bit formula every cached WAV was written with (formerly
    /// AudioFormatConverter.ConvertToPcm16; now the native s16 read).</summary>
    private static short CacheWavFormula(float value)
    {
        var sample = Math.Clamp(value, -1.0f, 1.0f);
        return sample <= -1.0f ? short.MinValue : (short)Math.Round(sample * short.MaxValue);
    }

    private static bool NeverNative(string wem, string staging, CancellationToken ct) =>
        throw new InvalidOperationException("The native decoder must not run here.");

    private void AssertOnly(params string[] extensions) =>
        Assert.Equal(
            extensions.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(_dir).Select(Path.GetExtension).Order(StringComparer.Ordinal));
}
