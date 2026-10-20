using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Updates as a screen: its startup setting round-trips through the
///     config and counts as unsaved, a manual check reports through the flow
///     and forgets a stale find on up-to-date, a staged payload shows the
///     recovery strip, and the GitHub block signs in and out through the seams.
/// </summary>
public sealed class UpdatesSettingsViewModelTests
{
    private sealed class Rig
    {
        public StubUpdateService Service = new(UpdateCheckStatus.UpToDate);
        public StubUpdatePrompt Prompt = new();
        public FakeConfigurationService Config = new();
        public StubGitHubAuth Auth = new();
        public StubShellLauncher Shell = new();
        public StubClipboard Clipboard = new();
        public StubApplicationCloser Closer = new();
        public FakeAppLogger Logger = new();
        public IUpdateFeed Feed = SignInFeed;

        public UpdateCheckFlow Flow => new(Service, Prompt, Config, FakeLocalization.English(), Logger,
            new TestUiDispatcher(), new ProjectState(), Closer);

        public GitHubSignInViewModel GitHub => new(Auth, FakeLocalization.English(), new TestUiDispatcher(),
            [Feed], Logger, Shell, Clipboard);

        public UpdatesSettingsViewModel Vm() => new(Flow, Config, FakeLocalization.English(), GitHub);
    }

    private static readonly ILocalizationService Loc = FakeLocalization.English();

    /// <summary>A private feed, as a plugin registers it: sign-in required, its own client id.</summary>
    private static readonly UpdateFeed SignInFeed = new("example-owner", "example-addon", RequiresSignIn: true,
        SignInClientId: "Iv23.example");

    [Fact]
    public void ItIsTheUpdatesTab_AndConformsToItsContract()
    {
        var vm = new Rig().Vm();
        Assert.Equal(SettingsTabKeys.Updates, vm.Key);
        Assert.Equal(typeof(IUpdatesSettingsScreen), vm.Screen);
        Assert.Equal("1.2.3", vm.CurrentVersionText);
        Assert.Empty(ContractConformanceTests.Violations(typeof(IUpdatesSettingsScreen), vm));
    }

