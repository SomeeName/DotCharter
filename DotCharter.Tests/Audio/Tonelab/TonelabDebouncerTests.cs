using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>A scheduler that fires only when the test says so: the debounce without a wall clock.</summary>
internal sealed class ManualTonelabScheduler : ITonelabScheduler
{
    private readonly List<Entry> _entries = [];

    public int Scheduled { get; private set; }

    public TimeSpan LastDelay { get; private set; }

    public int Pending
    {
        get
        {
            lock (_entries) return _entries.Count(e => !e.Cancelled);
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new Entry(callback);
        lock (_entries)
        {
            _entries.Add(entry);
            Scheduled++;
            LastDelay = delay;
        }

        return entry;
    }

    /// <summary>The delay elapses for everything scheduled so far.</summary>
    public void Elapse()
    {
        Entry[] due;
        lock (_entries)
        {
            due = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in due)
            if (!entry.Cancelled) entry.Callback();
    }

    private sealed class Entry(Action callback) : IDisposable
    {
        public Action Callback { get; } = callback;

        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

public sealed class TonelabDebouncerTests
{
    [Fact]
    public void ABurst_RunsOnce_WithTheLatestWork()
    {
        var scheduler = new ManualTonelabScheduler();
        using var debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
        var ran = new List<int>();

        for (var i = 1; i <= 5; i++)
        {
            var value = i;
            debouncer.Post(() => ran.Add(value));
        }

        Assert.Empty(ran);
        Assert.Equal(1, scheduler.Pending);
        scheduler.Elapse();

        Assert.Equal([5], ran);
        Assert.False(debouncer.IsPending);
    }

    [Fact]
    public void TheDelay_IsInsideTheProtocolsWindow()
    {
        var scheduler = new ManualTonelabScheduler();
        using var debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
        debouncer.Post(() => { });
        Assert.InRange(scheduler.LastDelay.TotalMilliseconds, 30, 50);
    }

    [Fact]
    public void Cancel_DropsTheWaitingWork()
    {
        var scheduler = new ManualTonelabScheduler();
        using var debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
        var ran = 0;
        debouncer.Post(() => ran++);

        Assert.True(debouncer.Cancel());
        scheduler.Elapse();

        Assert.Equal(0, ran);
        Assert.False(debouncer.Cancel());
    }

    [Fact]
    public void PostsAfterAFire_StartANewRound()
    {
        var scheduler = new ManualTonelabScheduler();
        using var debouncer = new TonelabDebouncer(scheduler, TonelabDebouncer.DefaultDelay);
        var ran = new List<int>();
        debouncer.Post(() => ran.Add(1));
        scheduler.Elapse();
        debouncer.Post(() => ran.Add(2));
        scheduler.Elapse();
        Assert.Equal([1, 2], ran);
    }
}
