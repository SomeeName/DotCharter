using DotCharter.UI.ViewModels.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     Concurrency audit #13: <c>MessageDialogViewModel</c> held a
///     <c>SemaphoreSlim</c> from "wait my turn" to "the user dismissed", so a
///     dialog nobody could dismiss — a view model that opens while nothing
///     renders — wedged every later message in the process, silently. The
///     turnstile is the pure part: a bounded wait, a displacement of the stuck
///     holder, and token-identified turns so the displaced holder cannot free
///     the turn it lost. The stuck timeout runs on a <see cref="StepTimeProvider" />:
///     it expires exactly when a case advances the clock past it, never because the
///     box was slow between two lines.
/// </summary>
public sealed class DialogTurnstileTests
{
    private static readonly TimeSpan Stuck = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task ASecondCaller_WaitsForAHolderThatFinishes()
    {
        var turnstile = new DialogTurnstile(Stuck, new StepTimeProvider());
        var (first, displacedA) = await BoundedWait.ForAsync(turnstile.EnterAsync(() => Assert.Fail("nothing to displace"), TestContext.Current.CancellationToken), "the first dialog turn", TestContext.Current.CancellationToken);
        Assert.False(displacedA);

        var second = turnstile.EnterAsync(() => Assert.Fail("the holder is not stuck"), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        turnstile.Exit(first);
        var (_, displacedB) = await BoundedWait.ForAsync(second, "the second dialog after holder exit", TestContext.Current.CancellationToken);
        Assert.False(displacedB);
        Assert.Equal(0, turnstile.Displacements);
    }

    [Fact]
    public async Task AStuckHolder_IsDisplaced_AndCannotFreeTheTurnItLost()
    {
        var clock = new StepTimeProvider();
        var turnstile = new DialogTurnstile(Stuck, clock);
        var (stuck, _) = await BoundedWait.ForAsync(turnstile.EnterAsync(() => { }, TestContext.Current.CancellationToken), "the initial dialog turn", TestContext.Current.CancellationToken);

        // The holder never exits. The next caller must not wait forever: once its
        // wait has run the stuck timeout out, it displaces the holder (the callback
        // is the dialog's cancel) and takes the turn.
        var displaced = 0;
        var entering = turnstile.EnterAsync(() => displaced++, TestContext.Current.CancellationToken);
        Assert.False(entering.IsCompleted, "the second caller took a turn that was held");
        Assert.Equal(1, clock.PendingTimers);
        clock.Advance(Stuck);
        var (thief, wasDisplaced) = await BoundedWait.ForAsync(entering, "the stuck dialog displacement", TestContext.Current.CancellationToken);
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
        var (_, thirdDisplaced) = await BoundedWait.ForAsync(third, "the third dialog after thief exit", TestContext.Current.CancellationToken);
        Assert.False(thirdDisplaced);
    }

    [Fact]
    public void TheDefaultTimeoutIsGenerousButBounded() =>
        // Long enough that a user reading a confirmation is essentially never
        // displaced; short enough that a wedged process recovers on its own.
        Assert.InRange(DialogTurnstile.DefaultStuckTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));

}
