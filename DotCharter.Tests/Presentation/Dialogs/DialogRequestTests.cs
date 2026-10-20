using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The pieces the dialog migration added to § 3.2: a dialog that has a say
///     in Esc, a typed request that configures the dialog it opens, and the
///     app-wide slot forwarding its host's open/close changes.
/// </summary>
public sealed class DialogRequestTests
{
    private sealed class Guarded(bool allow) : ICancelableDialog
    {
        public int Asked { get; private set; }

        public bool TryCancel()
        {
            Asked++;
            return allow;
        }
    }

    [Fact]
    public async Task A_dialog_that_refuses_Esc_stays_open_and_Esc_is_still_spent()
    {
        var stack = new DialogStack();
        var guard = new Guarded(allow: false);
        var session = stack.Open(new TaskCompletionSource<bool>().Task, guard);

        Assert.True(stack.CloseTop(), "the Esc was answered — it must not fall through to what is underneath");
        Assert.Equal(1, guard.Asked);
        Assert.True(stack.HasOpenDialog);
        Assert.False(session.Completion.IsCompleted);

        stack.CloseAll();
        Assert.False(await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Equal(1, guard.Asked);
    }

    [Fact]
    public async Task A_dialog_that_accepts_Esc_is_asked_first_then_closed_with_the_default()
    {
        var stack = new DialogStack();
        var guard = new Guarded(allow: true);
        var session = stack.Open(new TaskCompletionSource<int>().Task, guard);

        Assert.True(stack.CloseTop());

        Assert.Equal(1, guard.Asked);
        Assert.False(stack.HasOpenDialog);
        Assert.Equal(0, await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_opens_its_own_contract_and_configures_the_dialog_with_itself()
    {
        var dialogs = new InlineDialogs(new ProbeDialog());
        var request = new ProbeRequest("hello");

        var answer = dialogs.ShowAsync(request);

        var probe = (ProbeDialog)dialogs.Last!;
        Assert.Same(request, probe.Request);
        probe.Answer(7);
        Assert.Equal(7, await answer.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_for_a_dialog_that_cannot_take_it_fails_loudly()
    {
        var dialogs = new InlineDialogs(new DeafDialog());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dialogs.ShowAsync(new ProbeRequest("x")));
        Assert.Contains(nameof(ProbeRequest), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_slot_forwards_its_hosts_changes_and_stops_when_the_host_detaches()
    {
        var slot = new DialogsSlot();
        var host = new InlineDialogs(new ProbeDialog());
        var seen = new List<bool>();
        slot.Changed += (_, change) => seen.Add(change.IsOpen);

        slot.Attach(host);
        slot.Attach(host);
        host.Raise(true);
        slot.Detach(host);
        host.Raise(false);

        Assert.Equal([true], seen);
    }

    public interface IProbeScreen : IDialogScreen<int>;

    public sealed record ProbeRequest(string Text) : IDialogRequest<ProbeRequest, IProbeScreen, int>;

    private sealed class ProbeDialog : ObservableObject, IProbeScreen, IDialogSetup<ProbeRequest>
    {
        private readonly TaskCompletionSource<int> _result = new();

        public ProbeRequest? Request { get; private set; }

        public Task<int> Result => _result.Task;

        public void Configure(ProbeRequest request) => Request = request;

        public void Answer(int value) => _result.SetResult(value);
    }

    private sealed class DeafDialog : ObservableObject, IProbeScreen
    {
        public Task<int> Result { get; } = new TaskCompletionSource<int>().Task;
    }

    /// <summary>Resolves every contract to the one dialog it was given and awaits its result.</summary>
    private sealed class InlineDialogs(object dialog) : IDialogs
    {
        public object? Last { get; private set; }

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed;

        public async Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            var screen = (TContract)dialog;
            init?.Invoke(screen);
            Last = screen;
            return await screen.Result;
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) =>
            Task.FromResult(false);

        public bool CloseTop() => false;

        public void Raise(bool open) => Changed?.Invoke(this, new DialogChange(typeof(IProbeScreen), "Probe", open));
    }
}
