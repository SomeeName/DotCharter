using DotCharter.UI.ViewModels.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     Concurrency audit #13: <c>MessageDialogViewModel</c> held a
///     <c>SemaphoreSlim</c> from "wait my turn" to "the user dismissed", so a
///     dialog nobody could dismiss — a view model that opens while nothing
///     renders — wedged every later message in the process, silently. The
///     turnstile is the pure part: a bounded wait, a displacement of the stuck
///     holder, and token-identified turns so the displaced holder cannot free
///     the turn it lost. The 20 ms timeout here is the DELAY that lets the
///     displacement path run; the assertions are on what happened, not when.
/// </summary>
public sealed class DialogTurnstileTests
{
    /// <summary>
    ///     The displacement DELAY, and it is waited out rather than raced: the
    ///     test <c>await</c>s the displacing <c>EnterAsync</c>, so a longer value
    ///     only costs wall time and can never change the outcome.
    ///     <para>
    ///         It was 20 ms, which the case below then raced against a fixed
    ///         <c>Task.Delay(5)</c> — a 15 ms margin, on a platform whose timer
    ///         granularity is ~15 ms on its own. Under load that delay overran
    ///         the timeout, the third caller displaced the THIEF, and the test
    ///         failed on its `Assert.Fail("the thief is live, not stuck")` —
    ///         a displacement the product was right to make.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan Stuck = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ASecondCaller_WaitsForAHolderThatFinishes()
    {
        var turnstile = new DialogTurnstile(TimeSpan.FromSeconds(60));
        var (first, displacedA) = await turnstile.EnterAsync(() => Assert.Fail("nothing to displace"), TestContext.Current.CancellationToken);
        Assert.False(displacedA);

        var second = turnstile.EnterAsync(() => Assert.Fail("the holder is not stuck"), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        turnstile.Exit(first);
        var (_, displacedB) = await second;
        Assert.False(displacedB);
        Assert.Equal(0, turnstile.Displacements);
    }

    [Fact]
    public async Task AStuckHolder_IsDisplaced_AndCannotFreeTheTurnItLost()
    {
        var turnstile = new DialogTurnstile(Stuck);
        var (stuck, _) = await turnstile.EnterAsync(() => { }, TestContext.Current.CancellationToken);

        // The holder never exits. The next caller must not wait forever: it
        // displaces the holder (the callback is the dialog's cancel) and takes
        // the turn.
        var displaced = 0;
        var (thief, wasDisplaced) = await turnstile.EnterAsync(() => displaced++, TestContext.Current.CancellationToken);
        Assert.True(wasDisplaced);
        Assert.Equal(1, displaced);
        Assert.Equal(1, turnstile.Displacements);

        // The stuck holder eventually wakes (its task was completed) and exits:
        // that must NOT hand the turn to a third caller while the thief holds it.
        turnstile.Exit(stuck);
        var third = turnstile.EnterAsync(() => Assert.Fail("the thief is live, not stuck"),
            TestContext.Current.CancellationToken);
        // Checked IMMEDIATELY, with no delay, and that is what makes it sound
        // rather than what makes it fast. `SemaphoreSlim.WaitAsync` takes a free
        // permit SYNCHRONOUSLY, so if `Exit(stuck)` had wrongly released the
        // turn, `EnterAsync` would have run straight through to its return and
        // handed back an already-completed task. The bug is therefore visible
        // the instant the call returns; sleeping first only opened a window for
        // the stuck timeout to complete this task the LEGITIMATE way and turn a
        // correct product into a red gate.
        Assert.False(third.IsCompleted, "a displaced holder's Exit freed a turn it no longer owned");

        // The thief's own exit is what frees it.
        turnstile.Exit(thief);
        var (_, thirdDisplaced) = await third;
        Assert.False(thirdDisplaced);
    }

    [Fact]
    public void TheDefaultTimeoutIsGenerousButBounded() =>
        // Long enough that a user reading a confirmation is essentially never
        // displaced; short enough that a wedged process recovers on its own.
        Assert.InRange(DialogTurnstile.DefaultStuckTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

    /// <summary>The view model routes through the turnstile, and a displaced
    /// dialog's task answers FALSE — a cancel, the safe answer for every
    /// confirmation in the app. Source-level, because ShowAsync hops
    /// <c>Dispatcher.UIThread</c>, which never completes on a plain test thread.</summary>
    [Fact]
    public void TheViewModel_EntersTheTurnstile_AndCancelsWhatItDisplaces()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Dialogs",
            "MessageDialogViewModel.cs")));
        Assert.DoesNotContain("SemaphoreSlim", source);
        var enter = source.IndexOf("_queue.EnterAsync(", StringComparison.Ordinal);
        var cancel = source.IndexOf("TrySetResult(false)", StringComparison.Ordinal);
        var invoke = source.IndexOf("Dispatcher.UIThread.InvokeAsync(", StringComparison.Ordinal);
        var exit = source.IndexOf("_queue.Exit(turn)", StringComparison.Ordinal);
        Assert.True(enter >= 0 && cancel > enter && invoke > cancel && exit > invoke,
            "ShowAsync must enter the turnstile (cancelling what it displaces) before it shows, and exit in finally");
    }
}
