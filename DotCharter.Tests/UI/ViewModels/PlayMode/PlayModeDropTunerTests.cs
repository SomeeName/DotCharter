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
///     Get Ready's per-song drop tuner (#635), through the view model: the verdict per path and
///     its printed reason, the per-song opt-in in <c>AppConfig.DropTunerSongs</c>, the run
///     shifting the INPUT, and its precedence over "Play in my tuning" (#312), which shows
///     disabled with that reason while the drop tuner is on.
/// </summary>
public sealed class PlayModeDropTunerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null, DropTunerVerdict.NotUniform)]
    [InlineData(0, DropTunerVerdict.NotNeeded)]
    [InlineData(1, DropTunerVerdict.AboveStandard)]
    [InlineData(-1, DropTunerVerdict.Shift)]
    [InlineData(-12, DropTunerVerdict.Shift)]
    [InlineData(-13, DropTunerVerdict.TooFar)]
    public void TheVerdict_IsWhatTheEngineCanHonour(int? uniformStep, DropTunerVerdict expected) =>
        Assert.Equal(expected, DropTunerViewModel.Judge(uniformStep));

    [Fact]
    public void AnEFlatPath_WithTheDropTunerOn_ShiftsTheInputDown_AndNotTheSong()
    {
        var vm = NewVm(out var state, out var transport, out var livePlay, out _);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        transport.InputPitchShiftSemis = 0;
        transport.PitchShift = 0;
        vm.TuningMatch.PlayInMyTuning = true; // opted in on the profile, too
        vm.PreparePrePlay();

        Assert.True(vm.DropTuner.IsApplicable);
        Assert.True(vm.DropTuner.CanUseDropTuner);
        Assert.Contains("-1", vm.DropTuner.ToggleLabel);
        Assert.Contains("experimental", vm.DropTuner.ToggleLabel);
        vm.DropTuner.IsOn = true;
        Assert.Equal(-1, vm.DropTuner.EffectiveSemitones);

        BeginGameplay(vm);

        Assert.Equal(-1, transport.InputPitchShiftSemis);
        Assert.Equal(0, transport.PitchShift, 3);
        Assert.Equal(0, livePlay.TransposeSemitones);
    }

    /// <summary>The drop tuner wins, visibly: "Play in my tuning" reads off and disabled with
    /// the reason printed while it is on, the player's stored opt-in survives, and turning the
    /// drop tuner off brings the switch back.</summary>
    [Fact]
    public void WhileTheDropTunerIsOn_PlayInMyTuning_IsDisabledWithItsReason()
    {
        var vm = NewVm(out var state, out _, out _, out _);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        vm.PreparePrePlay();
        vm.TuningMatch.IsOn = true;
        Assert.True(vm.TuningMatch.IsOn);

        vm.DropTuner.IsOn = true;

        Assert.False(vm.TuningMatch.CanPlayInMyTuning);
        Assert.False(vm.TuningMatch.IsOn);
        Assert.Equal(0, vm.TuningMatch.EffectiveSemitones);
        Assert.StartsWith("Off while the drop tuner is on", vm.TuningMatch.StatusText);
        vm.TuningMatch.IsOn = false; // a disabled switch ignores writes
        Assert.True(vm.TuningMatch.PlayInMyTuning, "the drop tuner must not clear the stored opt-in");

        vm.DropTuner.IsOn = false;

        Assert.True(vm.TuningMatch.CanPlayInMyTuning);
        Assert.True(vm.TuningMatch.IsOn);
        Assert.Equal(1, vm.TuningMatch.EffectiveSemitones);
        Assert.DoesNotContain("drop tuner", vm.TuningMatch.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The lockup says what the guitar has to BE: under the drop tuner that is
    /// standard, and a drop-tuned run is no claim that the chart's tuning was verified.</summary>
    [Fact]
    public void WithTheDropTunerOn_TheLockupAsksForStandard_AndConfirmingRecordsNoTuning()
    {
        var vm = NewVm(out var state, out _, out _, out var config);
        Seed(state, Tuned("Lead", -2, -2, -2, -2, -2, -2));
        vm.PreparePrePlay();
        Assert.True(vm.FlowTuningNeedsAttention);

        vm.DropTuner.IsOn = true;

        Assert.Equal("E Standard", vm.FlowTuningAndCapoLabel);
        Assert.Equal("E Standard", vm.FlowTunerTarget);
        Assert.False(vm.FlowTuningNeedsAttention);
        Assert.Equal("Standard: the drop tuner shifts your input", vm.FlowTunerTargetCaption);

        vm.ConfirmTuningCommand.Execute(null);
        Assert.True(string.IsNullOrEmpty(config.AppConfig.LastVerifiedTuning));
    }

    [Fact]
    public void APathTheEngineCannotHonour_ShowsTheSwitchDisabledWithItsReason()
    {
        var vm = NewVm(out var state, out var transport, out _, out _);
        Seed(state, Tuned("Drop D", -2, 0, 0, 0, 0, 0), Tuned("Up", 1, 1, 1, 1, 1, 1),
            Tuned("Standard", 0, 0, 0, 0, 0, 0));
        vm.PreparePrePlay();
        vm.DropTuner.UseDropTuner = true; // stored on for this song

        Assert.True(vm.DropTuner.IsApplicable);
        Assert.False(vm.DropTuner.CanUseDropTuner);
        Assert.False(vm.DropTuner.IsOn);
        Assert.StartsWith("Unavailable:", vm.DropTuner.StatusText);
        Assert.Contains("not the same distance from standard", vm.DropTuner.StatusText);
        // A refused drop tuner overrules nothing: Play in my tuning gives its OWN answer.
        Assert.DoesNotContain("drop tuner", vm.TuningMatch.StatusText, StringComparison.OrdinalIgnoreCase);
        vm.DropTuner.IsOn = true;
        Assert.False(vm.DropTuner.IsOn);

        vm.SelectedFlowArrangement = vm.FlowArrangements[1];
        Assert.False(vm.DropTuner.CanUseDropTuner);
        Assert.Contains("only shifts down", vm.DropTuner.StatusText);

        vm.SelectedFlowArrangement = vm.FlowArrangements[2];
        Assert.False(vm.DropTuner.IsApplicable);
        Assert.Equal("", vm.DropTuner.StatusText);

        transport.InputPitchShiftSemis = 0;
        BeginGameplay(vm);
        Assert.Equal(0, transport.InputPitchShiftSemis);
        Assert.True(vm.DropTuner.UseDropTuner, "a refused path must not clear the song's stored choice");
    }

    /// <summary>The opt-in belongs to the SONG: on for one song, off for the next, back on when
    /// the first is picked again and after a restart; switching it off removes the entry.</summary>
    [Fact]
    public void TheOptIn_IsRememberedPerSong()
    {
        var config = new FakeConfigurationService();
        var vm = NewVm(out var state, out _, out _, out _, config);
        Seed(state, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        var songA = Path.Combine(_dir, "Artist - A.rscp");
        var songB = Path.Combine(_dir, "Artist - B.rscp");

        Pick(vm, songA);
        Assert.False(vm.DropTuner.IsOn);
        vm.DropTuner.IsOn = true;
        Assert.Equal(new[] { songA }, config.AppConfig.DropTunerSongs);

        Pick(vm, songB);
        Assert.False(vm.DropTuner.IsOn);

        Pick(vm, songA);
        Assert.True(vm.DropTuner.IsOn);

        var restarted = NewVm(out var state2, out _, out _, out _, config);
        Seed(state2, Tuned("Lead", -1, -1, -1, -1, -1, -1));
        Pick(restarted, songA.ToUpperInvariant());
        Assert.True(restarted.DropTuner.IsOn);

        restarted.DropTuner.IsOn = false;
        Assert.Empty(config.AppConfig.DropTunerSongs);
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

    /// <summary>What a real song pick does before Get Ready: the flow song, then
    /// <c>PreparePrePlay</c>. An own chart's identity is its project path.</summary>
    private static void Pick(PlayModeViewModel vm, string projectPath)
    {
        var field = typeof(PlayModeViewModel).GetField("_flowSong", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(vm, PlaySongItem.FromProject(projectPath, fileExists: true, FakeLocalization.English()));
        vm.PreparePrePlay();
    }

    private PlayModeViewModel NewVm(out ProjectState state, out TransportViewModel transport,
        out LivePlayViewModel livePlay, out FakeConfigurationService config, FakeConfigurationService? shared = null)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        config = shared ?? new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48_000 };
        state = new ProjectState();
        transport = new TransportViewModel(audio, state, config, FakeLocalization.KeysOnly());
        livePlay = LivePlayTestFactory.Create(audio, state, new FakeAppLogger(), config, store);

        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, FakeLocalization.English(), config,
            livePlay: livePlay, transport: transport);
    }

    private PlayModeViewModel NewVm(out ProjectState state, out TransportViewModel transport,
        out LivePlayViewModel livePlay, out FakeConfigurationService config) =>
        NewVm(out state, out transport, out livePlay, out config, shared: null);

    /// <summary>Drives the private run start, as <c>PlayModeTuningMatchTests</c> does.</summary>
    private static void BeginGameplay(PlayModeViewModel vm)
    {
        var method = typeof(PlayModeViewModel).GetMethod("BeginGameplay",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
