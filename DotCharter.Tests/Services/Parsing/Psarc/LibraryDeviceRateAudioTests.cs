using DotCharter.Services.Library.Cache;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Parsing.Psarc;

/// <summary>
///     #435: each cached song converted ONCE to the device's rate, in place. These pin
///     the managed half against a fake converter and a fake re-deriver: when a song is
///     converted, that the result REPLACES the original (one file per song), that
///     nothing half-written, wrong or stale ever takes its place, and that a song
///     stored for another rate -- or damaged -- is re-derived from its <c>.wem</c>
///     before an open. The native half (the file plays bit-identical samples) is
///     <see cref="LibraryDeviceRateAudioNativeTests" /> and JuceSharp's <c>AbiV43Tests</c>.
///     A private temp root, never the owner's cache.
/// </summary>
public sealed class LibraryDeviceRateAudioTests : IDisposable
{
    private readonly string _sandbox =
        Path.Combine(Path.GetTempPath(), "dc-devrate-" + Guid.NewGuid().ToString("N"));

    private readonly List<(string Source, string Destination, int Rate)> _conversions = [];
    private readonly List<(string Wem, string Wav)> _rederivations = [];

    public LibraryDeviceRateAudioTests() => Directory.CreateDirectory(Root);

    public void Dispose() => BestEffortDelete.Directory(_sandbox);

    private string Root => Path.Combine(_sandbox, "cache");

    private LibraryAudioCacheStore Store() => new(rootOverride: Root);

    /// <summary>A converter that writes a device-rate file at the destination it is given.</summary>
    private JuceDeviceRateConversion WriteDeviceRate(string source, string destination, int rate)
    {
        lock (_conversions) _conversions.Add((source, destination, rate));
        File.WriteAllBytes(destination, DeviceRateWavFixture.DeviceRate(rate, frames: 300));
        return JuceDeviceRateConversion.Pcm16;
    }

    /// <summary>A re-deriver that writes the 44.1 kHz original back, as the .wem decode does.</summary>
    private bool WriteOriginal(string wem, string wav)
    {
        lock (_rederivations) _rederivations.Add((wem, wav));
        File.WriteAllBytes(wav, DeviceRateWavFixture.Plain(44_100));
        return true;
    }

    private LibraryDeviceRateAudio NewAudio(LibraryDeviceRateAudio.DeviceRateConverter? convert = null,
        Func<bool>? originalsReplaceable = null)
        => new(Store(), null, convert ?? WriteDeviceRate, WriteOriginal, originalsReplaceable);

