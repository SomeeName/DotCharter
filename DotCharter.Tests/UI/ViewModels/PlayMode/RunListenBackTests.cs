using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

public sealed class RunListenBackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listen_back_borrows_an_audible_master_and_restores_its_previous_mute(bool muted)
    {
        var audio = new ListenAudio();
        var project = new ChartProject { OriginalMixMuted = muted };
        using var mixer = new StemMixerViewModel(new ProjectState(), audio,
            FakeLocalization.English(), ui: new TestUiDispatcher());
        var master = new StemTrackViewModel(audio, "Original", muted, FakeLocalization.English());
        var stem = new Stem { Name = "bass", Muted = false, Solo = true };
        var track = new StemTrackViewModel(audio, stem, FakeLocalization.English());
        mixer.Tracks.Add(master);
        mixer.Tracks.Add(track);
        audio.SetMasterMixMuted(muted);
        await audio.LoadAudioAsync("song.wav");
        audio.SeekTo(1234);
        var vm = new RunListenBackViewModel(audio, new InertLiveTakeStore(), new OversizedFileWriter(1),
            mixer, FakeLocalization.English(), new FakeAppLogger());
        vm.OnTakeSaved("take.opus", resultsShowing: true);
        try
        {
            await vm.ListenBackToRunCommand.ExecuteAsync(null);

            Assert.True(vm.IsTakePlaying);
            Assert.False(audio.MasterMixMuted);
            Assert.False(master.IsMuted);
            Assert.True(track.IsMuted);
            Assert.Equal(muted, project.OriginalMixMuted);
            Assert.False(stem.Muted);
            Assert.True(stem.Solo);
        }
        finally
        {
            await vm.StopAsync();
        }

        Assert.Equal(muted, audio.MasterMixMuted);
        Assert.Equal(muted, master.IsMuted);
        Assert.False(track.IsMuted);
        Assert.True(track.IsSolo);
        Assert.Equal(muted, project.OriginalMixMuted);
        Assert.Equal("song.wav", audio.CompletedLoadPath);
        Assert.Equal(1234, audio.LastSeekToMs);
    }

    private sealed class ListenAudio : FakeAudioPlaybackService, IAudioPlaybackService
    {
        public string CurrentAudioPath => CompletedLoadPath ?? "";

        public async Task<bool> TryRestoreAudioAsync(string? path, string ownedPath, long generation, double position)
        {
            await LoadAudioAsync(path ?? "");
            SeekTo(position);
            return true;
        }
    }
}
