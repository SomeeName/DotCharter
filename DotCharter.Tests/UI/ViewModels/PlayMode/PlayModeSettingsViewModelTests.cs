using Avalonia.Input;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The Play Mode settings modal, <see cref="PlayModeSettingsViewModel" />
///     (#658): its load-on-open / persist-on-close cycle, its pure maps, how Play
///     Mode hosts it, and the one subscription it makes on shared state. The cases
///     that drive it THROUGH Play Mode (Esc via <c>TryCloseOverlay</c>, the mid-run
///     re-arm) build a real <see cref="PlayModeViewModel" />, because that wiring is
///     what the extraction could have broken.
/// </summary>
public sealed class PlayModeSettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayModeViewModel NewPlayMode(
        FakeConfigurationService config,
        ProjectState? state = null,
        PlayModeSettingsViewModel? settings = null)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state ?? new ProjectState(), FakeLocalization.English(), config,
            settings: settings);
    }

    [Fact]
    public void GameplaySettingsOverlay_LoadsOnOpen_PersistsOnClose_AndEscCloses()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.GameplayMetronomeEnabled = true;
        config.AppConfig.GameplayCountInBeats = 4;
        var vm = NewPlayMode(config);

        Assert.False(vm.Settings.IsGameplaySettingsOpen);

        // Opening the curated overlay loads the persisted AppConfig-backed values.
        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        Assert.True(vm.Settings.IsGameplaySettingsOpen);
        Assert.True(vm.Settings.GameplayMetronomeEnabled);
        // Rows are OFF / 2 / 4 / 8 beats since the §6.5 rebuild made count-in a
        // segmented control, so 4 beats is row 2.
        Assert.Equal(2, vm.Settings.GameplayCountInIndex);
        Assert.True(vm.Settings.IsCountIn4);

        // Esc during Play Mode routes through TryCloseOverlay, which must close
        // this overlay first (returning true) and persist the changes.
        vm.Settings.GameplayMetronomeEnabled = false;
        vm.Settings.GameplayCountInIndex = 3; // 8 beats
        Assert.True(vm.Settings.IsCountIn8);
        Assert.True(vm.TryCloseOverlay());
        Assert.False(vm.Settings.IsGameplaySettingsOpen);

        Assert.False(config.AppConfig.GameplayMetronomeEnabled);
        Assert.Equal(8, config.AppConfig.GameplayCountInBeats);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 144)]
    [InlineData(5, 240)]
    public void GameplayFpsMap_RoundTrips(int index, int value)
    {
        Assert.Equal(value, PlayModeSettingsViewModel.GameplayFpsIndexToValue(index));
        Assert.Equal(index, PlayModeSettingsViewModel.GameplayFpsValueToIndex(value));
    }

    [Fact]
    public void GameplayFpsMap_UnknownFallsBackToUnlimited()
    {
        Assert.Equal(0, PlayModeSettingsViewModel.GameplayFpsIndexToValue(99)); // unknown index -> unlimited
        Assert.Equal(0, PlayModeSettingsViewModel.GameplayFpsValueToIndex(75)); // unknown fps  -> index 0
    }

    [Fact]
    public void GameplaySettingsOverlay_LoadsAndPersists_PerfAppearanceAndGraphicsToggles()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.MaxFps = 60;
        config.AppConfig.RenderScalePercent = 150;
        config.AppConfig.UiScale = 1.5;
        var projectState = new ProjectState();
        var vm = NewPlayMode(config, projectState);

        // Open loads the persisted overlay-owned values. The UI scale is the
        // APPLIED factor from the process-wide UiScaleController, not
        // config.UiScale: under Auto the shell runs a derived number and the
        // persisted one is the manual arm's keepsake (see
        // PlayModeSettingsViewModel.OpenGameplaySettings), so the test seeds the
        // controller the way the shell would have, and puts it back afterwards.
        var priorScale = DotCharter.UI.Services.UiScaleController.Scale;
        var priorAuto = DotCharter.UI.Services.UiScaleController.IsAuto;
        try
        {
            DotCharter.UI.Services.UiScaleController.Set(1.5);
            vm.Settings.OpenGameplaySettingsCommand.Execute(null);
            Assert.Equal(2, vm.Settings.GameplayMaxFpsIndex);          // 60 -> index 2
            Assert.Equal(150.0, vm.Settings.GameplayRenderScalePercent);
            Assert.Equal(1.5, vm.Settings.GameplayUiScale);
        }
        finally
        {
            DotCharter.UI.Services.UiScaleController.Set(priorScale);
            DotCharter.UI.Services.UiScaleController.SetAutoMode(priorAuto);
        }

        // Change perf/appearance + flip a couple of the newly-surfaced graphics
        // toggles (bound live to ProjectState), then close.
        vm.Settings.GameplayMaxFpsIndex = 3;                         // 120
        vm.Settings.GameplayRenderScalePercent = 100;
        vm.Settings.GameplayUiScale = 1.0;
        projectState.EnableDepthFog = true;
        projectState.EnableCrowdBackground = true;
        Assert.True(vm.TryCloseOverlay());

        // Persisted to the SAME AppConfig fields Settings uses.
        Assert.Equal(120, config.AppConfig.MaxFps);
        Assert.Equal(100, config.AppConfig.RenderScalePercent);
        Assert.Equal(1.0, config.AppConfig.UiScale);
        Assert.True(config.AppConfig.EnableDepthFog);
        Assert.True(config.AppConfig.EnableCrowdBackground);
    }

    [Fact]
    public void GameplaySettingsOverlay_LoadsAndPersists_RockMeterToggle()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.GameplayRockMeterEnabled = true;
        var vm = NewPlayMode(config);

        // The mirrored-toggle flow: load on open …
        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        Assert.True(vm.Settings.GameplayRockMeterEnabled);

        // … persist on close (Esc path through TryCloseOverlay included).
        vm.Settings.GameplayRockMeterEnabled = false;
        Assert.True(vm.TryCloseOverlay());
        Assert.False(config.AppConfig.GameplayRockMeterEnabled);
    }

    [Fact]
    public void RockMeter_DefaultsOff()
    {
        var config = new FakeConfigurationService();
        Assert.False(config.AppConfig.GameplayRockMeterEnabled); // opt-in, default OFF
    }

    // ── How Play Mode hosts it ───────────────────────────────────────────────

    /// <summary>The instance DI hands Play Mode IS the one the views bind as
    /// <c>Settings</c> — not a second modal built beside it.</summary>
    [Fact]
    public void PlayMode_ExposesTheInjectedModal()
    {
        var config = new FakeConfigurationService();
        var state = new ProjectState();
        var settings = new PlayModeSettingsViewModel(state, FakeLocalization.English(), config);

        var vm = NewPlayMode(config, state, settings);

        Assert.Same(settings, vm.Settings);
    }

    /// <summary>"Full settings…" leaves the modal for the editor's Settings tab;
    /// only the shell knows where that is, so the request has to come out of
    /// Play Mode's own <c>OpenSettingsRequested</c>.</summary>
    [Fact]
    public void FullSettings_ClosesTheModal_AndReachesTheShellThroughPlayMode()
    {
        var vm = NewPlayMode(new FakeConfigurationService());
        var requests = 0;
        vm.OpenSettingsRequested += () => requests++;

        vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        vm.Settings.OpenFullSettingsCommand.Execute(null);

        Assert.False(vm.Settings.IsGameplaySettingsOpen);
        Assert.Equal(1, requests);
    }

    /// <summary>The Gameplay section's reset puts back two rows that are Play
    /// Mode's, through the host — so the reset reaches the properties the view
    /// binds and their own persistence, not a copy.</summary>
    [Fact]
    public void ResettingTheGameplaySection_ResetsPlayModesToneRows()
    {
        var config = new FakeConfigurationService();
        var vm = NewPlayMode(config);
        var defaults = new AppConfig();
        vm.UseInGameTones = !defaults.UseInGameTones;
        vm.ShowGameplayToneHud = !defaults.ShowGameplayToneHud;

        vm.Settings.ShowGameplaySettingsSectionCommand.Execute(((int)GameplaySettingsSectionId.Gameplay).ToString());
        vm.Settings.ResetGameplaySettingsSectionCommand.Execute(null);

        Assert.Equal(defaults.UseInGameTones, vm.UseInGameTones);
        Assert.Equal(defaults.ShowGameplayToneHud, vm.ShowGameplayToneHud);
        Assert.Equal(defaults.UseInGameTones, config.AppConfig.UseInGameTones);
    }

    /// <summary>Get Ready's input row and the modal's meter are ONE value off one
    /// tap: sampling with no host (no input) reads silence, never 0 dB.</summary>
    [Fact]
    public void TheInputMeter_WithNoInput_ReadsSilence()
    {
        var vm = NewPlayMode(new FakeConfigurationService());

        vm.Settings.SampleInputLevel();

        Assert.True(double.IsNegativeInfinity(vm.Settings.GameplayInputLevelDb));
        Assert.Equal(FakeLocalization.English()["PlayMode_GS_NoSignal"], vm.Settings.GameplayInputLevelText);
    }

    // ── The one subscription on shared state ─────────────────────────────────

    /// <summary>The control for the case below: a keybind changed while the modal
    /// is open is saved on close (the save hands the live table to the config).</summary>
    [Fact]
    public void AKeybindChangedWhileOpen_IsSavedOnClose()
    {
        var config = new FakeConfigurationService();
        var state = new ProjectState();
        var settings = new PlayModeSettingsViewModel(state, FakeLocalization.English(), config);

        settings.OpenGameplaySettingsCommand.Execute(null);
        state.Keybinds.PlayPause = new KeyChord(Key.P);
        settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.Same(state.Keybinds, config.Keybinds);
    }

    /// <summary>
    ///     The modal watches the live keybind table (a process-wide
    ///     <see cref="ProjectState" />) while it is open. Disposing it — the
    ///     container does, at shutdown and per headless dispatch — must drop that
    ///     subscription: a change made afterwards marks nothing, so nothing is
    ///     saved.
    /// </summary>
    [Fact]
    public void Dispose_DropsTheKeybindSubscription()
    {
        var config = new FakeConfigurationService();
        var state = new ProjectState();
        var settings = new PlayModeSettingsViewModel(state, FakeLocalization.English(), config);

        settings.OpenGameplaySettingsCommand.Execute(null);
        settings.Dispose();
        state.Keybinds.PlayPause = new KeyChord(Key.P);
        settings.CloseGameplaySettingsCommand.Execute(null);

        Assert.NotSame(state.Keybinds, config.Keybinds);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
