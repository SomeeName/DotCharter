using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The pure half of target-architecture.md § 3.2: <see cref="DialogStack" />,
///     <see cref="DialogsSlot" /> and the message prompt's view model. The UI half
///     (<c>DialogHost</c>) is <c>Perf/ScreenHostUiPerfSmokeTests</c>.
/// </summary>
public sealed class DialogStackTests
{
    [Fact]
    public async Task A_session_completes_with_the_dialogs_own_result()
    {
        var stack = new DialogStack();
        var result = new TaskCompletionSource<int>();
        var session = stack.Open(result.Task);

        Assert.True(stack.HasOpenDialog);
        result.SetResult(7);

        Assert.Equal(7, await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloseTop_dismisses_only_the_top_with_the_default_result()
    {
        var stack = new DialogStack();
        var bottom = stack.Open(new TaskCompletionSource<string>().Task);
        var top = stack.Open(new TaskCompletionSource<string>().Task);

        Assert.True(stack.CloseTop());

        Assert.Null(await top.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.False(bottom.Completion.IsCompleted);
        Assert.Equal(1, stack.Count);

        stack.CloseAll();
        Assert.False(stack.HasOpenDialog);
        Assert.False(stack.CloseTop());
    }

    [Fact]
    public async Task A_faulted_dialog_faults_its_session()
    {
        var result = new TaskCompletionSource<bool>();
        var session = new DialogStack().Open(result.Task);
        result.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_slot_reports_nothing_open_and_refuses_to_show_without_a_host()
    {
        var slot = new DialogsSlot();

        Assert.False(slot.HasOpenDialog);
        Assert.False(slot.CloseTop());
        Assert.Throws<InvalidOperationException>(() => { _ = slot.MessageAsync("t", "m"); });
    }

    [Fact]
    public async Task The_message_prompt_completes_true_on_OK_and_false_on_Cancel()
    {
        var ok = new MessagePromptViewModel();
        ok.Configure("Title", "Body", isConfirmation: true);
        Assert.True(ok.HasMessage);
        Assert.True(ok.IsConfirmation);
        ok.OkCommand.Execute(null);
        Assert.True(await ok.Result.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        var cancel = new MessagePromptViewModel();
        cancel.Configure("Title", "", isConfirmation: false);
        Assert.False(cancel.HasMessage);
        cancel.CancelCommand.Execute(null);
        Assert.False(await cancel.Result.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Configuring_the_prompt_raises_PropertyChanged_for_what_it_changed()
    {
        IMessagePromptScreen prompt = new MessagePromptViewModel();
        var raised = new List<string?>();
        prompt.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        ((IMessagePromptSetup)prompt).Configure("T", "M", isConfirmation: true);

        Assert.Equal(["Title", "Message", "HasMessage", "IsConfirmation"], raised);
    }
}
