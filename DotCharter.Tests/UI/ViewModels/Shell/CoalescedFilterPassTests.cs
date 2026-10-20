using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     <see cref="CoalescedFilterPass" /> is the whole answer to "search feels
///     laggy": a filter pass over the owner's 2,160-song library costs 126–170 ms
///     with the real list attached (measured Release; the list re-realizing its
///     rows is 104–148 ms of it), so nine keystrokes used to be nine of them,
///     synchronously, in the property setter.
///     <para>
///         Asserted STRUCTURALLY and never against a clock: what this pins is the
///         REQUEST-to-PASS ratio and who wins between a typed request and a
///         discrete one. The <c>post</c> delegate is injected, so the queue is
///         drained by hand and no dispatcher, timer or sleep is involved.
///     </para>
/// </summary>
public sealed class CoalescedFilterPassTests
{
    [Fact]
    public void NineRequests_BeforeTheQueueDrains_ProduceExactlyOnePass()
    {
        var queue = new List<Action>();
        var passes = 0;
        var pass = new CoalescedFilterPass(() => passes++, queue.Add);

        for (var i = 0; i < 9; i++) pass.Request();

        // Nine keystrokes, ONE scheduled callback, and nothing has run yet: the
        // burst is still draining through input, which outranks Background.
        Assert.Equal(9, pass.RequestCount);
        Assert.Single(queue);
        Assert.Equal(0, passes);
        Assert.True(pass.IsPending);

        foreach (var job in queue) job();

        Assert.Equal(1, passes);
        Assert.Equal(1, pass.PassCount);
        Assert.False(pass.IsPending);
    }

    /// <summary>A pass that has RUN re-arms: the next keystroke is not swallowed.</summary>
    [Fact]
    public void ARequestAfterAPassRan_SchedulesTheNextOne()
    {
        var queue = new List<Action>();
        var passes = 0;
        var pass = new CoalescedFilterPass(() => passes++, queue.Add);

        pass.Request();
        queue[0]();
        pass.Request();

        Assert.Equal(2, queue.Count);
        queue[1]();
        Assert.Equal(2, passes);
    }

    /// <summary>A chip / sort / rescan is one discrete gesture: it applies at once
    /// AND consumes the typed pass queued behind it, so the same filter state is
    /// never published twice.</summary>
    [Fact]
    public void RunImmediately_RunsNow_AndDropsThePassQueuedBehindIt()
    {
        var queue = new List<Action>();
        var passes = 0;
        var pass = new CoalescedFilterPass(() => passes++, queue.Add);

        pass.Request();          // typing
        pass.RunImmediately();   // …then a filter chip

        Assert.Equal(1, passes);
        Assert.False(pass.IsPending);

        foreach (var job in queue) job(); // the queued callback finds nothing to do
        Assert.Equal(1, passes);
    }

    /// <summary>A scan clears the list it was going to filter; the pending pass
    /// must not run against the replacement.</summary>
    [Fact]
    public void Cancel_DropsThePendingPass()
    {
        var queue = new List<Action>();
        var passes = 0;
        var pass = new CoalescedFilterPass(() => passes++, queue.Add);

        pass.Request();
        pass.Cancel();
        foreach (var job in queue) job();

        Assert.Equal(0, passes);
        Assert.Equal(0, pass.PassCount);
    }

    /// <summary>Proves the counters are not decoration: <see cref="DotCharter.Tone.Presentation.Shared.CoalescedPass.PassCount" />
    /// is what the view-model tests assert on, so it has to count BOTH doors.</summary>
    [Fact]
    public void PassCount_CountsImmediateAndCoalescedPassesAlike()
    {
        var queue = new List<Action>();
        var pass = new CoalescedFilterPass(() => { }, queue.Add);

        pass.RunImmediately();
        pass.Request();
        queue[0]();

        Assert.Equal(2, pass.PassCount);
        Assert.Equal(2, pass.RequestCount);
    }
}
