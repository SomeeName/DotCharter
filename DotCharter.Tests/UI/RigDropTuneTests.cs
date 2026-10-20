using DotCharter.RigLab;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig footer's DROP TUNE pedal: a control over a value the HOST owns —
///     DotCharter's transport (one value for the footer, the transport's picker and
///     a song's own drop tune) or RigLab's engine + riglab.json.
/// </summary>
public sealed class RigDropTuneTests
{
    /// <summary>
    ///     DOWN ONLY: the engine's live shifter tunes down with no latency floor and
    ///     cannot tune up, and it answered a "+2" with OK while doing nothing. The
    ///     pedal offered +12…−12 until 2026-09-29 (owner: "only works downwards").
    ///     So no picker offers "up", and no writer passes one on.
    /// </summary>
    [Fact]
    public void ThePedal_OffersZeroDownToMinusTwelve_AndNeverWritesAnUpwardShift()
    {
        var value = 0;
        var pedal = new RigDropTuneViewModel(() => value, v => value = v);

        Assert.Equal(13, pedal.Steps.Count);
        Assert.Equal(0, pedal.Steps[0].Semitones);
        Assert.Equal(-12, pedal.Steps[^1].Semitones);
        Assert.DoesNotContain(pedal.Steps, s => s.Semitones > 0);
        Assert.DoesNotContain(RigDropTuneStep.AllSemitones, s => s > 0);

        pedal.Semitones = 3;
        Assert.Equal(0, value);
        value = 7;                                  // a stale positive value from an old config
        Assert.Equal(0, pedal.Semitones);
    }

    [Fact]
    public void TheTransport_ClampsAnUpwardShiftToZero_BeforeTheEngineOrTheConfigSeesIt()
    {
        var audio = new FakeAudioPlaybackService();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.KeysOnly())
        {
            InputPitchShiftSemis = -3
        };
        transport.InputPitchShiftSemis = 4;

        Assert.Equal(0, transport.InputPitchShiftSemis);
        Assert.Equal(0.0, audio.InputPitchShiftSemitones);
        Assert.Equal(0, config.AppConfig.InputPitchShiftSemis);
    }

    [Fact]
    public void RigLab_DoesNotApplyAnUpwardShiftSavedByAnOlderVersion()
    {
        var audio = new FakeAudioPlaybackService();
        var config = new FakeConfigurationService();
        config.AppConfig.InputPitchShiftSemis = 3;

        var pedal = RigLabHost.DropTuneOver(audio, config);

        Assert.Equal(0.0, audio.InputPitchShiftSemitones);
        Assert.Equal(0, pedal.Semitones);
    }

    [Fact]
    public void ThePedal_ClampsWhatItWrites()
    {
        var value = 0;
        var pedal = new RigDropTuneViewModel(() => value, v => value = v);

        Assert.Equal(["+3", "0", "−2"], new[] { 3, 0, -2 }.Select(s => new RigDropTuneStep(s).Label));
        Assert.False(pedal.IsEngaged);

        var changed = new List<string?>();
        pedal.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        pedal.SelectedStep = pedal.Steps.Single(s => s.Semitones == -2);
        Assert.Equal(-2, value);
        Assert.True(pedal.IsEngaged);
        Assert.Contains(nameof(RigDropTuneViewModel.SelectedStep), changed);

        pedal.Semitones = -40;
        Assert.Equal(-12, value);

        // a change made ELSEWHERE shows once the host says so
        value = -5;
        changed.Clear();
        pedal.Refresh();
        Assert.Equal(-5, pedal.SelectedStep.Semitones);
        Assert.Contains(nameof(RigDropTuneViewModel.Semitones), changed);
    }

    [Fact]
    public void RigLab_AppliesTheSavedShiftAtStart_AndRemembersANewOne()
    {
        var audio = new FakeAudioPlaybackService();
        var config = new FakeConfigurationService();
        config.AppConfig.InputPitchShiftSemis = -2;

        var pedal = RigLabHost.DropTuneOver(audio, config);
        Assert.Equal(-2.0, audio.InputPitchShiftSemitones);
        Assert.Equal(-2, pedal.Semitones);

        pedal.Semitones = -5;
        Assert.Equal(-5.0, audio.InputPitchShiftSemitones);
        Assert.Equal(-5, config.AppConfig.InputPitchShiftSemis);
    }

    [Fact]
    public void DotCharter_DrivesTheTransportsPedal_AndFollowsItsChanges()
    {
        var audio = new FakeAudioPlaybackService();
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.KeysOnly());

        var pedal = RigDropTune.Over(transport)!;
        pedal.Semitones = -3;
        Assert.Equal(-3, transport.InputPitchShiftSemis);
        Assert.Equal(-3.0, audio.InputPitchShiftSemitones);
        Assert.Equal(-3, config.AppConfig.InputPitchShiftSemis);

        // a song's own drop tune (or the transport's picker) moves the footer too
        var raised = false;
        pedal.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(RigDropTuneViewModel.SelectedStep);
        transport.InputPitchShiftSemis = -7;
        Assert.True(raised);
        Assert.Equal(-7, pedal.SelectedStep.Semitones);

        Assert.Null(RigDropTune.Over(null));
    }
}
