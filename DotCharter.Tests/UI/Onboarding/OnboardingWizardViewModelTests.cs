using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Onboarding;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The first-run wizard's side effects, driven through the product's own
///     commit paths over in-memory fakes (<see cref="OnboardingWizardRig"/>):
///     the persisted-complete flag on Finish AND on dismiss, the startup hook that
///     honours it, the role choice landing in <c>AppConfig.StartupScreen</c>, Skip
///     committing nothing, the library scan count, the seat-0 tone apply, the
///     profile create/switch, and the re-run starting clean.
/// </summary>
/// <remarks>Joins the serialized culture collection: the rig drives
/// <c>OnboardingWizardViewModel.CommitWelcome</c>, which writes the
/// process-global <c>LocalizationService.Instance</c> through
/// <c>SetConfiguredCulture</c>. A culture write through PRODUCT code races
/// exactly as a direct one does — see <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingWizardViewModelTests
{
    [Fact]
    public void OpensAtStartup_OnlyWhileNotCompleted()
    {
        var rig = new OnboardingWizardRig();
        Assert.False(rig.Config.AppConfig.OnboardingCompleted);

        Assert.True(rig.Vm.OpenAtStartupIfNeeded());
        Assert.True(rig.Vm.IsOpen);
        Assert.Equal(OnboardingStep.Welcome, rig.Vm.Step);

        rig.Vm.Finish();
        Assert.False(rig.Vm.IsOpen);

        Assert.False(rig.Vm.OpenAtStartupIfNeeded(), "a completed onboarding must never open on its own again");
        Assert.False(rig.Vm.IsOpen);
    }

    [Fact]
    public void Finish_PersistsTheFlagAndTheVersion()
    {
        var rig = new OnboardingWizardRig(appVersion: "1.2.3");
        rig.Vm.Open();
        rig.Vm.Finish();

        Assert.True(rig.Config.AppConfig.OnboardingCompleted);
        Assert.Equal("1.2.3", rig.Config.AppConfig.OnboardingCompletedVersion);
    }

    [Fact]
    public void Dismiss_AlsoCountsAsCompleted_SoTheWizardNeverNags()
    {
        var rig = new OnboardingWizardRig(appVersion: "4.5.6");
        rig.Vm.Open();
        rig.Vm.Next();            // on Library
        rig.Vm.RequestClose();                       // Esc / "Skip setup"

        Assert.False(rig.Vm.IsOpen);
        Assert.True(rig.Config.AppConfig.OnboardingCompleted);
        Assert.Equal("4.5.6", rig.Config.AppConfig.OnboardingCompletedVersion);
        Assert.False(rig.Vm.OpenAtStartupIfNeeded());
    }

    [Fact]
    public void AnInstallSetUpBeforeTheWizardExisted_IsGrandfathered_NotGreeted()
    {
        // The flag is new; an upgrade must not show a configured user the
        // first-run flow. A library path or a calibrated device is the evidence.
        var rig = new OnboardingWizardRig(appVersion: "0.49.0");
        rig.Config.AppConfig.PsarcLibraryPath = @"D:\cdlc";

        Assert.False(rig.Vm.OpenAtStartupIfNeeded());
        Assert.False(rig.Vm.IsOpen);
        Assert.True(rig.Config.AppConfig.OnboardingCompleted, "grandfathering must be recorded, not re-decided every launch");
        Assert.Equal("0.49.0", rig.Config.AppConfig.OnboardingCompletedVersion);

        var calibrated = new OnboardingWizardRig();
        calibrated.Config.AppConfig.LivePlayLatencyOffsetsByDevice["ASIO Focusrite"] = 4.2;
        Assert.False(calibrated.Vm.OpenAtStartupIfNeeded());

        // Run setup again still works for a grandfathered install.
        rig.Vm.Open();
        Assert.True(rig.Vm.IsOpen);
    }

    [Fact]
    public void RequestClose_WhileClosed_IsANoOp_AndWritesNothing()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.RequestClose();
        Assert.False(rig.Config.AppConfig.OnboardingCompleted);
    }

    [Theory]
    [InlineData(true, StartupScreen.Editor)]
    [InlineData(false, StartupScreen.PlayMode)]
    public void RoleChoice_WritesStartupScreen_CharterToEditor_PlayerToPlayMode(bool charter, StartupScreen expected)
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Vm.IsCharter = charter;
        Assert.Equal(!charter, rig.Vm.IsPlayer);

        rig.Vm.Next();            // commits Welcome

        Assert.Equal(expected, rig.Config.AppConfig.StartupScreen);
        // …and the back-compat alias the Play Mode menu's toggle still writes agrees.
        Assert.Equal(expected == StartupScreen.PlayMode, rig.Config.AppConfig.StartInPlayMode);
    }

    [Fact]
    public void Open_SeedsTheRoleFromTheCurrentStartupScreen()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.StartupScreen = StartupScreen.Editor;
        rig.Vm.Open();
        Assert.True(rig.Vm.IsCharter);

        rig.Config.AppConfig.StartupScreen = StartupScreen.PlayMode;
        rig.Vm.Open();
        Assert.True(rig.Vm.IsPlayer);
    }

    [Fact]
    public void Welcome_CommitsTheUpdateOptIn()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.UpdateCheckOnStartup = true;
        rig.Vm.Open();
        rig.Vm.UpdateCheckOnStartup = false;
        rig.Vm.Next();
        Assert.False(rig.Config.AppConfig.UpdateCheckOnStartup);
    }

    [Fact]
    public void Skip_AdvancesWithoutCommitting()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.StartupScreen = StartupScreen.PlayMode;
        rig.Vm.Open();
        rig.Vm.IsCharter = true;                     // would become Editor on Next…

        rig.Vm.SkipCommand.Execute(null);            // …but Welcome is skipped

        Assert.Equal(OnboardingStep.Library, rig.Vm.Step);
        Assert.Equal(StartupScreen.PlayMode, rig.Config.AppConfig.StartupScreen);
        Assert.Contains(OnboardingStep.Welcome, rig.Vm.SkippedSteps);
    }

    [Fact]
    public void Back_ReturnsAPage_AndTheHeadCaptionFollows()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        Assert.False(rig.Vm.CanGoBack);
        Assert.Contains("1", rig.Vm.StepCaption);

        rig.Vm.Next();
        Assert.True(rig.Vm.CanGoBack);
        Assert.Contains("2", rig.Vm.StepCaption);

        rig.Vm.BackCommand.Execute(null);
        Assert.Equal(OnboardingStep.Welcome, rig.Vm.Step);
        Assert.False(rig.Vm.CanGoBack);
    }

    [Fact]
    public void WalkingEveryPage_EndsOnDone_WithFinishAsThePrimary_AndASummary()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        Assert.Equal(LocalizationService.Instance["LatencyWizard_Next"], rig.Vm.PrimaryCaption);

        for (var i = 0; i < OnboardingStepMachine.Order.Count - 1; i++)
            rig.Vm.PrimaryCommand.Execute(null);      // the footer button = Next until Done

        Assert.Equal(OnboardingStep.Done, rig.Vm.Step);
        Assert.True(rig.Vm.IsDone);
        Assert.False(rig.Vm.CanSkipStep);
        Assert.Equal(LocalizationService.Instance["Onboarding_Finish"], rig.Vm.PrimaryCaption);
        Assert.Contains(rig.Profiles.ActiveProfile.Name, rig.Vm.Summary);
        Assert.Contains(LocalizationService.Instance["Onboarding_Summary_Profile"], rig.Vm.Summary);
        Assert.True(rig.Vm.IsOpen, "Done is a page, not the close — Finish closes");

        rig.Vm.PrimaryCommand.Execute(null);          // the footer button on Done
        Assert.False(rig.Vm.IsOpen);
        Assert.True(rig.Config.AppConfig.OnboardingCompleted);
    }

    [Fact]
    public async Task Library_BrowsePersistsTheFolderAndReportsTheScanCount()
    {
        var rig = new OnboardingWizardRig();
        rig.Library.SongsToReport = 412;
        rig.Storage.NextFolder = @"D:\cdlc";
        rig.Vm.Open();
        rig.Vm.Next();            // Library
        Assert.False(rig.Vm.HasLibraryPath);

        await rig.Vm.BrowseLibraryCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\cdlc", rig.Config.AppConfig.PsarcLibraryPath);
        Assert.Equal(@"D:\cdlc", rig.Vm.LibraryPath);
        Assert.Equal([@"D:\cdlc"], rig.Library.ScannedPaths);
        Assert.Equal(412, rig.Vm.LibrarySongCount);
        Assert.Contains("412", rig.Vm.LibraryStatus);
        Assert.False(rig.Vm.IsScanningLibrary);
    }

    [Fact]
    public async Task Library_CancelledPicker_ChangesNothing()
    {
        var rig = new OnboardingWizardRig();
        rig.Storage.NextFolder = null;
        rig.Vm.Open();
        rig.Vm.Next();
        await rig.Vm.BrowseLibraryCommand.ExecuteAsync(null);
        Assert.Empty(rig.Library.ScannedPaths);
        Assert.Equal(string.Empty, rig.Config.AppConfig.PsarcLibraryPath);
    }

    [Fact]
    public async Task Library_AFailedScan_IsReportedNotSwallowed()
    {
        var rig = new OnboardingWizardRig();
        rig.Library.ThrowOnScan = new IOException("share offline");
        rig.Config.AppConfig.PsarcLibraryPath = @"\\nas\cdlc";
        rig.Vm.Open();
        rig.Vm.Next();            // Library: an existing path scans on entry
        await Task.Yield();

        Assert.Contains("share offline", rig.Vm.LibraryStatus);
        Assert.Equal(0, rig.Vm.LibrarySongCount);
    }

    [Fact]
    public void Audio_TestClick_GoesThroughTheEngineAfterTheSharedSave()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(2);           // Library, then Audio (opens the shared audio page)
        Assert.True(rig.Vm.IsAudio);

        rig.Vm.TestAudioCommand.Execute(null);

        Assert.Contains(2, rig.Engine.ImmediateClicks);   // 2 == PlayClap on the fake
        Assert.False(rig.Vm.AudioTestFailed);
        Assert.True(rig.Vm.HasAudioTestStatus);
    }

    [Fact]
    public void Audio_Next_CommitsThroughTheSettingsPagesSave()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioSampleRate = 44100;
        rig.Vm.Open();
        rig.Advance(2);           // Library, then Audio
        rig.AudioSettings.AutoFollowDeviceSettings = false;
        rig.AudioSettings.SampleRate = 48000;

        rig.Advance();            // Input — Audio committed

        Assert.Equal(48000, rig.Config.AppConfig.AudioSampleRate);
    }

    [Fact]
    public void Input_MeterReadsTheEnginesCaptureWindow()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        Assert.False(rig.Vm.HasInputSignal);

        rig.Engine.InputCaptureWindow = [0f, 0.5f, -0.25f];
        rig.Vm.PollInputMeter();

        Assert.True(rig.Vm.HasInputSignal);
        Assert.Equal(20.0 * Math.Log10(0.5), rig.Vm.InputLevelDb, 6);
        Assert.InRange(rig.Vm.InputLevelFraction, 0.85, 0.95);

        rig.Engine.InputCaptureWindow = [];
        rig.Vm.PollInputMeter();
        Assert.False(rig.Vm.HasInputSignal);
        Assert.Equal(0.0, rig.Vm.InputLevelFraction);
    }

    [Fact]
    public void Tone_PickingAPreset_AppliesItToSeatZero()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(4);   // Tone
        Assert.True(rig.Vm.IsTone);
        Assert.NotEmpty(rig.Vm.Tones);

        rig.Vm.SelectedTone = rig.Vm.Tones[1];

        Assert.Equal([(0, rig.Vm.Tones[1].Preset.Id)], rig.Presets.AppliedPerPlayer);
        Assert.Contains(rig.Vm.Tones[1].Name, rig.Vm.ToneStatus);
    }

    /// <summary>
    ///     0.49 shipped the pick as a bare service call: the chain changed and an
    ///     Amp &amp; FX board that was already open kept showing the cards it had
    ///     before the wizard. The pick now goes through the board's own preset
    ///     browser — the card-click path — so the board re-syncs from the chain
    ///     and names the tone active. Proven by making the board STALE first
    ///     (a node added behind its back) and watching the pick catch it up.
    /// </summary>
    [Fact]
    public void Tone_PickingAPreset_ResyncsAnAlreadyOpenPedalboard()
    {
        var catalog = new[] { new DspModuleType("amp", "amp", new[] { new DspParamInfo("p", "p", 0f, 1f, 0f) }) };
        var chain = new FakeDspChainService(catalog);
        PedalboardViewModel? board = null;
        var rig = new OnboardingWizardRig(pedalboard: () => board);
        board = TestRigBuilders.Pedalboard(chain, rig.Presets, FakeLocalization.English());
        Assert.Empty(board.Nodes);

        // The board is now behind the chain — exactly what an apply the board
        // did not see leaves behind.
        chain.AddNode("amp");
        Assert.Empty(board.Nodes);

        rig.Vm.Open();
        rig.Advance(4);   // Tone
        rig.Vm.SelectedTone = rig.Vm.Tones[1];

        Assert.Equal([(0, rig.Vm.Tones[1].Preset.Id)], rig.Presets.AppliedPerPlayer);
        Assert.Equal(["amp"], board.Nodes.Select(n => n.ModuleTypeId));
        Assert.Equal(rig.Vm.Tones[1].Name, board.Browser.ActivePresetName);
    }

    [Fact]
    public void Latency_ShowsTheCurrentNumbers_AndOpensTheExistingWizardOnTop()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.LivePlayLatencyOffsetMs = 12.34;
        rig.Config.AppConfig.VisualLeadMs = 5.6;
        rig.Vm.Open();
        rig.Advance(5);   // Latency
        Assert.True(rig.Vm.IsLatency);
        Assert.Contains(12.3.ToString(System.Globalization.CultureInfo.CurrentCulture), rig.Vm.LatencySummary);
        Assert.Contains(5.6.ToString(System.Globalization.CultureInfo.CurrentCulture), rig.Vm.LatencySummary);

        rig.Vm.CalibrateLatencyCommand.Execute(null);
        Assert.Equal(1, rig.Latency.Opens);
        Assert.True(rig.Vm.IsOpen, "the calibration wizard sits ON TOP; onboarding stays open beneath");
    }

    /// <summary>
    ///     The first run greets the player as "Player 1", and the 0.49 wizard's
    ///     only control was Create — so being called by name meant a SECOND
    ///     profile beside the untouched default (owner report, 2026-08-30). The
    ///     page now edits the selected profile's name in place.
    /// </summary>
    [Fact]
    public void Profile_RenamesTheInitialProfileInPlace_NoSecondProfile()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile
        Assert.True(rig.Vm.IsProfile);
        Assert.Equal("Default", rig.Vm.ProfileName);
        Assert.False(rig.Vm.RenameProfileCommand.CanExecute(null), "an unchanged name is nothing to apply");

        rig.Vm.ProfileName = "  Alex ";
        Assert.True(rig.Vm.RenameProfileCommand.CanExecute(null));
        rig.Vm.RenameProfileCommand.Execute(null);

        Assert.Equal("Alex", rig.Profiles.ActiveProfile.Name);
        Assert.Single(rig.Profiles.Profiles);
        Assert.Equal("Alex", rig.Vm.SelectedProfile?.Name);
        Assert.Contains("Alex", rig.Vm.ProfileStatus);
        Assert.False(rig.Vm.RenameProfileCommand.CanExecute(null));
    }

    /// <summary>
    ///     …and the second half of the same defect: typing the name that is
    ///     ALREADY there and pressing Create made the second profile anyway.
    ///     Two players called the same thing are indistinguishable everywhere a
    ///     profile is shown by name (the picker, the local leaderboard rows, the
    ///     co-op roster) while their scores, bests and practice history stay split
    ///     across two ids — the exact outcome the rename affordance was added to
    ///     prevent. Create is refused and the page says why.
    /// </summary>
    [Fact]
    public void Profile_ADuplicateName_IsRefusedAndSaidOutLoud()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile

        rig.Vm.NewProfileName = "  default  ";        // trimmed + case-insensitive

        Assert.False(rig.Vm.CanCreateProfile);
        Assert.False(rig.Vm.CreateProfileCommand.CanExecute(null));
        Assert.Contains("default", rig.Vm.ProfileStatus, StringComparison.OrdinalIgnoreCase);

        rig.Vm.CreateProfileCommand.Execute(null);
        Assert.Single(rig.Profiles.Profiles);

        // A free name still creates, so the guard is a check and not a new refusal.
        rig.Vm.NewProfileName = "Ana";
        Assert.True(rig.Vm.CanCreateProfile);
        rig.Vm.CreateProfileCommand.Execute(null);
        Assert.Equal(2, rig.Profiles.Profiles.Count);
    }

    /// <summary>Renaming onto ANOTHER player's name is the same collision, so it
    /// is refused the same way — while renaming to a different casing of your
    /// OWN name still applies.</summary>
    [Fact]
    public void Profile_RenamingOntoAnotherPlayersName_IsRefused()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile
        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);    // Ana is now active

        rig.Vm.ProfileName = "Default";
        Assert.False(rig.Vm.CanRenameProfile);

        rig.Vm.ProfileName = "ANA";                   // your own name, restyled
        Assert.True(rig.Vm.CanRenameProfile);
        rig.Vm.RenameProfileCommand.Execute(null);
        Assert.Equal("ANA", rig.Profiles.ActiveProfile.Name);
    }

    /// <summary>
    ///     A name typed and abandoned does not survive into the NEXT run of the
    ///     wizard. <c>Open</c> resets every other page's draft state, and
    ///     <c>RefreshProfiles</c> used to leave <c>ProfileName</c> alone because
    ///     re-selecting the same <c>PlayerProfile</c> instance raises no change —
    ///     so "Run setup again" arrived on the profile page with the previous
    ///     run's abandoned draft in the box, and the next Next() committed it as
    ///     a rename.
    /// </summary>
    [Fact]
    public void Profile_AnAbandonedNameDraft_DoesNotSurviveARerun()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile
        rig.Vm.ProfileName = "Typed then abandoned";
        rig.Vm.CancelCommand.Execute(null);

        rig.Vm.Open();                                // "Run setup again"
        rig.Advance(6);   // Profile

        Assert.Equal("Default", rig.Vm.ProfileName);
        Assert.False(rig.Vm.CanRenameProfile);

        rig.Vm.Next();                                // Done
        Assert.Equal("Default", rig.Profiles.ActiveProfile.Name);
    }

    [Fact]
    public void Profile_NameTypedButNotApplied_IsCommittedByNext()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile
        rig.Vm.ProfileName = "Ana";
        rig.Vm.Next();                                // Done

        Assert.True(rig.Vm.IsDone);
        Assert.Equal("Ana", rig.Profiles.ActiveProfile.Name);
        Assert.Single(rig.Profiles.Profiles);
        Assert.Contains("Ana", rig.Vm.Summary);
    }

    /// <summary>
    ///     The tone rig follows the profile, and a profile made here has no rig:
    ///     the switch cleared the starter tone picked two pages earlier (owner
    ///     report, 2026-08-30). The pick is re-applied to the new profile.
    /// </summary>
    [Fact]
    public void Profile_CreatingAProfile_ReappliesThePickedStarterTone()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(4);   // Tone
        rig.Vm.SelectedTone = rig.Vm.Tones[1];
        rig.Advance(2);   // Profile
        Assert.True(rig.Vm.IsProfile);
        rig.Presets.AppliedPerPlayer.Clear();

        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);

        Assert.Equal("Ana", rig.Profiles.ActiveProfile.Name);
        Assert.Equal([(0, rig.Vm.Tones[1].Preset.Id)], rig.Presets.AppliedPerPlayer);
    }

    [Fact]
    public void Profile_CreatingAProfile_WithNoPick_AppliesTheShippedDefaultTone()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(4);   // Tone
        rig.Config.AppConfig.DefaultTonePresetId = rig.Vm.Tones[0].Preset.Id;
        rig.Advance(2);   // Profile
        rig.Presets.AppliedPerPlayer.Clear();

        rig.Vm.NewProfileName = "Ana";
        rig.Vm.CreateProfileCommand.Execute(null);

        Assert.Equal([(0, rig.Vm.Tones[0].Preset.Id)], rig.Presets.AppliedPerPlayer);
    }

    [Fact]
    public void Profile_CreateMakesTheNewProfileActive_AndPickingSwitches()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Advance(6);   // Profile
        Assert.True(rig.Vm.IsProfile);
        Assert.Single(rig.Vm.Profiles);
        Assert.False(rig.Vm.CreateProfileCommand.CanExecute(null));

        rig.Vm.NewProfileName = "  Ana ";
        Assert.True(rig.Vm.CreateProfileCommand.CanExecute(null));
        rig.Vm.CreateProfileCommand.Execute(null);

        Assert.Equal("Ana", rig.Profiles.ActiveProfile.Name);
        Assert.Equal(2, rig.Vm.Profiles.Count);
        Assert.Equal("Ana", rig.Vm.SelectedProfile?.Name);
        Assert.Equal(string.Empty, rig.Vm.NewProfileName);

        rig.Vm.SelectedProfile = rig.Vm.Profiles[0];
        Assert.Equal("Default", rig.Profiles.ActiveProfile.Name);
    }

    [Fact]
    public void RunAgain_StartsCleanOnWelcome()
    {
        var rig = new OnboardingWizardRig();
        rig.Vm.Open();
        rig.Vm.SkipCommand.Execute(null);
        rig.Vm.SkipCommand.Execute(null);
        rig.Vm.Finish();          // completed with two skips

        rig.Vm.Open();                               // "Run setup again"

        Assert.True(rig.Vm.IsOpen);
        Assert.Equal(OnboardingStep.Welcome, rig.Vm.Step);
        Assert.Empty(rig.Vm.SkippedSteps);
        Assert.True(rig.Config.AppConfig.OnboardingCompleted, "re-running does not un-complete");
    }

    /// <summary>#288: the first page's system check starts when the wizard OPENS — never when
    /// it is constructed at startup — runs once however often the wizard reopens, and its
    /// answer is what the page binds.</summary>
    [Fact]
    public async Task Open_StartsTheSystemCheck_Once_AndThePageShowsItsAnswer()
    {
        var rig = new OnboardingWizardRig(engineProbe: new FakeNativeEngineProbe(new NativeEngineCheck(NativeEngineState.Missing)));
        Assert.Equal(0, rig.EngineProbe.Calls);

        rig.Vm.Open();
        await rig.SystemCheck.Run;

        Assert.Same(rig.SystemCheck, rig.Vm.SystemCheck);
        Assert.True(rig.Vm.SystemCheck.HasProblem);
        Assert.True(rig.Vm.SystemCheck.Capabilities[0].IsUnavailable);

        rig.Vm.Open();
        await rig.SystemCheck.Run;
        Assert.Equal(1, rig.EngineProbe.Calls);
        Assert.Equal(1, rig.GpuProbe.Calls);
    }
}
