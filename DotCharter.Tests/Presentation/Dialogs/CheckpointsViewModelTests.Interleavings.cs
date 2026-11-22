namespace DotCharter.Tests.Presentation.Dialogs;

public sealed partial class CheckpointsViewModelTests
{
    [Theory]
    [InlineData("guard")]
    [InlineData("history")]
    [InlineData("read")]
    [InlineData("reload")]
    public async Task An_obsolete_restore_does_not_replace_or_report_over_a_new_session(string phase)
    {
        var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        async Task<bool> PauseAsync()
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        }

        if (phase == "guard") h.Session.ReplaceGuard = PauseAsync;
        if (phase == "history")
        {
            h.PushAnEdit();
            h.Prompt.Response = PauseAsync;
        }
        if (phase == "read")
        {
            var token = TestContext.Current.CancellationToken;
            h.Service.RestorePass = () =>
            {
                entered.TrySetResult();
                releaseRead.Wait(token);
                return h.Service.RestoredProject;
            };
        }
        if (phase == "reload") h.Session.ReloadMedia = async () => { await PauseAsync(); };
        var restoring = h.RestoreAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var next = new ChartProject { AudioFileName = "B.ogg" };
        h.Edit.Editor.ApplyProject(next, resetHistory: true);
        h.Edit.State.CurrentProjectPath = @"C:\charts\B.rscp";
        h.Edit.State.CurrentAudioFileName = "B.ogg";
        h.Edit.State.MainDisplayMessage = "new session";
        release.TrySetResult();
        releaseRead.Set();
        await restoring;

        Assert.Same(next, h.Edit.State.ActiveProject);
        Assert.Equal("B.ogg", h.Edit.State.CurrentAudioFileName);
        Assert.Equal("new session", h.Edit.State.MainDisplayMessage);
    }
}
