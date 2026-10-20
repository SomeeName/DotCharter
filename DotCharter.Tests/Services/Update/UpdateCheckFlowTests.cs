using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.Update;
using DotCharter.Tests.Presentation.Settings;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The one owner of the "remembered update" rule. Both doors into the
///     update dialog re-open on the remembered find without checking again, so
///     a check that comes back UP TO DATE has to forget it — or the app offers
///     a download of the version it is already running and Help ▸ Check for
///     updates never reaches the network again.
/// </summary>
public sealed class UpdateCheckFlowTests
{
    private static (UpdateCheckFlow Flow, StubUpdateService Service, StubUpdatePrompt Prompt, ProjectState State) Build(
        UpdateCheckStatus status, UpdateInfo? update = null, IApplicationCloser? closer = null)
    {
        var service = new StubUpdateService(status, update);
        var prompt = new StubUpdatePrompt();
        var state = new ProjectState();
        var flow = new UpdateCheckFlow(service, prompt, new FakeConfigurationService(), FakeLocalization.English(),
            new FakeAppLogger(), new TestUiDispatcher(), state, closer);
        return (flow, service, prompt, state);
    }

    [Fact]
    public async Task AnUpToDateCheck_ForgetsTheRememberedUpdate()
    {
        var (flow, _, prompt, _) = Build(UpdateCheckStatus.UpToDate);
        prompt.Prime(StubUpdateService.Newer);

        var outcome = await flow.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Null(prompt.PendingUpdate);
        Assert.False(outcome.HasPendingUpdate);
        Assert.False(flow.HasPendingUpdate);
        Assert.Equal(FakeLocalization.English()["Update_UpToDate"], outcome.StatusText);
    }

    [Fact]
    public async Task AndThenTheHelpMenuDoor_ChecksAgainInsteadOfReopening()
    {
        var (flow, service, prompt, state) = Build(UpdateCheckStatus.UpToDate);
        prompt.Prime(StubUpdateService.Newer);

        await flow.CheckNowAsync(TestContext.Current.CancellationToken);
        await flow.CheckOrShowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, service.Checks);
        Assert.Equal(0, prompt.Opened);
        Assert.Equal(FakeLocalization.English()["Update_UpToDate"], state.MainDisplayMessage);
    }

    [Fact]
    public async Task TheHelpMenuDoor_ReopensARememberedFind_WithoutChecking()
    {
        var (flow, service, prompt, _) = Build(UpdateCheckStatus.UpToDate);
        prompt.Prime(StubUpdateService.Newer);

        await flow.CheckOrShowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.Checks);
        Assert.Equal(1, prompt.Opened);
    }

    [Fact]
    public async Task APrivateFeedWithoutSignIn_TellsTheUserToSignIn_AndKeepsTheRememberedFind()
    {
        // #925: a plugin's private feed is never asked anonymously; the Help-menu door must
        // say "sign in", not "check failed: ?" and not "up to date".
        var (flow, _, prompt, state) = Build(UpdateCheckStatus.SignInRequired);

        var outcome = await flow.CheckNowAsync(TestContext.Current.CancellationToken);
        await flow.CheckOrShowAsync(TestContext.Current.CancellationToken);

        var expected = FakeLocalization.English()["Update_SignInRequired"];
        Assert.Contains("Sign in to GitHub", expected, StringComparison.Ordinal);
        Assert.Equal(expected, outcome.StatusText);
        Assert.Equal(expected, state.MainDisplayMessage);
        Assert.False(outcome.HasPendingUpdate);
        Assert.Equal(0, prompt.Opened);

        prompt.Prime(StubUpdateService.Newer);
        Assert.True((await flow.CheckNowAsync(TestContext.Current.CancellationToken)).HasPendingUpdate);
        Assert.Same(StubUpdateService.Newer, prompt.PendingUpdate);
    }

    [Fact]
    public async Task AnAvailableUpdate_IsOpened_AndRemembered()
    {
        var (flow, _, prompt, _) = Build(UpdateCheckStatus.UpdateAvailable, StubUpdateService.Newer);

        var outcome = await flow.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Same(StubUpdateService.Newer, prompt.PendingUpdate);
        Assert.Equal(1, prompt.Opened);
        Assert.True(outcome.HasPendingUpdate);
        Assert.True(flow.ShowPendingUpdate());
        Assert.Equal(2, prompt.Opened);
    }

    [Fact]
    public async Task AFailedCheck_LeavesTheRememberedUpdateAlone()
    {
        var (flow, _, prompt, _) = Build(UpdateCheckStatus.Failed);
        prompt.Prime(StubUpdateService.Newer);

        var outcome = await flow.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Same(StubUpdateService.Newer, prompt.PendingUpdate);
        Assert.True(outcome.HasPendingUpdate);
        Assert.Contains("offline", outcome.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishingAStagedApply_RelaunchesTheUpdater_ThenCloses()
    {
        var closer = new StubApplicationCloser();
        var (flow, service, _, _) = Build(UpdateCheckStatus.UpToDate, closer: closer);

        var failure = flow.FinishPendingApply();

        Assert.Null(failure);
        Assert.Equal(1, service.Resumed);
        Assert.Equal(1, closer.Closed);
    }

    [Fact]
    public void FinishingAStagedApply_WithoutACloser_RefusesBeforeApplying_AndReports()
    {
        var (flow, service, _, _) = Build(UpdateCheckStatus.UpToDate);

        var failure = flow.FinishPendingApply();

        Assert.Equal(0, service.Resumed);
        Assert.NotNull(failure);
        Assert.Equal("1.2.3", flow.CurrentVersionText);
    }

    [Fact]
    public void TheStartupCheck_IsDisabledByTheSwitch()
    {
        var (flow, service, _, _) = Build(UpdateCheckStatus.UpdateAvailable, StubUpdateService.Newer);
        AppContext.SetSwitch(UpdateCheckFlow.DisableStartupCheckSwitch, true);

        flow.StartStartupCheck();

        Assert.Equal(0, service.Checks);
    }
}