    [Fact]
    public void Load_ReadsTheStartupSettingFromConfig_AndReadsZeroUnsaved()
    {
        var rig = new Rig();
        rig.Config.AppConfig.UpdateCheckOnStartup = false;
        var vm = rig.Vm();

        vm.Load();

        Assert.False(vm.UpdateCheckOnStartup);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void EditingASetting_CountsAsUnsaved_AndSaveWritesItBack()
    {
        var rig = new Rig();
        var vm = rig.Vm();
        vm.Load();

        vm.UpdateCheckOnStartup = false;
        Assert.Equal(["updates.UpdateCheckOnStartup"], vm.UnsavedSettings);

        vm.Save();

        Assert.False(rig.Config.AppConfig.UpdateCheckOnStartup);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void Revert_PutsThePersistedValuesBack_AndCancelsADeviceFlow()
    {
        var rig = new Rig();
        var vm = rig.Vm();
        vm.Load();
        vm.UpdateCheckOnStartup = false;

        vm.Revert();

        Assert.True(vm.UpdateCheckOnStartup);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void AnUpToDateCheck_ForgetsTheRememberedUpdate()
    {
        var rig = new Rig();
        rig.Prompt.Prime(StubUpdateService.Newer);
        var vm = rig.Vm();
        vm.Load();
        Assert.True(vm.HasPendingUpdate);

        vm.CheckForUpdatesCommand.Execute(null);

        Assert.Null(rig.Prompt.PendingUpdate);
        Assert.False(vm.HasPendingUpdate);
        Assert.Equal(Loc["Update_UpToDate"], vm.CheckStatusText);
        Assert.True(vm.HasCheckStatus);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public void AnAvailableUpdate_IsRememberedAndOpened()
    {
        var rig = new Rig { Service = new StubUpdateService(UpdateCheckStatus.UpdateAvailable, StubUpdateService.Newer) };
        var vm = rig.Vm();

        vm.CheckForUpdatesCommand.Execute(null);

        Assert.Same(StubUpdateService.Newer, rig.Prompt.PendingUpdate);
        Assert.Equal(1, rig.Prompt.Opened);
        Assert.True(vm.HasPendingUpdate);
        Assert.Equal(Loc.Format("Update_Available", "v9.9.9"), vm.CheckStatusText);

        vm.ShowPendingUpdateCommand.Execute(null);
        Assert.Equal(2, rig.Prompt.Opened);
    }

    [Fact]
    public void AFailedCheck_LeavesTheRememberedUpdateAlone()
    {
        var rig = new Rig { Service = new StubUpdateService(UpdateCheckStatus.Failed) };
        rig.Prompt.Prime(StubUpdateService.Newer);
        var vm = rig.Vm();

        vm.CheckForUpdatesCommand.Execute(null);

        Assert.Same(StubUpdateService.Newer, rig.Prompt.PendingUpdate);
        Assert.Equal(Loc.Format("Update_CheckFailed", "offline"), vm.CheckStatusText);
    }

    [Fact]
    public void AStagedPayload_ShowsTheRecoveryStrip_AndFinishClosesTheApp()
    {
        var rig = new Rig();
        rig.Service.Pending = new PendingUpdateApply(
            new UpdateApplyState { TargetVersion = "0.63.0", StagedUtc = DateTimeOffset.UtcNow },
            CanResume: true, Problem: null, LogPath: null, LogTail: "waiting for PID");
        var vm = rig.Vm();

        vm.Load();

        Assert.True(vm.HasInterruptedUpdate);
        Assert.True(vm.CanFinishInterruptedUpdate);
        Assert.Contains("0.63.0", vm.InterruptedUpdateText, StringComparison.Ordinal);
        Assert.Equal("waiting for PID", vm.InterruptedUpdateDetail);
        Assert.True(vm.HasInterruptedUpdateDetail);

        vm.FinishInterruptedUpdateCommand.Execute(null);
        Assert.Equal(1, rig.Service.Resumed);
        Assert.Equal(1, rig.Closer.Closed);
    }

    [Fact]
    public void AnUnusablePayload_OffersOnlyDiscard_AndDiscardClearsTheStrip()
    {
        var rig = new Rig();
        rig.Service.Pending = new PendingUpdateApply(
            new UpdateApplyState(), CanResume: false, Problem: "staging folder is gone", LogPath: null, LogTail: "");
        var vm = rig.Vm();
        vm.Load();

        Assert.True(vm.HasInterruptedUpdate);
        Assert.False(vm.CanFinishInterruptedUpdate);
        Assert.Equal(Loc.Format("Update_InterruptedUnusable", "staging folder is gone"), vm.InterruptedUpdateDetail);

        vm.FinishInterruptedUpdateCommand.Execute(null);
        Assert.Equal(0, rig.Service.Resumed);

        vm.DiscardInterruptedUpdateCommand.Execute(null);
        Assert.Equal(1, rig.Service.Discarded);
        Assert.False(vm.HasInterruptedUpdate);
    }

    [Fact]
    public void ApplySearch_HidesTheRowsThatDoNotMatch_ByGroupAndByRow()
    {
        var vm = new Rig().Vm();

        // "Account" is carried by one row only; the token label would also hit
        // the GitHub group's subtitle, which shows the whole group.
        vm.ApplySearch(Loc["GitHub_Account"]);
        Assert.False(vm.IsUpdatesGroupVisible);
        Assert.True(vm.IsGitHubGroupVisible);
        Assert.True(vm.GitHub.IsAccountRowVisible);
        Assert.False(vm.GitHub.IsPatRowVisible);
        Assert.False(vm.IsCheckNowRowVisible);

        // A group-title match shows every row of that group.
        vm.ApplySearch(Loc["Update_GroupTitle"]);
        Assert.True(vm.IsUpdatesGroupVisible);
        Assert.True(vm.IsCurrentVersionRowVisible);
        Assert.True(vm.IsCheckNowRowVisible);
        Assert.True(vm.IsCheckOnStartupRowVisible);
        Assert.False(vm.IsGitHubGroupVisible);

        vm.ApplySearch("");
        Assert.True(vm.IsGitHubGroupVisible);
        Assert.True(vm.GitHub.IsAccountRowVisible);
    }

    [Fact]
    public void TheSearchGroups_AreTheTwoGroups_WithLocalizedRows()
    {
        var groups = new Rig().Vm().SearchGroups;
        Assert.Equal(2, groups.Count);
        Assert.Equal(Loc["Update_GroupTitle"], groups[0].Title);
        Assert.Equal(3, groups[0].Rows.Count);
        Assert.Equal(3, groups[1].Rows.Count);
        Assert.Equal(Loc["Settings_Tip_UpdateCheckNow"], groups[0].Rows[1].Description);
    }

    [Fact]
    public void SigningInWithAToken_ClearsTheBox_AndShowsTheLogin()
    {
        var rig = new Rig { Auth = { Login = "octocat" } };
        var vm = rig.Vm();
        vm.GitHub.PatInput = " ghp_secret ";

        vm.GitHub.SignInWithPatCommand.Execute(null);

        Assert.True(vm.GitHub.IsSignedIn);
        Assert.Equal("", vm.GitHub.PatInput);
        Assert.Equal(Loc.Format("GitHub_SignedInAs", "octocat"), vm.GitHub.SignedInText);

        vm.GitHub.SignOutCommand.Execute(null);
        Assert.False(vm.GitHub.IsSignedIn);
        Assert.Equal(Loc["GitHub_NotSignedIn"], vm.GitHub.SignedInText);
    }

    /// <summary>Browser sign-in uses the client id the FEED supplies: nothing to paste.</summary>
    [Fact]
    public void TheDeviceFlow_UsesTheFeedsClientId_ThenSignsIn()
    {
        var rig = new Rig();
        var vm = rig.Vm();
        Assert.True(vm.GitHub.IsDeviceFlowRowVisible);

        vm.GitHub.StartDeviceFlowCommand.Execute(null);

        Assert.Equal("Iv23.example", rig.Auth.LastClientId);
        Assert.True(vm.GitHub.IsSignedIn);
        Assert.False(vm.GitHub.IsDeviceFlowRunning);
        Assert.False(vm.GitHub.HasDeviceCode);
        Assert.Equal("", vm.GitHub.AuthStatusText);
    }

    /// <summary>The public build reads its feed anonymously: no GitHub group, not even
    /// through the settings search, and nothing to sign in with.</summary>
    [Fact]
    public void ThePublicFeed_HasNoSignInSection()
    {
        var rig = new Rig { Feed = UpdateFeed.Public };
        var vm = rig.Vm();
        vm.Load();

        Assert.False(vm.IsGitHubGroupVisible);
        Assert.Single(vm.SearchGroups);
        Assert.False(vm.GitHub.IsDeviceFlowRowVisible);

        vm.ApplySearch(Loc["GitHub_Account"]);
        Assert.False(vm.IsGitHubGroupVisible);
        vm.ApplySearch(Loc["Update_GroupTitle"]);
        Assert.True(vm.IsUpdatesGroupVisible);
        Assert.False(vm.IsGitHubGroupVisible);
        vm.ApplySearch("");
        Assert.False(vm.IsGitHubGroupVisible);

        vm.GitHub.StartDeviceFlowCommand.Execute(null);
        Assert.Null(rig.Auth.LastClientId);
    }

    /// <summary>A private feed that brings no client id still offers the pasted token, but no
    /// browser sign-in it could not start.</summary>
    [Fact]
    public void ASignInFeedWithoutAClientId_OffersTheTokenOnly()
    {
        var rig = new Rig { Feed = new UpdateFeed("example-owner", "example-addon", RequiresSignIn: true) };
        var vm = rig.Vm();

        Assert.True(vm.IsGitHubGroupVisible);
        Assert.False(vm.GitHub.IsDeviceFlowRowVisible);
        vm.ApplySearch("");
        Assert.False(vm.GitHub.IsDeviceFlowRowVisible);
        Assert.True(vm.GitHub.IsPatRowVisible);

        vm.GitHub.StartDeviceFlowCommand.Execute(null);
        Assert.Null(rig.Auth.LastClientId);
        Assert.False(vm.GitHub.IsSignedIn);
    }

    [Fact]
    public void TheDeviceCode_IsCopiedAndOpenedThroughTheSeams()
    {
        var rig = new Rig();
        var gitHub = rig.GitHub;
        rig.Auth.DeviceFlowSupported = false;

        gitHub.StartDeviceFlowCommand.Execute(null);
        Assert.Contains("device flow disabled", gitHub.AuthStatusText, StringComparison.Ordinal);

        gitHub.OpenVerificationPageCommand.Execute(null);
        Assert.Equal(["https://github.com/login/device"], rig.Shell.Urls);

        gitHub.CopyDeviceCodeCommand.Execute(null);
        Assert.Null(rig.Clipboard.Text);
    }
}
