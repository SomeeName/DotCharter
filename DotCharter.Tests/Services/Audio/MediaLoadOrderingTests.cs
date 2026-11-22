using System.Reflection;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Engine;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using JuceSharp;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Audio;

/// <summary>Controls the existing request and swap seams to reorder actual media workers without clock assumptions.</summary>
[Collection("NativeEngine")]
public sealed class MediaLoadOrderingTests : IDisposable
{
    /// <summary>Declared first: armed before the engine below opens, and restored to
    /// what the run started with (#606).</summary>
    private readonly NullDeviceEnvScope _nullDevice = NullDeviceEnvScope.Enable();
    private readonly JuceAudioPlaybackService _audio = new(FakeLocalization.English());
    private readonly byte[] _wave;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-owner-" + Guid.NewGuid().ToString("N"));

    public MediaLoadOrderingTests()
    {
        typeof(JuceAudioPlaybackService).GetMethod("ReplaceEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_audio, [new JuceEngineConfig
            {
                PreferredDriver = AudioDriver.Wasapi, OutputTypeName = "Null", InputTypeName = "Null",
                SampleRate = 48_000, BufferFrames = 2048, OutputChannels = 2, InputChannels = 1
            }]);
        using var stream = new MemoryStream();
        using (var writer = new WavWriter(stream, 48_000, 1, leaveOpen: true)) writer.Write(new float[480]);
        _wave = stream.ToArray();
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "take.wav"), _wave);
    }

    [Fact]
    public void OlderExplicitWorker_CannotReplaceATemporarySourceThatPublishedFirst()
    {
        var song = Request("song-b.wav", false, out _);
        var take = Request("take.wav", true, out var previous);
        using var cancellation = new CancellationTokenSource();
        var workers = new Action[]
        {
            () => Swap(song, null, CancellationToken.None),
            () => Swap(take, previous, cancellation.Token)
        };

        workers[1]();
        workers[0]();

        Assert.Equal("take.wav", _audio.CurrentAudioPath);
        Assert.Same(take, Field<MediaLoadRequest>("_requestedMediaLoad"));
        Assert.Same(take, Field<MediaLoadRequest>("_loadedMediaLoad"));
        Assert.True(Field<JuceAudioSource>("_source").Length > TimeSpan.Zero);
    }

    [Fact]
    public void ANewExplicitRequestDuringDecode_RejectsTheOlderPublication()
    {
        var song = Request("song-b.wav", false, out _);
        MediaLoadRequest? newer = null;

        Swap(song, null, CancellationToken.None, () => newer = Request("song-c.wav", false, out _));

        Assert.Empty(_audio.CurrentAudioPath);
        Assert.NotNull(newer);
        Swap(newer, null, CancellationToken.None);
        Assert.Equal("song-c.wav", _audio.CurrentAudioPath);
        Assert.Same(newer, Field<MediaLoadRequest>("_loadedMediaLoad"));
        Assert.Same(newer, Field<MediaLoadRequest>("_requestedMediaLoad"));
    }

    [Fact]
    public void FailedOrdinaryDecode_ReportsFailureAndLeavesAnEmptyConsistentIdentity()
    {
        var song = Request("unreadable.wav", false, out _);

        var failure = Assert.Throws<TargetInvocationException>(() =>
            Swap(song, null, CancellationToken.None, () => throw new IOException("synthetic decode failure")));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.Empty(_audio.CurrentAudioPath);
        Assert.Null(Field<MediaLoadRequest>("_requestedMediaLoad").Identity);
        Assert.Same(Field<MediaLoadRequest>("_requestedMediaLoad"), Field<MediaLoadRequest>("_loadedMediaLoad"));
    }

    [Fact]
    public async Task HandBackWithoutAnOriginalMaster_PreservesTheSongsLoadedStems()
    {
        var path = Path.Combine(_dir, "take.wav");
        await _audio.LoadStemsAsync([new StemLoadRequest("drums", path)]);
        Assert.True(_audio.HasStems);
        var vm = new RunListenBackViewModel(_audio, new TakeStore(_dir), new ExportTargetWriter(), null,
            FakeLocalization.English(), new FakeAppLogger());
        vm.OnTakeSaved("take.wav", true);
        await vm.ListenBackToRunCommand.ExecuteAsync(null);
        Assert.True(vm.IsTakePlaying);

        await vm.StopAsync();

        Assert.Empty(_audio.CurrentAudioPath);
        Assert.True(_audio.HasStems);
        Assert.Contains("drums", _audio.LoadedStemNames);
    }

    [Fact]
    public async Task ANewerWorkerDuringHandBack_PreventsPublicationAndPostLoadSeek()
    {
        using var cancellation = new CancellationTokenSource();
        var take = Request("take.wav", true, out var previous);
        Swap(take, previous, cancellation.Token);
        var method = typeof(JuceAudioPlaybackService).GetMethod("TryRestoreAudioAsync");
        Assert.NotNull(method);
        Task<bool> restore;
        long seeks;
        lock (Field<object>("_mediaLoadGate"))
        {
            restore = (Task<bool>)method.Invoke(_audio,
                [Path.Combine(_dir, "take.wav"), "take.wav", take.Generation, 321d])!;
            seeks = _audio.SeekGeneration;
            var newer = Request("song-c.wav", false, out _);
            Swap(newer, null, CancellationToken.None);
        }
        Assert.False(await restore);
        Assert.Equal("song-c.wav", _audio.CurrentAudioPath);
        Assert.Equal(seeks, _audio.SeekGeneration);
        Assert.Same(Field<MediaLoadRequest>("_requestedMediaLoad"), Field<MediaLoadRequest>("_loadedMediaLoad"));
    }

    [Fact]
    public async Task StopBetweenEngineTeardownAndReplay_RestoresTheOriginalSong()
    {
        var original = Path.Combine(_dir, "original.wav");
        File.WriteAllBytes(original, _wave);
        await _audio.LoadAudioAsync(original);
        var vm = new RunListenBackViewModel(_audio, new TakeStore(_dir), new ExportTargetWriter(), null,
            FakeLocalization.English(), new FakeAppLogger());
        vm.OnTakeSaved("take.wav", true);
        await vm.ListenBackToRunCommand.ExecuteAsync(null);
        Assert.True(vm.IsTakePlaying);
        var take = Field<MediaLoadRequest>("_requestedMediaLoad");
        Task stopping;
        var staleOpens = 0;
        lock (Field<object>("_mediaLoadGate"))
        {
            lock (Field<object>("_gate"))
            {
                typeof(JuceAudioPlaybackService).GetMethod("FreeAudioLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_audio, null);
            }
            Assert.Empty(_audio.CurrentAudioPath);
            stopping = vm.StopAsync();
            var engine = Field<JuceAudioEngine>("_engine");
            MediaRebuildReplay.Replay<JuceAudioSource, object>(Field<object>("_mediaLoadGate"), Field<object>("_gate"), take,
                () => Field<MediaLoadRequest>("_requestedMediaLoad"), engine, () => engine,
                () => { staleOpens++; return engine.OpenMemory(_wave, "wav"); },
                reopened => reopened.Dispose(), () => true, null, _ => { });
        }
        await stopping;

        Assert.Equal(0, staleOpens);
        Assert.Equal(original, _audio.CurrentAudioPath);
        Assert.False(vm.IsTakePlaying);
        Assert.True(_audio.OwnsMedia(original, _audio.CurrentMediaGeneration));
    }

    [Fact]
    public void FailedEarlierExplicitWorker_DoesNotInvalidateTheQueuedTake()
    {
        var song = Request("song-b.wav", false, out _);
        var take = Request("take.wav", true, out var previous);
        using var cancellation = new CancellationTokenSource();
        var workers = new Action[]
        {
            () => Swap(song, null, CancellationToken.None, () => throw new IOException("synthetic decode failure")),
            () => Swap(take, previous, cancellation.Token)
        };

        var failure = Assert.Throws<TargetInvocationException>(workers[0]);
        Assert.IsType<IOException>(failure.InnerException);
        workers[1]();

        Assert.Equal("take.wav", _audio.CurrentAudioPath);
        Assert.Same(take, Field<MediaLoadRequest>("_requestedMediaLoad"));
        Assert.Same(take, Field<MediaLoadRequest>("_loadedMediaLoad"));
        Assert.True(Field<JuceAudioSource>("_source").Length > TimeSpan.Zero);
    }

    [Fact]
    public void TwoQueuedTakes_RunInRequestOrder_LeaveTheNewerTakeLoaded()
    {
        Request("song-b.wav", false, out _);
        var first = Request("take-1.wav", true, out var firstPrevious);
        var second = Request("take-2.wav", true, out var secondPrevious);
        using var cancellation = new CancellationTokenSource();
        var workers = new Action[]
        {
            () => Swap(first, firstPrevious, cancellation.Token),
            () => Swap(second, secondPrevious, cancellation.Token)
        };

        workers[0]();
        workers[1]();

        Assert.Equal("take-2.wav", _audio.CurrentAudioPath);
        Assert.Same(second, Field<MediaLoadRequest>("_requestedMediaLoad"));
        Assert.Same(second, Field<MediaLoadRequest>("_loadedMediaLoad"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledNewerTake_DoesNotRevokePublishedTakesHandBack(bool cancel, bool teardown)
    {
        var original = Path.Combine(_dir, "original.wav");
        File.WriteAllBytes(original, _wave);
        await _audio.LoadAudioAsync(original);
        var first = Request(Path.Combine(_dir, "take.wav"), true, out var previous);
        Swap(first, previous, CancellationToken.None);
        var second = Request("take-2.wav", true, out var secondPrevious);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();

        var failure = Assert.Throws<TargetInvocationException>(() => Swap(second, secondPrevious, cancellation.Token,
            () => throw new IOException("synthetic decode failure")));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        else Assert.IsType<IOException>(failure.InnerException);
        if (teardown)
        {
            lock (Field<object>("_gate"))
            {
                typeof(JuceAudioPlaybackService).GetMethod("FreeAudioLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_audio, null);
            }
        }

        Assert.True(await _audio.TryRestoreAudioAsync(original, first.Identity!, first.Generation, 0));
        Assert.Equal(original, _audio.CurrentAudioPath);
    }

    [Fact]
    public async Task LiveNewerTake_PreventsThePublishedTakesHandBack()
    {
        var first = Request("take-1.wav", true, out var previous);
        Swap(first, previous, CancellationToken.None);
        var second = Request("take-2.wav", true, out var secondPrevious);

        Assert.False(await _audio.TryRestoreAudioAsync(null, first.Identity!, first.Generation, 0));
        Swap(second, secondPrevious, CancellationToken.None);
        Assert.Equal("take-2.wav", _audio.CurrentAudioPath);
    }

    [Fact]
    public async Task CancellingAQueuedTake_BeforeItsWorkerStarts_SettlesTheCandidate()
    {
        var original = Path.Combine(_dir, "original.wav");
        File.WriteAllBytes(original, _wave);
        var first = Request("take-1.wav", true, out var previous);
        Swap(first, previous, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task take;
        Task<bool> restoration;
        lock (Field<object>("_mediaLoadGate"))
        {
            take = _audio.LoadTemporaryAudioAsync(Path.Combine(_dir, "take.wav"), cancellation.Token, out _);
            cancellation.Cancel();
            restoration = _audio.TryRestoreAudioAsync(original, first.Identity!, first.Generation, 0);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
        Assert.True(await restoration);
        Assert.Equal(original, _audio.CurrentAudioPath);
    }

    [Fact]
    public void ExplicitFree_SettlesLogicalOwnershipToEmpty()
    {
        var take = Request("take.wav", true, out var previous);
        Swap(take, previous, CancellationToken.None);

        _audio.FreeAudio();

        Assert.False(_audio.OwnsMedia(take.Identity!, take.Generation));
        Assert.Null(Field<MediaLoadRequest>("_loadedMediaLoad").Identity);
        Assert.Equal(Field<MediaLoadRequest>("_requestedMediaLoad"), Field<MediaLoadRequest>("_loadedMediaLoad"));
    }

    private sealed class TakeStore(string directory) : ILiveTakeStore
    {
        public bool IsEnabled => true;
        public string TakesDirectory => directory;
        public LiveTakeRecorder? TryStartTake(int sampleRate) => null;
        public Task<string?> FinalizeTakeAsync(LiveTakeRecorder recorder) => Task.FromResult<string?>(null);
        public void CompleteTake(LiveTakeRecorder recorder) { }
        public Task CleanupAsync() => Task.CompletedTask;
    }

    private MediaLoadRequest Request(string identity, bool temporary, out MediaLoadRequest previous)
    {
        var args = new object?[] { identity, temporary, null };
        var request = (MediaLoadRequest)typeof(JuceAudioPlaybackService)
            .GetMethod("RequestMediaLoad", BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(string), typeof(bool), typeof(MediaLoadRequest).MakeByRefType()], null)!.Invoke(_audio, args)!;
        previous = (MediaLoadRequest)args[2]!;
        return request;
    }

    private void Swap(MediaLoadRequest request, MediaLoadRequest? previous, CancellationToken cancellation, Action? beforeOpen = null)
    {
        Func<JuceAudioEngine, JuceAudioSource> open = engine =>
        {
            beforeOpen?.Invoke();
            return engine.OpenMemory(_wave, "wav");
        };
        typeof(JuceAudioPlaybackService).GetMethod("SwapMasterSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_audio, [request.Identity!, request, open, cancellation, previous]);
    }

    private T Field<T>(string name) => (T)typeof(JuceAudioPlaybackService)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_audio)!;

    public void Dispose()
    {
        _audio.Dispose();
        _nullDevice.Dispose();
        BestEffortDelete.Directory(_dir);
    }
}