    /// <summary>A song in a cache folder: <c>123456.wav</c> (+ its <c>.wem</c>), ten minutes old.</summary>
    private string CachedSong(byte[]? wavBytes = null, bool withWem = true)
    {
        var dir = Path.Combine(Root, "Song_ABCDEF0123456789_1");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "123456.wav");
        File.WriteAllBytes(wav, wavBytes ?? DeviceRateWavFixture.Plain(44_100));
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow.AddMinutes(-10));
        if (withWem) File.WriteAllBytes(Path.ChangeExtension(wav, ".wem"), [1, 2, 3]);
        return wav;
    }

    private static string[] FolderFiles(string wav)
        => Directory.GetFiles(Path.GetDirectoryName(wav)!).Select(f => Path.GetFileName(f)!).Order().ToArray();

    // ---- the background conversion -------------------------------------------

    [Fact]
    public async Task An_original_below_the_device_rate_is_converted_and_REPLACED()
    {
        var wav = CachedSong();
        await NewAudio().ScheduleConversion(wav, 48_000);

        var call = Assert.Single(_conversions);
        Assert.Equal((wav, 48_000), (call.Source, call.Rate));
        // Written under a temporary name in the song's folder, then renamed over the
        // original: one file per song, and an open sees one whole file or the other.
        Assert.NotEqual(wav, call.Destination);
        Assert.Equal(Path.GetDirectoryName(wav), Path.GetDirectoryName(call.Destination));
        Assert.Equal(DeviceRateWavFixture.DeviceRate(48_000, frames: 300), await File.ReadAllBytesAsync(wav,
            TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
    }

    /// <summary>A song with no <c>.wem</c> beside it (a <c>.dcsong</c>'s decoded Opus, #708)
    /// could not be re-derived for another rate, so it is never replaced.</summary>
    [Fact]
    public async Task A_song_without_its_wem_is_never_converted()
    {
        var original = DeviceRateWavFixture.Plain(44_100);
        var wav = CachedSong(original, withWem: false);

        await NewAudio().ScheduleConversion(wav, 48_000);

        Assert.Empty(_conversions);
        Assert.Equal(original, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_song_already_at_the_device_rate_or_above_is_left_alone()
    {
        var atRate = CachedSong(DeviceRateWavFixture.DeviceRate(48_000));
        var audio = NewAudio();
        await audio.ScheduleConversion(atRate, 48_000);
        await audio.ScheduleConversion(atRate, 44_100);   // a device-rate file is never converted again

        File.WriteAllBytes(atRate, DeviceRateWavFixture.Plain(48_000));
        await audio.ScheduleConversion(atRate, 48_000);
        await audio.ScheduleConversion(atRate, 44_100);   // the engine never downsamples at load

        Assert.Empty(_conversions);
    }

    [Fact]
    public async Task A_converter_result_that_is_not_a_device_rate_file_at_the_rate_is_discarded()
    {
        var wav = CachedSong();
        var original = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        foreach (var wrong in new[]
                 {
                     DeviceRateWavFixture.Plain(48_000), DeviceRateWavFixture.DeviceRate(96_000),
                     DeviceRateWavFixture.Truncated(48_000),
                 })
        {
            await NewAudio((s, d, r) =>
            {
                File.WriteAllBytes(d, wrong);
                return JuceDeviceRateConversion.Pcm16;
            }).ScheduleConversion(wav, 48_000);

            Assert.Equal(original, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
        }
    }

    [Fact]
    public async Task Nothing_changes_when_the_engine_would_not_convert()
    {
        var wav = CachedSong();
        var original = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        await NewAudio((s, d, r) =>
        {
            lock (_conversions) _conversions.Add((s, d, r));
            return JuceDeviceRateConversion.NotNeeded;
        }).ScheduleConversion(wav, 48_000);

        Assert.Single(_conversions);
        Assert.Equal(original, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
    }

    [Fact]
    public async Task A_failed_conversion_leaves_the_original_and_no_staging_file()
    {
        var wav = CachedSong();
        var original = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        await NewAudio((s, d, r) =>
        {
            File.WriteAllText(d, "half");
            throw new IOException("convert_audio_for_device failed with Decode.");
        }).ScheduleConversion(wav, 48_000);

        Assert.Equal(original, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
    }

    [Fact]
    public async Task A_conversion_made_while_the_original_changed_is_discarded()
    {
        var wav = CachedSong();
        var reExtracted = DeviceRateWavFixture.Plain(44_100, frames: 999);
        await NewAudio((s, d, r) =>
        {
            File.WriteAllBytes(d, DeviceRateWavFixture.DeviceRate(r));
            File.WriteAllBytes(s, reExtracted);   // a re-extraction lands mid-conversion
            return JuceDeviceRateConversion.Pcm16;
        }).ScheduleConversion(wav, 48_000);

        Assert.Equal(reExtracted, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
    }

    [Fact]
    public async Task Files_outside_the_cache_and_a_missing_engine_get_nothing()
    {
        var own = Path.Combine(_sandbox, "my-song.wav");
        File.WriteAllBytes(own, DeviceRateWavFixture.Plain(44_100));
        var loose = Path.Combine(Root, "loose.wav");
        File.WriteAllBytes(loose, DeviceRateWavFixture.Plain(44_100));
        var wav = CachedSong();
        var audio = NewAudio();

        await audio.ScheduleConversion(own, 48_000);
        await audio.ScheduleConversion(loose, 48_000);
        await audio.ScheduleConversion(wav, 0);
        Assert.Empty(_conversions);
        Assert.False(audio.PrepareForOpen(own, 48_000));
    }

    [Fact]
    public async Task Two_opens_at_once_convert_once()
    {
        var wav = CachedSong();
        using var gate = new ManualResetEventSlim();
        var audio = NewAudio((s, d, r) =>
        {
            gate.Wait(TestContext.Current.CancellationToken);
            return WriteDeviceRate(s, d, r);
        });
        var first = audio.ScheduleConversion(wav, 48_000);
        var second = audio.ScheduleConversion(wav, 48_000);
        gate.Set();
        await Task.WhenAll(first, second);
        Assert.Single(_conversions);
    }

    /// <summary>The stem cache's one-off WEM-key adoption proves a pre-#426 archive by
    /// the ORIGINAL WAV's exact bytes, and stems are never regenerated: no original is
    /// replaced until that pass is done.</summary>
    [Fact]
    public async Task No_original_is_replaced_while_the_stem_cache_still_needs_it()
    {
        var wav = CachedSong();
        var original = await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken);
        var replaceable = false;
        var audio = NewAudio(originalsReplaceable: () => replaceable);

        await audio.ScheduleConversion(wav, 48_000);
        Assert.Empty(_conversions);
        Assert.Equal(original, await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken));

        replaceable = true;
        await audio.ScheduleConversion(wav, 48_000);
        Assert.Single(_conversions);
    }

    /// <summary>The song is still ONE file in its folder, which the store's folder-level
    /// verbs measure and evict whole: the cache holds the device-rate file only, never
    /// a second copy beside the original.</summary>
    [Fact]
    public async Task The_cache_holds_one_file_per_song_and_evicts_it_with_its_folder()
    {
        var wav = CachedSong();
        await NewAudio().ScheduleConversion(wav, 48_000);
        var store = Store();

        Assert.Equal(new[] { "123456.wav", "123456.wem" }, FolderFiles(wav));
        Assert.Equal(new FileInfo(wav).Length + new FileInfo(Path.ChangeExtension(wav, ".wem")).Length,
            store.MeasureUsage().Bytes);
        Assert.Equal(1, store.Clear());
        Assert.False(Directory.Exists(Path.GetDirectoryName(wav)));
    }

    // ---- before an open -------------------------------------------------------

    [Fact]
    public void A_song_stored_for_another_rate_is_rederived_from_its_wem_before_the_open()
    {
        var wav = CachedSong(DeviceRateWavFixture.DeviceRate(48_000));
        var audio = NewAudio();

        Assert.True(audio.PrepareForOpen(wav, 44_100));
        Assert.Equal((Path.ChangeExtension(wav, ".wem"), wav), Assert.Single(_rederivations));
        Assert.Equal(DeviceRateWavFixture.Plain(44_100), File.ReadAllBytes(wav));

        // ...and once it is the original again, there is nothing more to do.
        Assert.False(audio.PrepareForOpen(wav, 44_100));
        Assert.Single(_rederivations);
    }

    [Fact]
    public void A_damaged_device_rate_file_is_rederived_before_the_open()
    {
        var wav = CachedSong(DeviceRateWavFixture.Truncated(48_000));
        Assert.True(NewAudio().PrepareForOpen(wav, 48_000));
        Assert.Single(_rederivations);
    }

    [Fact]
    public void The_original_and_a_file_at_the_devices_own_rate_are_opened_as_they_are()
    {
        var audio = NewAudio();
        var wav = CachedSong();
        Assert.False(audio.PrepareForOpen(wav, 48_000));
        File.WriteAllBytes(wav, DeviceRateWavFixture.DeviceRate(48_000));
        Assert.False(audio.PrepareForOpen(wav, 48_000));
        Assert.False(audio.PrepareForOpen(wav, 0));
        Assert.Empty(_rederivations);
    }

    [Fact]
    public void Without_its_wem_a_song_is_left_alone()
    {
        var bytes = DeviceRateWavFixture.DeviceRate(48_000);
        var wav = CachedSong(bytes, withWem: false);
        Assert.False(NewAudio().PrepareForOpen(wav, 44_100));
        Assert.Empty(_rederivations);
        Assert.Equal(bytes, File.ReadAllBytes(wav));
    }

    [Fact]
    public void A_failed_open_of_a_device_rate_file_is_rederived_and_of_an_original_is_not()
    {
        var audio = NewAudio();
        var wav = CachedSong();
        Assert.False(audio.RecoverAfterFailedOpen(wav));
        Assert.Empty(_rederivations);

        File.WriteAllBytes(wav, DeviceRateWavFixture.DeviceRate(48_000));
        Assert.True(audio.RecoverAfterFailedOpen(wav));
        Assert.Single(_rederivations);
    }

    // ---- the header -----------------------------------------------------------

    [Fact]
    public void The_header_reader_tells_plain_device_rate_and_damaged_files_apart()
    {
        static DeviceRateWavHeader Read(byte[] b) => DeviceRateWavHeader.Read(new MemoryStream(b));

        Assert.Equal((DeviceRateWavKind.Plain, 44_100), (Read(DeviceRateWavFixture.Plain(44_100)).Kind,
            Read(DeviceRateWavFixture.Plain(44_100)).SampleRate));
        var valid = Read(DeviceRateWavFixture.DeviceRate(48_000, step: 1.2f / 32768f));
        Assert.Equal((DeviceRateWavKind.DeviceRate, 48_000, 1.2f / 32768f), (valid.Kind, valid.SampleRate, valid.Step));
        Assert.Equal(DeviceRateWavKind.Plain, Read("OggS not a wav"u8.ToArray()).Kind);

        Assert.Equal(DeviceRateWavKind.Damaged, Read(DeviceRateWavFixture.Truncated(48_000)).Kind);
        Assert.Equal(DeviceRateWavKind.Damaged, Read(DeviceRateWavFixture.DeviceRate(48_000, step: 0f)).Kind);
        Assert.Equal(DeviceRateWavKind.Damaged, Read(DeviceRateWavFixture.DeviceRate(48_000, step: 1.5f)).Kind);
        var wrongFrames = DeviceRateWavFixture.DeviceRate(48_000);
        BitConverter.TryWriteBytes(wrongFrames.AsSpan(52), 257UL);
        Assert.Equal(DeviceRateWavKind.Damaged, Read(wrongFrames).Kind);
        var wrongVersion = DeviceRateWavFixture.DeviceRate(48_000);
        BitConverter.TryWriteBytes(wrongVersion.AsSpan(44), 2u);
        Assert.Equal(DeviceRateWavKind.Damaged, Read(wrongVersion).Kind);
    }
}
