using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Every way OUT of a run has to unwind it, and four of them did not.
///     <para>
///         A run applies five things that outlive it if nobody takes them back:
///         the practice playback speed, the per-playthrough highway scroll speed
///         (explicitly "just for this playthrough"), the drop-tuner pitch shift,
///         the backing-track mutes and the in-game rig. <c>EndRunSideEffects</c>
///         is the one place that undoes them, and
///         <c>PlayModeRunChainStateTests</c> already pins the paths that call it.
///         These are the paths that did not:
///     </para>
///     <list type="bullet">
///         <item><b>Esc on the results screen.</b> The screen's own "Song select"
///         button unwinds; <c>NavigateBack</c> was a bare screen assignment, so
///         the identical exit by keyboard carried the whole run into the editor
///         — and skipped the stats refresh, so the song row kept the old best.</item>
///         <item><b>The section breakdown's "Practice" link.</b> Its sibling
///         <c>OpenInEditor</c> unwinds first; this one dropped the player into
///         the chart editor with the run's scroll speed and pitch shift still
///         applied.</item>
///         <item><b>Abandoning a countdown.</b> A countdown is not always the
///         first thing that happens — Retry, Restart and retry-from-section all
///         count down INSIDE a run chain, with the previous run's side effects
///         live.</item>
///         <item><b>The drop-tuner restore itself</b>, which was keyed on
///         the drop-tuner switch — a pre-play control <c>PreparePrePlay</c> reloads
///         on every song pick, so the shift was stranded whenever the player
///         moved on before the run unwound.</item>
///     </list>
/// </summary>
public sealed class PlayModeRunUnwindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellingTheFirstCountdownPreservesTheEditorsSettings(bool byEscape)
    {
        var vm = NewVm(out var state, out var transport);
        transport.PlaybackSpeed = 0.85;
        state.HighwayScrollSpeed = 1.75;
        vm.CurrentScreen = PlayModeScreen.Countdown;

        if (byEscape) Assert.True(vm.TryCloseOverlay());
        else Assert.True(vm.NavigateBack());

        Assert.Equal(0.85, transport.PlaybackSpeed, 3);
        Assert.Equal(1.75, state.HighwayScrollSpeed, 3);
    }

    [Fact]
    public void EscOffTheResultsScreen_UnwindsTheRunLikeTheButtonDoes()
    {
        var vm = NewVm(out var state, out var transport);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 50;
        vm.RunScrollSpeed = 2.5;

        BeginGameplay(vm);
        Assert.Equal(0.5, transport.PlaybackSpeed, 3);

        // The run finished: the results screen is up with everything still applied.
        vm.CurrentScreen = PlayModeScreen.Results;
        Assert.True(vm.NavigateBack());

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    [Fact]
    public void TheSectionBreakdownsPracticeLink_UnwindsTheRunBeforeLeavingForTheEditor()
    {
        var vm = NewVm(out var state, out var transport);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 60;
        vm.RunScrollSpeed = 2.0;

        BeginGameplay(vm);
        vm.CurrentScreen = PlayModeScreen.Results;

        var left = false;
        vm.ExitToEditorRequested += () => left = true;
        vm.OpenSectionInEditorCommand.Execute(
            new SectionRow("Solo", "1:20", "62%", 9, true, 80_000) { Localization = FakeLocalization.English() });

        Assert.True(left, "the editor bridge must still leave for the editor");
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>Both ways off the countdown screen: Esc (which
    /// <c>TryCloseOverlay</c> claims before <c>NavigateBack</c> ever sees it)
    /// and the Back verb.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbandoningACountdownInsideARunChain_UnwindsTheRun(bool byEscape)
    {
        var vm = NewVm(out var state, out var transport);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 70;
        vm.RunScrollSpeed = 0.9;

        // Run one, then Restart from the pause menu: a countdown with the first
        // run's side effects still applied.
        BeginGameplay(vm);
        Assert.Equal(0.7, transport.PlaybackSpeed, 3);
        vm.CurrentScreen = PlayModeScreen.Countdown;

        if (byEscape) Assert.True(vm.TryCloseOverlay());
        else Assert.True(vm.NavigateBack());

        Assert.NotEqual(PlayModeScreen.Countdown, vm.CurrentScreen);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>
    ///     The drop-tuner shift is restored off what the RUN applied, not off the
    ///     <c>DropTuner.IsOn</c> switch. A refused run lands the player straight back
    ///     on the pre-play screen with the shift applied; picking another song
    ///     runs <c>PreparePrePlay</c>, which reloads the switch for that song — and the eventual
    ///     unwind then skipped the restore entirely, leaving the guitar input
    ///     permanently transposed.
    /// </summary>
    [Fact]
    public void TheDropTunerShift_IsRestoredEvenAfterThePlayerHasMovedOn()
    {
        var vm = NewVm(out _, out var transport);
        transport.InputPitchShiftSemis = 0;
        vm.SelectedFlowArrangement = new ArrangementOption(EbTunedArrangement(), FakeLocalization.English());
        vm.DropTuner.IsOn = true;

        BeginGameplay(vm);
        Assert.Equal(-1, transport.InputPitchShiftSemis);

        // PreparePrePlay on the next song pick — the flag the restore used to be
        // keyed on is now false while the shift is still applied.
        vm.DropTuner.IsOn = false;
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(0, transport.InputPitchShiftSemis);
    }

    /// <summary>…and a run that never applied a shift must not write one on the
    /// way out: the restore is a no-op, not an unconditional assignment.</summary>
    [Fact]
    public void ARunWithNoDropTuner_LeavesTheEditorsPitchShiftAlone()
    {
        var vm = NewVm(out _, out var transport);
        BeginGameplay(vm);
        // The editor's own shift, set while no run was applying one.
        transport.InputPitchShiftSemis = -3;
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(-3, transport.InputPitchShiftSemis);
    }

    /// <summary>The drop tuner only tunes DOWN (RigDropTuneViewModel), so a song
    /// tuned ABOVE standard is left alone — "applying" +1 used to hand the engine
    /// a shift it silently ignored while the run believed it was in tune.</summary>
    [Fact]
    public void ASongTunedAboveStandard_IsNotShiftedUp()
    {
        var vm = NewVm(out _, out var transport);
        transport.InputPitchShiftSemis = 0;
        vm.SelectedFlowArrangement = new ArrangementOption(TunedArrangement(+1), FakeLocalization.English());
        // The stored per-song opt-in: the switch itself refuses a path above standard.
        vm.DropTuner.UseDropTuner = true;
        Assert.False(vm.DropTuner.IsOn);

        BeginGameplay(vm);

        Assert.Equal(0, transport.InputPitchShiftSemis);
    }

    // ---- fixture -------------------------------------------------------------

    private static Arrangement EbTunedArrangement() => TunedArrangement(-1);

    private static Arrangement TunedArrangement(int semitones)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(semitones));
        return arrangement;
    }

    /// <summary>Same shape as <c>PlayModeRunChainStateTests</c>: a real transport
    /// and a real Live Play view model over fake audio, with no chart loaded — so
    /// Live Play REFUSES the run and every transport/highway side effect this
    /// class is about is left applied, which is exactly the state each exit path
    /// has to cope with.</summary>
    private PlayModeViewModel NewVm(out ProjectState state, out TransportViewModel transport)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var config = new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48_000 };
        state = new ProjectState();
        transport = new TransportViewModel(audio, state, config, FakeLocalization.KeysOnly());
        var livePlay = LivePlayTestFactory.Create(audio, state, new FakeAppLogger(), config, store);

        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, FakeLocalization.English(), config,
            livePlay: livePlay, transport: transport);
    }

    private static void BeginGameplay(PlayModeViewModel vm)
    {
        var method = typeof(PlayModeViewModel).GetMethod("BeginGameplay",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
