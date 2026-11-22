using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Rule 4 retains main's prepared settings, while leaving Get Ready fixes #567's actual leak.</summary>
public sealed class PreparedSettingsRestorationReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedPreparation_KeepsMainSettingsUntilBackThenRestoresEverything(bool noInput)
    {
        StemTrackViewModel? guitar = null;
        StemTrackViewModel? other = null;
        using var h = new PlayModeReviewHarness(stems: (state, audio) =>
        {
            var mixer = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly());
            guitar = new StemTrackViewModel(audio, new Stem { Name = "guitar" }, FakeLocalization.KeysOnly());
            other = new StemTrackViewModel(audio, new Stem { Name = "other" }, FakeLocalization.KeysOnly()) { IsMuted = true };
            mixer.Tracks.Add(guitar);
            mixer.Tracks.Add(other);
            return mixer;
        });
        Assert.NotNull(guitar);
        Assert.NotNull(other);
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(-1));
        arrangement.Levels.Add(new ChartLevel());
        h.Vm.SelectedFlowArrangement = new ArrangementOption(arrangement, FakeLocalization.English());
        h.Vm.DropTuner.IsOn = true;
        h.Vm.MuteMyInstrument = true;
        h.Transport.PlaybackSpeed = 0.85;
        h.Transport.InputPitchShiftSemis = -3;
        h.Transport.PitchShift = -2;
        h.State.HighwayScrollSpeed = 1.75;
        h.Vm.PracticeSpeedPercent = 50;
        h.Vm.RunScrollSpeed = 2.5;
        h.Config.AppConfig.GameplayMetronomeEnabled = true;
        h.Config.AppConfig.GameplayClapsEnabled = true;
        if (noInput) h.Audio.InputSampleRate = 0;
        else h.State.ActiveArrangement = null;

        PlayModeReviewHarness.Invoke(h.Vm, "BeginGameplay");

        Assert.False(h.Live.IsActive);
        Assert.Equal(PlayModeScreen.TuningCheck, h.Vm.CurrentScreen);
        Assert.Equal(0.5, h.Transport.PlaybackSpeed, 3);
        Assert.Equal(-1, h.Transport.InputPitchShiftSemis);
        Assert.True(guitar.IsMuted);
        Assert.True(other.IsMuted);
        Assert.True(h.Vm.NavigateBack());
        Assert.Equal(0.85, h.Transport.PlaybackSpeed, 3);
        Assert.Equal(-3, h.Transport.InputPitchShiftSemis);
        Assert.Equal(-2, h.Transport.PitchShift);
        Assert.Equal(1.75, h.State.HighwayScrollSpeed);
        Assert.False(guitar.IsMuted);
        Assert.True(other.IsMuted);
        Assert.False(h.Transport.GameplayMetronomeActive);
        Assert.False(h.Transport.GameplayClapsActive);
        Assert.Equal(PlayModeScreen.SongSelect, h.Vm.CurrentScreen);
    }
}
