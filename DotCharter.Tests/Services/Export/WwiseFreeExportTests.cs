using System.Reflection;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="WwiseFreeExportTests" /> that need no package reader; its fixtures are in <c>WwiseFreeExportTests.Fixtures.cs</c>.</summary>
public partial class WwiseFreeExportTests
{
    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public async Task An_old_generated_wem_is_rebuilt_but_a_supplied_wem_is_untouched(WemCodec codec)
    {
        var wav = Path.Combine(_tempDir, "old.wav");
        PcmAudioLoader.WriteWav(wav, new PcmAudio(Enumerable.Repeat((short)10000, 48000).ToArray(), 1, 48000));
        var suffix = codec == WemCodec.Pcm ? "pcm" : "vorbis";
        var oldCache = $"{wav}.{suffix}.wem";
        WemWriter.WriteFile(oldCache, codec, Enumerable.Repeat((short)7071, 48000 * 2).ToArray(), 2, 48000);
        File.SetLastWriteTimeUtc(oldCache, File.GetLastWriteTimeUtc(wav).AddMinutes(1));
        var bytes = File.ReadAllBytes(oldCache);
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());

        Assert.NotEqual(oldCache, CachePath(wav, codec));
        await BoundedWait.ForAsync(builder.BuildPsarcAsync(CreateProject(), new RocksmithBuildOptions
        {
            AudioPath = wav, AudioCodec = codec, OutputPath = Path.Combine(_tempDir, "rebuilt_p.psarc")
        }, cancellationToken: TestContext.Current.CancellationToken), "the rebuilt mono package", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(CachePath(wav, codec)));
        Assert.Equal(bytes, File.ReadAllBytes(oldCache));
        Assert.NotEqual(bytes, File.ReadAllBytes(CachePath(wav, codec)));
        var firstWrite = File.GetLastWriteTimeUtc(CachePath(wav, codec));
        await BoundedWait.ForAsync(builder.BuildPsarcAsync(CreateProject(), new RocksmithBuildOptions
        {
            AudioPath = wav, AudioCodec = codec, OutputPath = Path.Combine(_tempDir, "again_p.psarc")
        }, cancellationToken: TestContext.Current.CancellationToken), "the repeated mono package", TestContext.Current.CancellationToken);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(CachePath(wav, codec)));

        await BoundedWait.ForAsync(builder.BuildPsarcAsync(CreateProject(), new RocksmithBuildOptions
        {
            AudioPath = oldCache, AudioCodec = codec, OutputPath = Path.Combine(_tempDir, "supplied_p.psarc")
        }, cancellationToken: TestContext.Current.CancellationToken), "the supplied WEM package", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, File.ReadAllBytes(oldCache));
    }

    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public void Known_stereo_sources_use_the_stable_managed_cache_slot(WemCodec codec)
    {
        var wav = WriteWav(Path.Combine(_tempDir, "stereo.wav"), seconds: 1);
        Assert.Equal(ManagedAudioCache.WemPath(wav), CachePath(wav, codec));
    }

    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public void Wem_cache_identity_includes_the_source_extension_and_directory(WemCodec codec)
    {
        var wav = Path.Combine(_tempDir, "song.wav");
        var flac = Path.Combine(_tempDir, "song.flac");
        var other = Path.Combine(_tempDir, "other", "song.wav");

        var wavCache = CachePath(wav, codec);
        var flacCache = CachePath(flac, codec);
        var otherCache = CachePath(other, codec);

        Assert.NotEqual(wavCache, flacCache);
        Assert.NotEqual(wavCache, otherCache);
        Assert.Equal(wavCache, CachePath(Path.Combine(_tempDir, ".", "song.wav"), codec));
        Assert.Equal(wavCache, CachePath(wav, codec == WemCodec.Pcm ? WemCodec.Vorbis : WemCodec.Pcm));
    }

    private static string CachePath(string audioPath, WemCodec codec)
    {
        // Exercise the existing private decision so the regression also compiles against the old exporter.
        var method = typeof(RocksmithPackageBuilder).GetMethod("ManagedWemCachePath", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [audioPath, codec, null]));
    }

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

        var cached = CachePath(wav, WemCodec.Vorbis);
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
