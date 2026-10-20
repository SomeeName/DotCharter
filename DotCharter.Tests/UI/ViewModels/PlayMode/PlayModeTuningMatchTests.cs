using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode's "Play in my tuning" (#312), end to end through the view model: the Get Ready
///     verdict per path, the run shifting the SONG and judging with it, every way off the run
///     putting the transport back, and the opt-in living on the player profile.
/// </summary>
public sealed class PlayModeTuningMatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnEFlatPath_PlayedInMyTuning_ShiftsTheSongForTheRun_AndTheUnwindPutsItBack()
    {
        var vm = NewVm(out var state, out var transport, out var livePlay);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        transport.PitchShift = -2; // the editor's own pitch slider, before Play Mode
        vm.PreparePrePlay();

        Assert.True(vm.TuningMatch.CanPlayInMyTuning);
        vm.TuningMatch.IsOn = true;
        Assert.Equal(1, vm.TuningMatch.EffectiveSemitones);

        BeginGameplay(vm);
        Assert.Equal(1, transport.PitchShift, 3);
        Assert.Equal(1, livePlay.TransposeSemitones);

        // A run chain (Retry / Restart) must not make the run's shift the new baseline.
        BeginGameplay(vm);
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(-2, transport.PitchShift, 3);
        Assert.Equal(0, livePlay.TransposeSemitones);
    }

    /// <summary>Drop D against E standard moves one string's frets. The stored opt-in survives,
    /// but the switch reads OFF and disabled with the reason printed, and a run shifts nothing.</summary>
    [Fact]
    public void ADropDPath_ShowsTheSwitchDisabledWithItsReason_AndARunShiftsNothing()
    {
        var vm = NewVm(out var state, out var transport, out var livePlay);
        Seed(state, Tuned("Lead", -2, 0, 0, 0, 0, 0));
        vm.TuningMatch.PlayInMyTuning = true; // opted in on an earlier song
        vm.PreparePrePlay();

        Assert.False(vm.TuningMatch.CanPlayInMyTuning);
        Assert.False(vm.TuningMatch.IsOn);
        Assert.Equal(0, vm.TuningMatch.EffectiveSemitones);
        Assert.StartsWith("Unavailable:", vm.TuningMatch.StatusText);
        Assert.Contains("not the same distance", vm.TuningMatch.StatusText);

        // Writing the switch on a path that cannot honour it changes nothing.
        vm.TuningMatch.IsOn = true;
        Assert.False(vm.TuningMatch.IsOn);

        transport.PitchShift = 0;
        BeginGameplay(vm);
        Assert.Equal(0, transport.PitchShift, 3);
        Assert.Equal(0, livePlay.TransposeSemitones);
        Assert.True(vm.TuningMatch.PlayInMyTuning, "a refused path must not clear the player's stored choice");
    }

    /// <summary>Switching path re-answers the verdict: E♭ can shift, the Drop D path beside it
    /// cannot, and back again.</summary>
    [Fact]
    public void SwitchingPath_ReAnswersTheVerdict()
    {
        var vm = NewVm(out var state, out _, out _);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1), Tuned("Rhythm", -2, 0, 0, 0, 0, 0));
        vm.PreparePrePlay();
        vm.TuningMatch.IsOn = true;

        vm.SelectedFlowArrangement = vm.FlowArrangements[1];
        Assert.False(vm.TuningMatch.IsOn);
        Assert.Equal(0, vm.TuningMatch.EffectiveSemitones);

        vm.SelectedFlowArrangement = vm.FlowArrangements[0];
        Assert.True(vm.TuningMatch.IsOn);
        Assert.Equal(1, vm.TuningMatch.EffectiveSemitones);
    }

    /// <summary>The tuning lockup says what the guitar has to BE: while the song comes to the
    /// player that is THEIR tuning, and nothing needs doing to the guitar.</summary>
    [Fact]
    public void WhileShifted_TheLockupShowsThePlayersTuning_AndNeedsNoAttention()
    {
        var vm = NewVm(out var state, out _, out _);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        vm.PreparePrePlay();
        Assert.True(vm.FlowTuningNeedsAttention, "E♭ standard asks an E-standard player to retune");
        Assert.Equal(vm.FlowTuning, vm.FlowTunerTarget);
        Assert.Equal("Expected by this chart", vm.FlowTunerTargetCaption);

        vm.TuningMatch.IsOn = true;

        Assert.Equal(vm.TuningMatch.MyTuningName, vm.FlowTuningAndCapoLabel);
        Assert.False(vm.FlowTuningNeedsAttention);
        // The tuner's heading names what the strings are checked against — not the chart's E♭.
        Assert.Equal(vm.TuningMatch.MyTuningName, vm.FlowTunerTarget);
        Assert.Equal("Your tuning: the song is shifted to it", vm.FlowTunerTargetCaption);
    }

    /// <summary>The player's tuning matters: a player who keeps their guitar in E♭ plays the
    /// E♭ path as charted, and an E-standard path shifted DOWN.</summary>
    [Fact]
    public void ThePlayersOwnTuning_IsWhatTheVerdictIsMeasuredFrom()
    {
        var vm = NewVm(out var state, out _, out _);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1), Tuned("Rhythm", 0, 0, 0, 0, 0, 0));
        vm.PreparePrePlay();
        vm.TuningMatch.SelectedMyTuning = vm.TuningMatch.Choices.First(c => c.Tuning.Steps.All(s => s == -1));

        Assert.False(vm.TuningMatch.CanPlayInMyTuning);
        Assert.Contains(vm.TuningMatch.MyTuningName, vm.TuningMatch.StatusText);

        vm.SelectedFlowArrangement = vm.FlowArrangements[1];
        vm.TuningMatch.IsOn = true;
        Assert.Equal(-1, vm.TuningMatch.EffectiveSemitones);
    }

    /// <summary>Both settings are the PROFILE's, like the lefty flip: they survive a restart.</summary>
    [Fact]
    public void TheTuningAndTheOptIn_ArePersistedOnTheActiveProfile()
    {
        var file = Path.Combine(_dir, "player-profiles.json");
        var vm = NewVm(out var state, out _, out _);
        Seed(state, Tuned("Lead", -2, -2, -2, -2, -2, -2));
        vm.PreparePrePlay();
        var dropD = vm.TuningMatch.Choices.First(c => c.Tuning.Steps.SequenceEqual(new[] { -2, 0, 0, 0, 0, 0 }));
        vm.TuningMatch.SelectedMyTuning = dropD;
        vm.TuningMatch.PlayInMyTuning = true;

        var reread = new PlayerProfileService(FakeLocalization.English(), logger: null, filePath: file, defaultProfileName: "Tester").ActiveProfile;
        Assert.Equal(dropD.Tuning.Id, reread.MyTuningId);
        Assert.True(reread.PlayInMyTuning);

        var again = NewVm(out var state2, out _, out _);
        Seed(state2, Tuned("Lead", -2, -2, -2, -2, -2, -2));
        again.PreparePrePlay();
        Assert.Equal(dropD.Tuning.Id, again.TuningMatch.SelectedMyTuning.Tuning.Id);
        // D standard against Drop D is not uniform — the stored choice is read back, and refused.
        Assert.True(again.TuningMatch.PlayInMyTuning);
        Assert.False(again.TuningMatch.IsOn);
    }

    // ---- fixture -------------------------------------------------------------

    private static Arrangement Tuned(string name, params int[] steps)
    {
        var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Lead, StringCount = 6 };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var i = 0; i < 6; i++) level.Notes.Add(new GuitarNote { StringIndex = i, Fret = i % 4 });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static void Seed(ProjectState state, params Arrangement[] paths)
    {
        var project = new ChartProject();
        foreach (var path in paths) project.Arrangements.Add(path);
        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }

    /// <summary>Over a real transport and a real Live Play view model with fake audio, as
    /// <c>PlayModeRunChainStateTests</c> builds it; every view model in one test shares the
    /// profile file.</summary>
    private PlayModeViewModel NewVm(out ProjectState state, out TransportViewModel transport,
        out LivePlayViewModel livePlay)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var config = new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48_000 };
        state = new ProjectState();
        transport = new TransportViewModel(audio, state, config, FakeLocalization.KeysOnly());
        livePlay = LivePlayTestFactory.Create(audio, state, new FakeAppLogger(), config, store);

        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, FakeLocalization.English(), config,
            livePlay: livePlay, transport: transport);
    }

    /// <summary>Drives the private run start, as <c>PlayModeRunChainStateTests</c> does.</summary>
    private static void BeginGameplay(PlayModeViewModel vm)
    {
        var method = typeof(PlayModeViewModel).GetMethod("BeginGameplay",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
