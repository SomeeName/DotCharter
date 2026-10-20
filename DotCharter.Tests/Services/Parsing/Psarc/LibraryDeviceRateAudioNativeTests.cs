using System.Runtime.InteropServices;
using DotCharter.Services.Parsing.Audio;
using DotCharter.Services.Library.Cache;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     #435 end to end through the real DLL, on the native Null device (never the
///     machine's output): a song in a (temp) PSARC cache folder is converted to the
///     device rate by <see cref="LibraryDeviceRateAudio" /> with the real converter, the
///     result REPLACES it, and the replaced file -- opened the way the app opens a
///     library song (bytes, hint "wav") -- holds the SAME samples, step, frame count,
///     rate and length as the load-time conversion of the original, so sync cannot
///     move. Both a song inside full scale and one whose upsample overshoots it (the
///     case every real song measured hits) are 16-bit. A device at another rate gets
///     the original back, re-derived from the real <c>.wem</c>. Skips when
///     <c>juce_sharp.dll</c> is not loadable.
/// </summary>
[Collection("NativeEngine")]
public sealed class LibraryDeviceRateAudioNativeTests : IDisposable
{
    private const int DeviceRate = 48_000;

    private static readonly string WemFixture =
        Path.Combine(AppContext.BaseDirectory, "Services", "Parsing", "Fixtures", "tone-stereo48k-inline-mod.wem");

    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), "dc-devrate-native-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_sandbox);

    private static bool NativeLoadable()
        => NativeLibrary.TryLoad("juce_sharp", typeof(JuceAudioEngine).Assembly, null, out _);

    private static JuceAudioEngine NullEngine(int rate)
    {
        var engine = new JuceAudioEngine(new JuceEngineConfig
        {
            EnableNullDevice = true, OutputTypeName = "Null", SampleRate = rate, BufferFrames = 480,
        });
        Assert.Equal(rate, engine.SampleRate);
        return engine;
    }

    private string SongFolder()
    {
        var folder = Path.Combine(_sandbox, "cache", "Song_ABCDEF0123456789_1");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private LibraryDeviceRateAudio NewAudio() => new(new LibraryAudioCacheStore(rootOverride: Path.Combine(_sandbox, "cache")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_replaced_song_plays_bit_identically_to_the_load_time_conversion(bool overshoots)
    {
        Assert.SkipUnless(NativeLoadable(), "juce_sharp.dll is not loadable here");
        var wav = Path.Combine(SongFolder(), "123456.wav");
        var original = SongWav(44_100, 6, overshoots);
        await File.WriteAllBytesAsync(wav, original, TestContext.Current.CancellationToken);
        // A cached song's .wem sits beside it; only such a song is converted (#708).
        await File.WriteAllBytesAsync(Path.ChangeExtension(wav, ".wem"), [1, 2, 3], TestContext.Current.CancellationToken);

        await NewAudio().ScheduleConversion(wav, DeviceRate);
        var replaced = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        Assert.NotEqual(original, replaced);
        Assert.Equal(new[] { "123456.wav", "123456.wem" },
            Directory.GetFiles(Path.GetDirectoryName(wav)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        using var engine = NullEngine(DeviceRate);
        var (atLoad, atLoadLength) = Open(engine, original);
        var (fromFile, fileLength) = Open(engine, replaced);

        Assert.True(atLoad.ConvertedAtLoad, "the 44.1 kHz original was not converted at load; the test proves nothing");
        Assert.False(fromFile.ConvertedAtLoad, "the device-rate file was converted AGAIN at load");
        Assert.Equal(atLoad with { ConvertedAtLoad = false }, fromFile);
        Assert.Equal(atLoadLength, fileLength);
        if (overshoots) Assert.True(atLoad.Step > 1f / 32768f, $"the square should overshoot full scale; step {atLoad.Step}");
        else Assert.Equal(1f / 32768f, atLoad.Step);

        // 16 bits a sample whatever the step: the 76-byte header and frames x 2 x 2.
        Assert.Equal(76 + atLoad.Frames * 4, replaced.LongLength);
    }

    /// <summary>
    /// The whole cycle on a real <c>.wem</c>: decoded to the original WAV, converted for
    /// a 96 kHz device and replaced, then -- the device back at 48 kHz -- re-derived from
    /// the <c>.wem</c> into exactly the bytes the first decode wrote.
    /// </summary>
    [Fact]
    public async Task A_device_at_another_rate_gets_the_original_rederived_from_the_wem()
    {
        Assert.SkipUnless(NativeLoadable(), "juce_sharp.dll is not loadable here");
        var folder = SongFolder();
        var wem = Path.Combine(folder, "123456.wem");
        var wav = Path.ChangeExtension(wem, ".wav");
        File.Copy(WemFixture, wem);
        Assert.True(AudioFormatConverter.WriteNativeWave(wem, wav, TestContext.Current.CancellationToken));
        var decoded = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);

        var audio = NewAudio();
        JucePreloadDigest atLoad;
        using (var engine = NullEngine(96_000))
            atLoad = Open(engine, decoded).Digest;
        await audio.ScheduleConversion(wav, 96_000);
        var at96 = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        Assert.NotEqual(decoded, at96);
        using (var engine = NullEngine(96_000))
            Assert.Equal(atLoad with { ConvertedAtLoad = false }, Open(engine, at96).Digest);

        Assert.False(audio.PrepareForOpen(wav, 96_000));
        Assert.True(audio.PrepareForOpen(wav, DeviceRate));
        Assert.Equal(decoded, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "123456.wav", "123456.wem" },
            Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    private static (JucePreloadDigest Digest, TimeSpan Length) Open(JuceAudioEngine engine, byte[] bytes)
    {
        using var source = engine.OpenMemory(bytes, "wav");
        return (source.GetPreloadDigest(), source.Length);
    }

    /// <summary>Stereo 16-bit. A few partials and noise well inside full scale, or a
    /// full-scale square whose upsample overshoots it (as loud masters do).</summary>
    private static byte[] SongWav(int rate, int seconds, bool overshoots)
    {
        var frames = rate * seconds;
        var rng = new Random(435);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4);
        w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            short s;
            if (overshoots)
            {
                s = i / 50 % 2 == 0 ? short.MaxValue : short.MinValue;
            }
            else
            {
                var t = (double)i / rate;
                var x = 0.4 * Math.Sin(2 * Math.PI * 110 * t) + 0.2 * Math.Sin(2 * Math.PI * 2_500 * t)
                        + 0.05 * (rng.NextDouble() * 2 - 1);
                s = (short)Math.Round(x * 32767);
            }

            w.Write(s);
            w.Write((short)(s / 2));
        }

        w.Flush();
        return ms.ToArray();
    }
}
