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

    /// <summary>
    ///     A dialog's result completes asynchronously. Completing the session from a POOL continuation
    ///     made it a second completer racing <see cref="DialogStack.CloseTop" />'s dismissal, and a caller
    ///     on the UI thread could still be waiting after its queue had drained (shown by a headless close
    ///     under load). The completion is queued to the context the dialog opened on instead.
    /// </summary>
    [Fact]
    public async Task A_dialogs_own_result_completes_the_session_on_the_context_it_opened_on()
    {
        var context = new QueueingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // As DialogViewModel's: its continuations never run inline.
            var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new DialogStack().Open(result.Task);
            result.SetResult(7);

            Assert.True(context.RunAll() > 0, "the session's completion was not queued to the dialog's context");
            Assert.True(session.Completion.IsCompletedSuccessfully, "running the queued work did not complete the session");
            Assert.Equal(7, await session.Completion);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
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

    /// <summary>A context that only queues what is posted to it, until the test runs it.</summary>
    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posted) _posted.Enqueue((d, state));
        }

        public int RunAll()
        {
            var ran = 0;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_posted)
                {
                    if (_posted.Count == 0) return ran;
                    next = _posted.Dequeue();
                }

                next.Callback(next.State);
                ran++;
            }
        }
    }
}
