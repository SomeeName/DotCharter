using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Update;
using DotCharter.Tests.Presentation.Settings;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The update prompt on the dialog host: <see cref="UpdatePrompt" /> keeps
///     the remembered find (the dialog's view model lives only while it is open)
///     and <see cref="UpdateAvailableViewModel" /> is one open of it.
/// </summary>
public sealed class UpdateDialogTests
{
    private static UpdateAvailableViewModel Open(IAppUpdateService service, UpdateInfo update)
    {
        var vm = new UpdateAvailableViewModel(service, FakeLocalization.English(), new FakeAppLogger(),
            new StubApplicationCloser());
        vm.Configure(new UpdateAvailableRequest(update));
        return vm;
    }

    [Fact]
    public void Configure_shows_the_release_and_offers_the_install()
    {
        var vm = Open(new StubUpdateService(UpdateCheckStatus.UpdateAvailable), StubUpdateService.Newer);

        Assert.Contains("v9.9.9", vm.VersionText, StringComparison.Ordinal);
        Assert.Equal("notes", vm.ReleaseNotes);
        Assert.True(vm.HasReleaseNotes);
        Assert.True(vm.CanInstall);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void A_blocked_install_says_why_and_cannot_be_started()
    {
        var vm = Open(new BlockedUpdateService(), StubUpdateService.Newer);

        Assert.False(vm.CanInstall);
        Assert.Equal(FakeLocalization.English()["Update_InstallDirNotWritable"], vm.StatusText);
    }

    [Fact]
    public void Later_and_Esc_answer_false()
    {
        var later = Open(new StubUpdateService(UpdateCheckStatus.UpdateAvailable), StubUpdateService.Newer);
        later.LaterCommand.Execute(null);
        Assert.False(ChartEditHarness.Answer(later.Result));

        var esc = Open(new StubUpdateService(UpdateCheckStatus.UpdateAvailable), StubUpdateService.Newer);
        Assert.True(esc.TryCancel());
        Assert.False(ChartEditHarness.Answer(esc.Result));
    }

    [Fact]
    public async Task The_prompt_remembers_the_find_and_opens_it_once()
    {
        var dialogs = new RecordingDialogs();
        var prompt = new UpdatePrompt(dialogs, new FakeAppLogger());

        prompt.Prime(StubUpdateService.Newer);
        Assert.Same(StubUpdateService.Newer, prompt.PendingUpdate);
        Assert.Empty(dialogs.Shown);

        prompt.Open(StubUpdateService.Newer);
        prompt.Open(StubUpdateService.Newer);
        Assert.Equal([nameof(IUpdateAvailableScreen)], dialogs.Shown);

        // Once the dialog has answered, the next Open shows it again. Whether the
        // prompt's continuation ran inline or was posted is not this test's
        // business, so it asks until the reopen lands (a detector, not a budget).
        dialogs.Answer(false);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (dialogs.Shown.Count < 2 && DateTime.UtcNow < deadline)
        {
            prompt.Open(StubUpdateService.Newer);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, dialogs.Shown.Count);

        prompt.ClearPending();
        Assert.Null(prompt.PendingUpdate);
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IUpdateAvailableScreen),
            Open(new BlockedUpdateService(), StubUpdateService.Newer)));

    /// <summary>An <see cref="IDialogs" /> that records what was shown; <see cref="Answer" /> closes the last one.</summary>
    internal sealed class RecordingDialogs : IDialogs
    {
        private TaskCompletionSource<object?>? _open;

        public List<string> Shown { get; } = [];

        public bool HasOpenDialog => _open is { Task.IsCompleted: false };

        public event EventHandler<DialogChange>? Changed;

        public async Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            Shown.Add(typeof(TContract).Name);
            var open = _open = new TaskCompletionSource<object?>();
            Changed?.Invoke(this, new DialogChange(typeof(TContract), typeof(TContract).Name, true));
            return (TResult?)await open.Task;
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) =>
            Task.FromResult(false);

        public bool CloseTop() => _open?.TrySetResult(null) ?? false;

        public void Answer(object? result) => _open?.TrySetResult(result);
    }

    private sealed class BlockedUpdateService : IAppUpdateService
    {
        public Version CurrentVersion => new(1, 2, 3);

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null));

        public Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("blocked");

        public Task<string?> TryFetchReleaseChecksumsAsync(Version version, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public UpdateApplyBlockReason GetApplyBlockReason() => UpdateApplyBlockReason.InstallDirNotWritable;

        public void ApplyDownloadedUpdate(string zipPath) => throw new InvalidOperationException("blocked");

        public PendingUpdateApply? GetPendingUpdateApply() => null;

        public void ResumePendingUpdateApply() { }

        public void DiscardPendingUpdateApply() { }
    }
}
