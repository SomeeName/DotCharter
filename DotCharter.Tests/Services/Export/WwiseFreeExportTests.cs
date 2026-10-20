using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="WwiseFreeExportTests" /> that need no package reader; its fixtures are in <c>WwiseFreeExportTests.Fixtures.cs</c>.</summary>
public partial class WwiseFreeExportTests
{
    [Theory]
    [InlineData(WemCodec.Pcm, 65537u)]      // 0x00010001
    [InlineData(WemCodec.ImaAdpcm, 131073u)] // 0x00020001
    [InlineData(WemCodec.Vorbis, 262145u)]   // 0x00040001
    public void Bank_declares_the_codec_plugin_id(WemCodec codec, uint expectedPluginId)
    {
        using var audio = new MemoryStream(new byte[60_000]);
        using var bank = new MemoryStream();

        SoundBankWriter.Generate("song", audio, bank, -7.0f, isPreview: false, codec);

        Assert.Equal(expectedPluginId, ReadSoundObjectPluginId(bank.ToArray()));
    }

    [Fact]
    public async Task Encoded_wem_is_cached_and_not_re_encoded_on_the_next_build()
    {
        var wav = WriteWav(Path.Combine(_tempDir, "guitar.wav"), seconds: 1);
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());

        RocksmithBuildOptions Options(string name) => new()
        {
            OutputPath = Path.Combine(_tempDir, name),
            AudioPath = wav,
            AudioCodec = WemCodec.Vorbis
        };

        await builder.BuildPsarcAsync(CreateProject(), Options("a_p.psarc"),
            cancellationToken: TestContext.Current.CancellationToken);

        var cached = Path.Combine(_tempDir, "guitar.vorbis.wem");
        Assert.True(File.Exists(cached), "the encoded wem should be cached next to the source audio");
        var firstWrite = File.GetLastWriteTimeUtc(cached);

        await builder.BuildPsarcAsync(CreateProject(), Options("b_p.psarc"),
            cancellationToken: TestContext.Current.CancellationToken);

        // Re-encoding on every save would make psarc-on-save unusable for long songs.
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(cached));
    }

    [Fact]
    public void A_user_placed_wem_in_the_wwise_cache_slot_keeps_its_own_codec()
    {
        // The Wwise branch parks its output at <stem>.wem — an ordinary name any
        // tool may have produced. Declaring Vorbis over PCM data is the documented
        // "loads, scrolls, plays silence, no error anywhere" failure.
        var source = Path.Combine(_tempDir, "song.ogg");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        var cache = Path.Combine(_tempDir, "song.wem");
        WemWriter.WriteFile(cache, WemCodec.Pcm, new short[512], channels: 2, sampleRate: 48000);
        File.SetLastWriteTimeUtc(cache, File.GetLastWriteTimeUtc(source).AddMinutes(1));

        var resolved = RocksmithPackageBuilder.TryReuseCachedWem(cache, source);

        Assert.NotNull(resolved);
        Assert.Equal(WemCodec.Pcm, resolved.Value.Codec);
    }

    [Fact]
    public void An_unrecognisable_cached_wem_is_a_cache_miss()
    {
        var source = Path.Combine(_tempDir, "song2.ogg");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        var cache = Path.Combine(_tempDir, "song2.wem");
        File.WriteAllBytes(cache, "definitely not a RIFF/WAVE container at all"u8.ToArray());
        File.SetLastWriteTimeUtc(cache, File.GetLastWriteTimeUtc(source).AddMinutes(1));

        // A miss re-encodes; it must never become "assume Vorbis and hope".
        Assert.Null(RocksmithPackageBuilder.TryReuseCachedWem(cache, source));
    }
}
