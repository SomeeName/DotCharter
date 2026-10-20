using DotCharter.Services.Infrastructure.Recovery;

namespace DotCharter.Tests.Services.Recovery;

public class RecoverySnapshotSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    [Fact]
    public void Clean_NeverSnapshots()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);

        Assert.False(scheduler.IsDirty);
        Assert.False(scheduler.ShouldSnapshot(T0 + TimeSpan.FromHours(5), Interval, Debounce));
    }

    [Fact]
    public void Change_ThenQuietForDebounce_Snapshots()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        Assert.False(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(10), Interval, Debounce));
        Assert.True(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(11), Interval, Debounce));
    }

    [Fact]
    public void ContinuousEditBurst_StillSnapshotsAtInterval()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);

        // Edits arriving every 5 s: the debounce never elapses...
        for (var s = 5; s <= 55; s += 5)
        {
            scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(s));
            Assert.False(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(s + 1), Interval, Debounce));
        }

        // ...but once a full interval has passed since the last snapshot,
        // one is due regardless of the ongoing burst.
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(60));
        Assert.True(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(61), Interval, Debounce));
    }

    [Fact]
    public void SnapshotCaptured_ClearsDirty_UntilNextChange()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));
        scheduler.NotifySnapshotCaptured(T0 + TimeSpan.FromSeconds(12));

        Assert.False(scheduler.IsDirty);
        Assert.False(scheduler.ShouldSnapshot(T0 + TimeSpan.FromMinutes(30), Interval, Debounce));

        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(20));
        Assert.True(scheduler.IsDirty);
        Assert.True(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(31), Interval, Debounce));
    }

    [Fact]
    public void Reset_DiscardsPendingChanges()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));
        scheduler.Reset(T0 + TimeSpan.FromSeconds(2));

        Assert.False(scheduler.IsDirty);
        Assert.False(scheduler.ShouldSnapshot(T0 + TimeSpan.FromMinutes(10), Interval, Debounce));
    }

    [Fact]
    public void EditDuringWrite_MarksDirtyAgain()
    {
        var scheduler = new RecoverySnapshotScheduler(T0);
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(1));

        // Capture happens, then an edit lands while the file write is still
        // running — the scheduler must consider that edit unsnapshotted.
        scheduler.NotifySnapshotCaptured(T0 + TimeSpan.FromSeconds(12));
        scheduler.NotifyChanged(T0 + TimeSpan.FromSeconds(12.5));

        Assert.True(scheduler.IsDirty);
        Assert.True(scheduler.ShouldSnapshot(T0 + TimeSpan.FromSeconds(23), Interval, Debounce));
    }
}
