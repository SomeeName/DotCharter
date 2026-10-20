using DotCharter.Services.Parsing.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     #425: the WAV beside the WEM is the playable file and the cache, and no WAV
///     is ever left half-written. #431: an Ogg an older build left beside the WEM
///     is deleted, never read. The native decode is replaced through the
///     converter's seam (the WEM here is not a real Wwise file).
/// </summary>
public sealed class AudioFormatConverterPlayableTests : IDisposable
{
    private static readonly string OggFixture =
        Path.Combine(AppContext.BaseDirectory, "Services", "Parsing", "Fixtures", "tone-440hz-0.3s.ogg");

    private static readonly byte[] DecodedWav = PcmWave(sampleRate: 48000, channels: 2, frames: 4800);

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-playable-").FullName;
    private readonly string _wem;
    private readonly string _wav;
    private readonly string _ogg;

    public AudioFormatConverterPlayableTests()
    {
        _wem = Path.Combine(_dir, "123.wem");
        _wav = Path.ChangeExtension(_wem, ".wav");
        _ogg = Path.ChangeExtension(_wem, ".ogg");
        File.WriteAllBytes(_wem, new byte[512]);
        File.SetLastWriteTimeUtc(_wem, DateTime.UtcNow.AddMinutes(-10));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void A_fresh_wav_is_served_as_is_and_a_leftover_ogg_is_dropped()
    {
        var wavBytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(_wav, wavBytes);
        File.Copy(OggFixture, _ogg);

        Assert.True(Converter(NeverDecode).TryConvertWemToPlayable(_wem, out var playable, TestContext.Current.CancellationToken));

        Assert.Equal(_wav, playable);
        Assert.Equal(wavBytes, File.ReadAllBytes(_wav));
        Assert.False(File.Exists(_ogg));
    }

    [Fact]
    public void A_fresh_ogg_from_an_older_build_is_deleted_unread_and_the_wav_is_decoded_from_the_wem()
    {
        File.Copy(OggFixture, _ogg);
        File.SetLastWriteTimeUtc(_ogg, DateTime.UtcNow);
        var decodes = 0;

        Assert.True(Converter((wem, staging, _) =>
        {
            decodes++;
            Assert.Equal(_wem, wem);
            File.WriteAllBytes(staging, DecodedWav);
            return true;
        }).TryConvertWemToPlayable(_wem, out var playable, TestContext.Current.CancellationToken));

        Assert.Equal(1, decodes);
        Assert.Equal(_wav, playable);
        Assert.Equal(DecodedWav, File.ReadAllBytes(_wav));
        AssertOnly(".wem", ".wav");
    }

    [Fact]
    public void A_new_conversion_writes_only_the_wav_and_the_next_call_is_a_cache_hit()
    {
        var decodes = 0;
        var converter = Converter((_, staging, _) =>
        {
            decodes++;
            File.WriteAllBytes(staging, DecodedWav);
            return true;
        });

        Assert.True(converter.TryConvertWemToPlayable(_wem, out var playable, TestContext.Current.CancellationToken));

        Assert.Equal(1, decodes);
        Assert.Equal(_wav, playable);
        AssertPcmWave(_wav);
        AssertOnly(".wem", ".wav");

        Assert.True(converter.TryConvertWemToPlayable(_wem, out _, TestContext.Current.CancellationToken));
        Assert.Equal(1, decodes);
    }

    [Fact]
    public void A_cancel_during_the_decode_leaves_no_wav_and_no_staging_file()
    {
        using var cts = new CancellationTokenSource();
        var converter = Converter((_, staging, ct) =>
        {
            File.WriteAllBytes(staging, DecodedWav);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return true;
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            converter.TryConvertWemToPlayable(_wem, out _, cts.Token));

        AssertOnly(".wem");
    }

    [Fact]
    public void A_staged_write_moves_into_place_only_when_it_completed()
    {
        var target = Path.Combine(_dir, "out.wav");

        Assert.Throws<IOException>(() => AudioFormatConverter.TryWriteStaged(target, staging =>
        {
            File.WriteAllBytes(staging, new byte[4096]);
            throw new IOException("disk full");
        }));
        Assert.False(AudioFormatConverter.TryWriteStaged(target, staging =>
        {
            File.WriteAllBytes(staging, new byte[4096]);
            return false;
        }));
        Assert.False(File.Exists(target));
        AssertOnly(".wem");

        Assert.True(AudioFormatConverter.TryWriteStaged(target, staging =>
        {
            File.WriteAllBytes(staging, new byte[4096]);
            return true;
        }));
        Assert.Equal(4096, new FileInfo(target).Length);
        AssertOnly(".wem", ".wav");
    }

    private static bool NeverDecode(string wem, string staging, CancellationToken ct) =>
        throw new InvalidOperationException("The decoder must not run here.");

    private static AudioFormatConverter Converter(Func<string, string, CancellationToken, bool> decode) =>
        new(new FakeAppLogger(), decode);

    /// <summary>A 16-bit PCM WAV of silence, header included.</summary>
    private static byte[] PcmWave(int sampleRate, int channels, int frames)
    {
        var data = frames * channels * 2;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + data);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(data);
            writer.Write(new byte[data]);
        }

        return stream.ToArray();
    }

    private static void AssertPcmWave(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 44 + 1000, $"WAV too short: {bytes.Length} bytes.");
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal(bytes.Length - 44, BitConverter.ToInt32(bytes, 40));
    }

    /// <summary>The folder holds exactly one file per listed extension — no
    /// <c>.ogg</c> and no <c>.part-*</c> staging file left behind.</summary>
    private void AssertOnly(params string[] extensions) =>
        Assert.Equal(
            extensions.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(_dir).Select(Path.GetExtension).Order(StringComparer.Ordinal));
}
