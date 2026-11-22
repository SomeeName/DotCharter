using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Deterministic queued transport workers keep hand-back behind the user's newest explicit song request.</summary>
public sealed class RunListenBackOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "listen-owner-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingATake_CannotSupersedeANewerExplicitSongRequest(bool publishNewSong)
    {
        var (vm, audio) = Create();
        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;
        Assert.True(vm.IsTakePlaying);
        var newer = audio.Request("song-b.wav", TestContext.Current.CancellationToken);
        if (publishNewSong) audio.Workers.Dequeue()();
        var generation = audio.RequestedGeneration;
        audio.StoppedPaths.Clear();

        var stop = vm.StopAsync();
        while (audio.Workers.TryDequeue(out var worker)) worker();
        await Task.WhenAll(stop, newer);

        Assert.Equal(generation, audio.RequestedGeneration);
        Assert.Equal("song-b.wav", audio.Path);
        Assert.DoesNotContain("song-b.wav", audio.StoppedPaths);
        Assert.False(vm.IsTakePlaying);
        Assert.DoesNotContain(321, audio.SeekHistory);
    }

    [Fact]
    public async Task ANewSongWhileHandBackDecodes_CannotReceiveTheOldSongsSeek()
    {
        var (vm, audio) = Create();
        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;
        var stop = vm.StopAsync();
        var handBack = audio.Workers.Dequeue();
        var newer = audio.Request("song-c.wav", TestContext.Current.CancellationToken);

        audio.Workers.Dequeue()();
        await newer;
        handBack();
        await stop;

        Assert.Equal("song-c.wav", audio.Path);
        Assert.DoesNotContain(321, audio.SeekHistory);
    }

    [Fact]
    public async Task AFailedHandBack_IsReportedWithoutThrowingFromStop()
    {
        var (vm, audio) = Create();
        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;
        audio.FailReads = true;

        var stop = vm.StopAsync();
        audio.Workers.Dequeue()();
        await stop;

        Assert.False(vm.IsTakePlaying);
        Assert.Empty(audio.Path);
        Assert.NotEmpty(vm.TakePlaybackStatus);
        Assert.DoesNotContain(321, audio.SeekHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASongPublishedWhileTheTakeStarts_ReceivesNeitherTheTakesSeekNorItsPlay(bool duringSeek)
    {
        var (vm, audio) = Create();
        var published = false;
        void PublishOnce()
        {
            if (published) return;
            published = true;
            audio.Publish("song-b.wav");
        }
        if (duringSeek) audio.PositionChanged += (_, _) => PublishOnce();
        else audio.DuringStop = PublishOnce;

        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;

        Assert.True(published);
        Assert.Equal("song-b.wav", audio.Path);
        Assert.False(vm.IsTakePlaying);
        Assert.False(audio.PlayCalled);
        if (!duringSeek) Assert.DoesNotContain(0d, audio.SeekHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASongPublishedRightAfterTheOwnershipCheck_ReceivesNeitherTheSeekNorThePlay(bool beforePlay)
    {
        var (vm, audio) = Create();
        // The publish waits for the transport's gate: it lands right after the next
        // ownership check, or right after the next check-and-act, whichever comes first.
        audio.DuringStop = () =>
        {
            if (!beforePlay && audio.Path.EndsWith("take.wav", StringComparison.Ordinal)) audio.PublishAfterNextCheck = "song-b.wav";
        };
        audio.AfterSeek = () =>
        {
            if (beforePlay) audio.PublishAfterNextCheck = "song-b.wav";
        };

        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;

        Assert.Equal("song-b.wav", audio.Path);
        Assert.Null(audio.PublishAfterNextCheck);
        Assert.DoesNotContain(audio.Acts, act => act.Path == "song-b.wav");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewSongBeforeStopOrBeforeMutes_ReceivesNoTakeTransportOrMixActions(bool afterStop)
    {
        var (vm, audio) = Create();
        if (afterStop) audio.DuringStop = () => audio.Publish("song-b.wav");
        else audio.PublishAfterNextCheck = "song-b.wav";

        var playing = vm.ListenBackToRunCommand.ExecuteAsync(null);
        audio.Workers.Dequeue()();
        await playing;

        Assert.Equal("song-b.wav", audio.Path);
        Assert.DoesNotContain("song-b.wav", audio.StoppedPaths);
        Assert.DoesNotContain(audio.Acts, act => act.Path == "song-b.wav");
        Assert.False(vm.IsTakePlaying);
    }

    private (RunListenBackViewModel Vm, QueuedAudio Audio) Create()
    {
        Directory.CreateDirectory(_dir);
        var song = Path.Combine(_dir, "song-a.wav");
        File.WriteAllText(song, "synthetic song");
        File.WriteAllText(Path.Combine(_dir, "take.wav"), "synthetic take");
        var audio = new QueuedAudio { Path = song };
        audio.SeekTo(321);
        audio.SeekHistory.Clear();
        var vm = new RunListenBackViewModel(audio, new TakeStore(_dir), new ExportTargetWriter(), null,
            FakeLocalization.English(), new FakeAppLogger());
        vm.OnTakeSaved("take.wav", true);
        return (vm, audio);
    }

    private sealed class QueuedAudio : FakeAudioPlaybackService, IAudioPlaybackService
    {
        private long _loadedGeneration;
        private bool _acting;
        private string? _waitingPublish;
        public bool FailReads { get; set; }
        public long RequestedGeneration { get; private set; }
        public long CurrentMediaGeneration => _loadedGeneration;
        public string Path { get; set; } = "";
        public Queue<Action> Workers { get; } = new();
        public List<string> StoppedPaths { get; } = [];
        string IPlaybackTransport.CurrentAudioPath => Path;

        Task IPlaybackTransport.LoadAudioAsync(string path) => Request(path);
        Task IPlaybackTransport.LoadAudioAsync(string path, CancellationToken cancellation) => Request(path, cancellation);
        public Action? DuringStop { get; set; }
        public Action? AfterSeek { get; set; }
        public string? PublishAfterNextCheck { get; set; }
        public List<(string Op, string Path)> Acts { get; } = [];

        public bool RunIfOwned(string path, long generation, Action act)
        {
            var owns = Owns(path, generation);
            if (owns)
            {
                _acting = true;
                try { act(); }
                finally { _acting = false; }
            }
            if (_waitingPublish is { } waiting)
            {
                _waitingPublish = null;
                Publish(waiting);
            }
            LetThePublishIn();
            return owns;
        }

        void IStemMixer.SetMasterMixMuted(bool muted)
        {
            Acts.Add(("master-mute", Path));
            SetMasterMixMuted(muted);
        }

        private bool Owns(string path, long generation) =>
            generation == _loadedGeneration && string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);

        private void LetThePublishIn()
        {
            if (PublishAfterNextCheck is not { } waiting) return;
            PublishAfterNextCheck = null;
            Publish(waiting);
        }

        bool IPlaybackTransport.OwnsMedia(string path, long generation)
        {
            var owns = Owns(path, generation);
            LetThePublishIn();
            return owns;
        }

        void IPlaybackTransport.SeekTo(double timeMs) => RecordedSeek(timeMs);

        void IPlaybackTransport.Play() => RecordedPlay();

        private void RecordedSeek(double timeMs)
        {
            Acts.Add(("seek", Path));
            SeekTo(timeMs);
            AfterSeek?.Invoke();
        }

        private void RecordedPlay()
        {
            Acts.Add(("play", Path));
            Play();
        }

        void IPlaybackTransport.Stop()
        {
            StoppedPaths.Add(Path);
            Stop();
            DuringStop?.Invoke();
        }

        /// <summary>A newer song's worker publishing on its own thread, between two transport calls.</summary>
        public void Publish(string path)
        {
            if (_acting)
            {
                _waitingPublish = path;
                return;
            }
            Path = path;
            _loadedGeneration = ++RequestedGeneration;
        }

        public Task LoadTemporaryAudioAsync(string path, CancellationToken cancellation, out long generation)
        {
            var loading = Request(path, cancellation);
            generation = RequestedGeneration;
            return loading;
        }

        public Task Request(string path, CancellationToken cancellation = default)
        {
            var generation = ++RequestedGeneration;
            var ready = new TaskCompletionSource();
            Workers.Enqueue(() =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    ready.SetCanceled(cancellation);
                }
                else if (FailReads)
                {
                    Path = "";
                    ready.SetException(new IOException("synthetic hand-back decode failure"));
                }
                else
                {
                    if (generation == RequestedGeneration)
                    {
                        Path = path;
                        _loadedGeneration = generation;
                    }
                    ready.SetResult();
                }
            });
            return ready.Task;
        }

        public async Task<bool> TryRestoreAudioAsync(string? path, string ownedPath, long generation, double position)
        {
            if (Path != ownedPath || generation != _loadedGeneration || generation != RequestedGeneration) return false;
            StoppedPaths.Add(Path);
            Stop();
            var loading = Request(path ?? "");
            var request = RequestedGeneration;
            await loading;
            if (request != RequestedGeneration || request != _loadedGeneration) return false;
            SeekTo(position);
            return true;
        }
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

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
