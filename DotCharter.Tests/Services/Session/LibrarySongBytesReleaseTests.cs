using System.Runtime.InteropServices;
using DotCharter.Tone.Audio;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Session;
using DotCharter.Tests.Services.Parsing.Psarc;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using JuceSharp;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     #411: opening a library song hands the engine the cached WAV's BYTES
///     (<c>LoadAudioFile(preferMemoryPlayback: true)</c>) so the engine never holds
///     the file open. Those bytes were a managed <c>byte[]</c> read with
///     <c>File.ReadAllBytesAsync</c> — ~50 MB of large-object heap per five-minute
///     song, which the GC returns to the OS only much later — and the playback
///     service copied them once more before the native open. They now live in a
///     <see cref="NativeFileBuffer" /> that is freed the moment the engine's open
///     returns. These pin the three halves: the engine gets the real bytes, they are
///     not a managed array, and they are released on success AND on failure.
/// </summary>
public sealed class LibrarySongBytesReleaseTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("dotcharter-songbytes-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string WriteSong()
    {
        var wav = Path.Combine(_tempDir, "song.wav");
        var bytes = new byte[256 * 1024];
        new Random(411).NextBytes(bytes);
        File.WriteAllBytes(wav, bytes);
        return wav;
    }

    [Fact]
    public async Task MemoryPlaybackLoad_HandsTheEngineNativeBytes_AndReleasesThemOnceTheOpenReturns()
    {
        var wav = WriteSong();
        var audio = new FakeAudioPlaybackService();
        var session = NewSession(audio);

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        var load = Assert.Single(audio.MemoryLoads);
        Assert.Equal("wav", load.FormatHint);
        Assert.Equal(await File.ReadAllBytesAsync(wav, TestContext.Current.CancellationToken), load.Content);
        Assert.False(MemoryMarshal.TryGetArray(load.Bytes, out _),
            "the song's bytes were handed over as a managed array — a whole-file byte[] on the large "
            + "object heap per song opened, which is what #411 removed.");
        Assert.Throws<ObjectDisposedException>(() => load.Bytes.Span.Length);
        // The waveform pass reads the FILE, not a second managed copy.
        Assert.Contains(wav, audio.WaveformRequests);
        Assert.Empty(audio.LoadedPaths);
    }

    [Fact]
    public async Task MemoryPlaybackLoad_ReleasesTheBytes_WhenTheEngineOpenFails()
    {
        var wav = WriteSong();
        var audio = new FakeAudioPlaybackService { MemoryLoadFailure = new IOException("undecodable") };
        var session = NewSession(audio);

        await Assert.ThrowsAsync<IOException>(
            () => session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true));

        var load = Assert.Single(audio.MemoryLoads);
        Assert.Throws<ObjectDisposedException>(() => load.Bytes.Span.Length);
    }

    [Fact]
    public async Task ProjectSongLoad_OpensByPath_WithNoBlobAtAll()
    {
        var wav = WriteSong();
        var audio = new FakeAudioPlaybackService();
        var session = NewSession(audio);

        await session.LoadAudioFile(wav, generateBeats: false);

        Assert.Empty(audio.MemoryLoads);
        Assert.Equal(new[] { wav }, audio.LoadedPaths);
    }

    // ---- #435: the song converted to the device rate, in place ------------------

    private string CacheRoot => Path.Combine(_tempDir, "cache");

    /// <summary>A song inside a (temp) PSARC cache folder with its .wem, an hour old.</summary>
    private string WriteCachedSong(byte[] wavBytes)
    {
        var folder = Path.Combine(CacheRoot, "Song_ABCDEF0123456789_1");
        Directory.CreateDirectory(folder);
        var wav = Path.Combine(folder, "123456.wav");
        File.WriteAllBytes(wav, wavBytes);
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow.AddHours(-1));
        File.WriteAllBytes(Path.ChangeExtension(wav, ".wem"), [1, 2, 3]);
        return wav;
    }

    private sealed class DeviceRateCalls
    {
        public List<(string Source, int Rate)> Conversions { get; } = [];
        public List<string> Rederived { get; } = [];
        public TaskCompletionSource Converted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The real class over a fake converter (writes a device-rate file) and a
    /// fake re-deriver (writes the 44.1 kHz original back).</summary>
    private LibraryDeviceRateAudio DeviceRateAudio(DeviceRateCalls calls)
        => new(new LibraryAudioCacheStore(rootOverride: CacheRoot), null,
            (source, destination, rate) =>
            {
                lock (calls) calls.Conversions.Add((source, rate));
                File.WriteAllBytes(destination, DeviceRateWavFixture.DeviceRate(rate));
                calls.Converted.TrySetResult();
                return JuceDeviceRateConversion.Pcm16;
            },
            (wem, wav) =>
            {
                lock (calls) calls.Rederived.Add(wav);
                File.WriteAllBytes(wav, DeviceRateWavFixture.Plain(44_100));
                return true;
            });

    [Fact]
    public async Task AFirstOpen_ReadsTheOriginal_ThenConvertsTheSongInPlace()
    {
        var original = DeviceRateWavFixture.Plain(44_100);
        var wav = WriteCachedSong(original);
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48_000 };
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        Assert.Equal(original, Assert.Single(audio.MemoryLoads).Content);
        Assert.Equal(wav, Assert.Single(audio.MemoryLoadDisplayPaths));
        await calls.Converted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal((wav, 48_000), Assert.Single(calls.Conversions));
        Assert.Empty(calls.Rederived);
    }

    /// <summary>The conversion REPLACES the file, which fails (or worse) while the
    /// native waveform pass still has it open: it starts only once that pass is done.</summary>
    [Fact]
    public async Task TheConversion_StartsOnlyAfterTheWaveformPassStoppedReadingTheFile()
    {
        var wav = WriteCachedSong(DeviceRateWavFixture.Plain(44_100));
        var waveformDone = false;
        var audio = new FakeAudioPlaybackService
        {
            ActiveSampleRate = 48_000,
            WaveformPass = async () =>
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
                Volatile.Write(ref waveformDone, true);
            },
        };
        bool? waveformDoneAtConversion = null;
        var converted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceRateAudio = new LibraryDeviceRateAudio(new LibraryAudioCacheStore(rootOverride: CacheRoot), null,
            (source, destination, rate) =>
            {
                waveformDoneAtConversion = Volatile.Read(ref waveformDone);
                File.WriteAllBytes(destination, DeviceRateWavFixture.DeviceRate(rate));
                converted.TrySetResult();
                return JuceDeviceRateConversion.Pcm16;
            });
        var session = NewSession(audio, deviceRateAudio);

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        await converted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(waveformDoneAtConversion, "the conversion started while the waveform pass was still reading the song");
    }

    [Fact]
    public async Task ARepeatOpen_ReadsTheDeviceRateSong_AsItIs()
    {
        var atRate = DeviceRateWavFixture.DeviceRate(48_000);
        var wav = WriteCachedSong(atRate);
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48_000 };
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        Assert.Equal(atRate, Assert.Single(audio.MemoryLoads).Content);
        Assert.Contains(wav, audio.WaveformRequests);
        Assert.Empty(calls.Conversions);
        Assert.Empty(calls.Rederived);
    }

    /// <summary>The device now runs at another rate than the song was stored for: the
    /// song is re-derived from its .wem BEFORE its bytes are read, so the engine
    /// converts at load as it always did (never the transport's linear resampler on a
    /// file made for another rate), and then it is converted for the new rate.</summary>
    [Fact]
    public async Task ASongStoredForAnotherRate_IsRederivedBeforeTheOpen_AndConvertedForTheNewOne()
    {
        var wav = WriteCachedSong(DeviceRateWavFixture.DeviceRate(48_000));
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 96_000 };
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        Assert.Equal(new[] { wav }, calls.Rederived);
        Assert.Equal(DeviceRateWavFixture.Plain(44_100), Assert.Single(audio.MemoryLoads).Content);
        await calls.Converted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal((wav, 96_000), Assert.Single(calls.Conversions));
    }

    /// <summary>The device changed rate between the pick and the open: the song,
    /// stored for the OLD rate, is re-derived and re-opened.</summary>
    [Fact]
    public async Task ADeviceThatChangesRateDuringTheOpen_GetsTheSongRederivedAndReopened()
    {
        var atRate = DeviceRateWavFixture.DeviceRate(48_000);
        var wav = WriteCachedSong(atRate);
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48_000 };
        audio.DuringMemoryLoad = () => audio.ActiveSampleRate = 44_100;
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        Assert.Equal(2, audio.MemoryLoads.Count);
        Assert.Equal(atRate, audio.MemoryLoads[0].Content);
        Assert.Equal(DeviceRateWavFixture.Plain(44_100), audio.MemoryLoads[1].Content);
        Assert.All(audio.MemoryLoadDisplayPaths, p => Assert.Equal(wav, p));
        Assert.Equal(new[] { wav }, calls.Rederived);
        Assert.Empty(calls.Conversions);   // 44.1 kHz on a 44.1 kHz device: nothing to convert
    }

    /// <summary>The engine refuses a device-rate file it cannot trust (DeviceRateWav.h):
    /// the song is re-derived and opened again, so the user hears it converted at load
    /// instead of an error.</summary>
    [Fact]
    public async Task ADeviceRateSongTheEngineRefuses_IsRederivedAndOpenedAgain()
    {
        var wav = WriteCachedSong(DeviceRateWavFixture.DeviceRate(48_000));
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48_000 };
        var refused = new IOException("source_open_memory failed with Decode.");
        audio.MemoryLoadFailure = refused;
        audio.DuringMemoryLoad = () => audio.MemoryLoadFailure = audio.MemoryLoads.Count == 1 ? refused : null;
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true);

        Assert.Equal(2, audio.MemoryLoads.Count);
        Assert.Equal(DeviceRateWavFixture.Plain(44_100), audio.MemoryLoads[1].Content);
        Assert.All(audio.MemoryLoads, l => Assert.Throws<ObjectDisposedException>(() => l.Bytes.Span.Length));
        Assert.Equal(new[] { wav }, calls.Rederived);
        // ...and the re-derived original is converted again, in the background.
        await calls.Converted.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal((wav, 48_000), Assert.Single(calls.Conversions));
    }

    [Fact]
    public async Task AnOriginalTheEngineRefuses_StillFailsTheOpen()
    {
        var wav = WriteCachedSong(DeviceRateWavFixture.Plain(44_100));
        var audio = new FakeAudioPlaybackService
        {
            ActiveSampleRate = 48_000, MemoryLoadFailure = new IOException("undecodable"),
        };
        var calls = new DeviceRateCalls();
        var session = NewSession(audio, DeviceRateAudio(calls));

        await Assert.ThrowsAsync<IOException>(
            () => session.LoadAudioFile(wav, generateBeats: false, preferMemoryPlayback: true));
        Assert.Single(audio.MemoryLoads);
        Assert.Empty(calls.Rederived);
    }

    private static ProjectSessionService NewSession(FakeAudioPlaybackService audio,
        LibraryDeviceRateAudio? deviceRateAudio = null)
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var selection = new SelectionService();
        var history = new HistoryService(logger);
        var editor = new ChartEditorViewModel(
            state, transport, history, selection,
            new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

        return new ProjectSessionService(
            audio, config, logger, new FakeFileStorageService(),
            new NoDocuments(), state, transport, editor,
            new TempoService(logger),
            new NoRecovery(), history,
            new NoCompanions(), statusBar: new RecordingStatusBarService(),
            uiDispatcher: new TestUiDispatcher(),
            localization: FakeLocalization.English(),
            deviceRateAudio: deviceRateAudio);
    }

    private sealed class NoDocuments : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();
        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }
        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project,
            double? audioLengthMs = null)
        { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName)
            => new() { AudioFileName = audioFileName };
    }

    private sealed class NoRecovery : ICrashRecoveryService
    {
        public string SnapshotRootDirectory => string.Empty;
        public void Start() { }
        public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => [];
        public void DiscardSession(RecoverableSession session) { }
        public void NotifyProjectSaved(string projectPath) { }
        public void NotifyProjectSwitched() { }
        public void NotifyCleanShutdown() { }
        public Func<bool>? HasUnsavedChanges { get; set; }
        public void NotifyProjectChanged() { }
        public void Dispose() { }
    }

    private sealed class NoCompanions : ISaveCompanionService
    {
        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(
            string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot,
            double? audioLengthMs, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
