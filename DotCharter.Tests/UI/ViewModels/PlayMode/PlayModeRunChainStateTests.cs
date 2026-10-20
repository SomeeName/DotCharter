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
///     A RUN CHAIN — Retry from the results screen, Restart from the pause menu,
///     retry-from-section after a fail-out, or simply starting again after a
///     REFUSED run — reaches <c>BeginGameplay</c> a second time WITHOUT ever
///     passing through <c>EndRunSideEffects</c>.
///     <para>
///         The bug: <c>BeginGameplay</c> re-captured <c>_preRunSpeed</c>,
///         <c>_preRunScrollSpeed</c> and <c>_preRunPitchShift</c> on every entry.
///         On the second run those reads see the FIRST run's own applied values,
///         so the "before" state becomes the run state — and the eventual
///         <c>EndRunSideEffects</c> then "restores" the practice speed and the
///         per-playthrough highway scroll speed permanently onto the editor and
///         every later song. The comment on the scroll-speed line says exactly
///         why that must not happen ("just for this playthrough").
///     </para>
///     <para>
///         The class also covers the other half of the same seam: the two
///         per-RUN audio toggles the gameplay-settings overlay owns, which were
///         persisted on close and never re-armed onto the run the overlay was
///         opened over.
///     </para>
/// </summary>
public sealed class PlayModeRunChainStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ASecondRunInTheSameChain_DoesNotMakeTheRunsSpeedTheNewBaseline()
    {
        var vm = NewVm(out var state, out var transport, out _);

        // The state the editor was in before the player ever pressed play.
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;

        // The per-playthrough picks on the pre-song card.
        vm.PracticeSpeedPercent = 50;
        vm.RunScrollSpeed = 2.5;

        BeginGameplay(vm);
        Assert.Equal(0.5, transport.PlaybackSpeed, 3);
        Assert.Equal(2.5, state.HighwayScrollSpeed, 3);

        // Retry / Restart: a second run begins while the first one's side
        // effects are still applied, with no EndRunSideEffects in between.
        BeginGameplay(vm);

        // …and now the player leaves for Song Select, which unwinds the run.
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>The ordinary single run must be unaffected: one
    /// <c>BeginGameplay</c>, one unwind, everything back.</summary>
    [Fact]
    public void ASingleRun_StillRestoresEverythingItApplied()
    {
        var vm = NewVm(out var state, out var transport, out _);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 70;
        vm.RunScrollSpeed = 0.9;

        BeginGameplay(vm);
        Assert.Equal(0.7, transport.PlaybackSpeed, 3);
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>…and a SECOND, unrelated chain still takes a fresh baseline:
    /// the flag must be cleared on the way out, not latched for the session.</summary>
    [Fact]
    public void AFreshChain_TakesAFreshBaseline()
    {
        var vm = NewVm(out _, out var transport, out _);
        transport.PlaybackSpeed = 1.0;
        vm.PracticeSpeedPercent = 50;
        BeginGameplay(vm);
        vm.BackToSongSelectCommand.Execute(null);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);

        // The player then changes the editor speed and plays again.
        transport.PlaybackSpeed = 0.8;
        vm.PracticeSpeedPercent = 60;
        BeginGameplay(vm);
        Assert.Equal(0.6, transport.PlaybackSpeed, 3);
        vm.BackToSongSelectCommand.Execute(null);
        Assert.Equal(0.8, transport.PlaybackSpeed, 3);
    }

    /// <summary>
    ///     "End run" from the pause menu has two branches. The one that ends a
    ///     LIVE session lets <c>OnSessionCompleted</c> unwind the run; the other
    ///     — reached when the session is already gone (the editor's own Live Play
    ///     toggle, an engine teardown) — walked out to Song Select leaving every
    ///     side effect applied, unlike its sibling <c>AbortRun</c>.
    /// </summary>
    [Fact]
    public void EndingARunWhoseSessionIsAlreadyGone_StillUnwindsTheRun()
    {
        var vm = NewVm(out var state, out var transport, out _);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 50;
        vm.RunScrollSpeed = 2.5;

        BeginGameplay(vm);           // Live Play refuses: no session, effects applied
        vm.CurrentScreen = PlayModeScreen.Paused;
        vm.EndRunFromPauseCommand.Execute(null);

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>Resume and Riff Repeater carry the identical branch, and it
    /// means the identical thing: there is no run left, so this is an exit.</summary>
    [Theory]
    [InlineData("ResumeGameplayCommand")]
    [InlineData("OpenRiffRepeaterCommand")]
    public void EveryPauseMenuVerbThatFindsNoSession_UnwindsTheRun(string command)
    {
        var vm = NewVm(out var state, out var transport, out _);
        transport.PlaybackSpeed = 1.0;
        state.HighwayScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;
        vm.PracticeSpeedPercent = 60;
        vm.RunScrollSpeed = 2.0;

        BeginGameplay(vm);
        vm.CurrentScreen = PlayModeScreen.Paused;
        var property = typeof(PlayModeViewModel).GetProperty(command);
        Assert.True(property != null, $"PlayModeViewModel has no {command} — this test was renamed out of its job");
        ((System.Windows.Input.ICommand)property!.GetValue(vm)!).Execute(null);

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
        Assert.Equal(HighwaySpeedPolicy.DefaultScrollSpeed, state.HighwayScrollSpeed, 3);
    }

    /// <summary>
    ///     The gameplay-settings overlay opens from the PAUSE menu so settings can
    ///     be changed mid-song. The metronome and the note claps are armed once, in
    ///     <c>BeginGameplay</c>, off AppConfig — so persisting them on close was the
    ///     whole of the effect, and a mid-run flip did nothing until the NEXT run.
    /// </summary>
    [Fact]
    public void FlippingTheMetronomeMidRun_ArmsItForTheRunTheOverlayWasOpenedOver()
    {
        var vm = NewVm(out _, out var transport, out var config);
        config.AppConfig.GameplayMetronomeEnabled = false;
        config.AppConfig.GameplayClapsEnabled = false;

        BeginGameplay(vm);
        Assert.False(transport.GameplayMetronomeActive);

        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        vm.Settings.GameplayMetronomeEnabled = true;
        vm.Settings.GameplayClapsEnabled = true;
        vm.Settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.True(config.AppConfig.GameplayMetronomeEnabled, "the flip must still persist");
        Assert.True(transport.GameplayMetronomeActive,
            "the run the overlay was opened over never heard the metronome the player just switched on");
        Assert.True(transport.GameplayClapsActive);
    }

    /// <summary>…and OUTSIDE a run it must not touch the transport at all: the
    /// coordinator ORs this flag with the EDITOR's own metronome toggle, which
    /// this overlay does not own.</summary>
    [Fact]
    public void FlippingTheMetronomeWithNoRun_LeavesTheEditorsTransportAlone()
    {
        var vm = NewVm(out _, out var transport, out var config);

        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        vm.Settings.GameplayMetronomeEnabled = true;
        vm.Settings.GameplayClapsEnabled = true;
        vm.Settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.True(config.AppConfig.GameplayMetronomeEnabled);
        Assert.False(transport.GameplayMetronomeActive);
        Assert.False(transport.GameplayClapsActive);
    }

    // ---- fixture -------------------------------------------------------------

    /// <summary>A Play Mode view model over a real transport and a real Live
    /// Play view model with fake audio. There is no chart loaded, so Live Play
    /// REFUSES the run — which is itself one of the four ways into a run chain,
    /// and leaves every transport/highway side effect this test is about
    /// already applied.</summary>
    private PlayModeViewModel NewVm(out ProjectState state, out TransportViewModel transport,
        out FakeConfigurationService config)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        config = new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48_000 };
        state = new ProjectState();
        transport = new TransportViewModel(audio, state, config, FakeLocalization.KeysOnly());
        var livePlay = LivePlayTestFactory.Create(audio, state, new FakeAppLogger(), config, store);

        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, FakeLocalization.English(), config,
            livePlay: livePlay, transport: transport);
    }

    /// <summary>Drives the private run start. It is private because nothing but
    /// the countdown tick may call it in the app; the countdown is a
    /// <c>DispatcherTimer</c>, which never fires on a plain test thread.</summary>
    private static void BeginGameplay(PlayModeViewModel vm)
    {
        var method = typeof(PlayModeViewModel).GetMethod("BeginGameplay",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
