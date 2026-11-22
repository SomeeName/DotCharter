using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Delayed Results loads must lose ownership before they can play or mute stems.</summary>
public sealed class RunListenBackViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "listen-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LeavingResultsOrStoppingDuringLoad_CancelsTheTakeAndKeepsTheSong(int exit)
    {
        Directory.CreateDirectory(_dir);
        var song = Path.Combine(_dir, "song.wav");
        File.WriteAllText(song, "synthetic song");
        File.WriteAllText(Path.Combine(_dir, "take.wav"), "synthetic take");
        var audio = new DelayedAudio { Path = song };
        var mixer = new StemMixerViewModel(new ProjectState(), audio, FakeLocalization.English());
        var stem = new Stem { Name = "vocals", Path = "vocals.wav" };
        var track = new StemTrackViewModel(audio, stem, FakeLocalization.English());
        mixer.Tracks.Add(track);
        var profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "players.json"), "Player");
        var practice = new PracticeProfileService(filePath: Path.Combine(_dir, "practice.json"));
        var host = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(practice), new LeaderboardService(profiles),
            new ProjectState(), FakeLocalization.English(), audio: audio, takeStore: new TakeStore(_dir), stemMixer: mixer);
        host.CurrentScreen = PlayModeScreen.Results;
        host.ListenBack.OnTakeSaved("take.wav", true);
        var play = host.ListenBack.ListenBackToRunCommand.ExecuteAsync(null);
        await audio.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = Task.CompletedTask;
        try
        {
            switch (exit)
            {
                case 0:
                    stop = host.ListenBack.StopAsync();
                    break;
                case 1:
                    host.CurrentScreen = PlayModeScreen.SongSelect;
                    break;
                case 2:
                    host.OpenInEditorCommand.Execute(null);
                    break;
                case 3:
                    host.OpenSectionInEditorCommand.Execute(new SectionRow("Solo", "0:00", "90%", 1, true, 0)
                    {
                        Localization = FakeLocalization.English()
                    });
                    break;
            }
        }
        finally { audio.Release.SetResult(); }
        await Task.WhenAll(play, stop);

        Assert.True(audio.LoadCancellation.IsCancellationRequested);
        Assert.False(host.ListenBack.IsTakePlaying);
        Assert.False(audio.PlayCalled);
        Assert.False(track.IsMuted);
        Assert.False(stem.Muted);
        Assert.Equal(song, audio.Path);
        Assert.Empty(host.ListenBack.TakePlaybackStatus);
    }

    [Fact]
    public async Task AFailedLoad_LeavesNoMutedStemsOrBorrowedTransport()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "take.wav"), "synthetic take");
        var audio = new DelayedAudio { Fail = true };
        var mixer = new StemMixerViewModel(new ProjectState(), audio, FakeLocalization.English());
        var track = new StemTrackViewModel(audio, new Stem { Name = "vocals" }, FakeLocalization.English());
        mixer.Tracks.Add(track);
        var vm = new RunListenBackViewModel(audio, new TakeStore(_dir), new ExportTargetWriter(), mixer,
            FakeLocalization.English(), new FakeAppLogger());
        vm.OnTakeSaved("take.wav", true);
        audio.Release.SetResult();
        await vm.ListenBackToRunCommand.ExecuteAsync(null);

        Assert.False(vm.IsTakePlaying);
        Assert.False(track.IsMuted);
        Assert.False(audio.PlayCalled);
    }

    private sealed class DelayedAudio : FakeAudioPlaybackService, IAudioPlaybackService
    {
        public string Path { get; set; } = "";
        public bool Fail { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LoadCancellation { get; private set; }
        string IPlaybackTransport.CurrentAudioPath => Path;

        Task IPlaybackTransport.LoadAudioAsync(string filePath) => Load(filePath, CancellationToken.None);

        Task IPlaybackTransport.LoadAudioAsync(string filePath, CancellationToken cancellationToken) => Load(filePath, cancellationToken);

        private async Task Load(string filePath, CancellationToken cancellationToken)
        {
            LoadCancellation = cancellationToken;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Synthetic decode failure");
            Path = filePath;
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
