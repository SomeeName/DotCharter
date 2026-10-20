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
///     Who owns <c>PracticeSpeedPercent</c>, and for how long.
///     <para>
///         It belongs to the PLAYER: a run's unwind restores the TRANSPORT,
///         never the picker. (The two run-scoped overrides that used to write
///         it — the smart-practice drill and a LAN host's speed — were removed
///         in 2026-09 with their stash-and-restore.)
///     </para>
///     <para>
///         The other half is the opposite failure. The gameplay-settings overlay
///         opens from the PAUSE menu so the song can be slowed mid-run, and its
///         Speed row binds this same property — but the only code that reached
///         the transport was <c>BeginGameplay</c>, so a mid-run change did
///         nothing at all until the next run. That is the identical bug the
///         metronome row had (see
///         <c>PlayModeRunChainStateTests.FlippingTheMetronomeMidRun_…</c>) and it
///         is fixed in the identical place.
///     </para>
/// </summary>
public sealed class PracticeSpeedOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>The player's OWN pick is never touched by an unwind, so a plain
    /// run leaves the picker where they set it.</summary>
    [Fact]
    public void ThePlayersOwnPick_SurvivesTheRun()
    {
        var vm = NewVm(out _);
        vm.PracticeSpeedPercent = 80;

        BeginGameplay(vm);
        vm.BackToSongSelectCommand.Execute(null);

        Assert.Equal(80, vm.PracticeSpeedPercent);
    }

    [Fact]
    public void ChangingTheSpeedMidRun_AppliesToTheRunTheOverlayWasOpenedOver()
    {
        var vm = NewVm(out var transport);
        transport.PlaybackSpeed = 1.0;
        vm.PracticeSpeedPercent = 100;

        BeginGameplay(vm);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);

        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        vm.PracticeSpeedPercent = 50;
        vm.Settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.Equal(0.5, transport.PlaybackSpeed, 3);

        // …and the run's own baseline is untouched, so the unwind still gives
        // the editor its speed back.
        vm.BackToSongSelectCommand.Execute(null);
        Assert.Equal(1.0, transport.PlaybackSpeed, 3);
    }

    /// <summary>Outside a run the overlay must not touch the transport: that
    /// speed belongs to the editor, not to Play Mode.</summary>
    [Fact]
    public void ChangingTheSpeedWithNoRun_LeavesTheEditorsTransportAlone()
    {
        var vm = NewVm(out var transport);
        transport.PlaybackSpeed = 0.75;

        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        vm.PracticeSpeedPercent = 50;
        vm.Settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.Equal(0.75, transport.PlaybackSpeed, 3);
    }

    // ---- fixture -------------------------------------------------------------

    private PlayModeViewModel NewVm(out TransportViewModel transport)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var config = new FakeConfigurationService();
        var audio = new FakeAudioPlaybackService { InputSampleRate = 48_000 };
        var state = new ProjectState();
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
