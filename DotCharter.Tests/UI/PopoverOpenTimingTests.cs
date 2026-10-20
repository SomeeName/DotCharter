using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     <see cref="PopoverOpenTiming" /> is the one desktop-side number for the
///     owner's "~200 ms freeze opening Tempo / Mix / Tools / Tracks"
///     (2026-08-30). A pure sink is easy to leave unwired, so besides the
///     no-sink / bad-sink contract this lints that the flyout RECORDS and the
///     composition root LISTENS — comments stripped, like every reachability
///     lint here.
/// </summary>
[Collection("PopoverOpenTiming")]
public sealed class PopoverOpenTimingTests
{
    [Fact]
    public void Record_WithoutASink_IsANoOp_AndABadSinkNeverThrows()
    {
        var before = PopoverOpenTiming.Sink;
        try
        {
            PopoverOpenTiming.Sink = null;
            PopoverOpenTiming.Record("Mix", 12.5);

            PopoverOpenTiming.Sink = (_, _) => throw new InvalidOperationException("sink broke");
            PopoverOpenTiming.Record("Mix", 12.5);

            var seen = new List<(string, double)>();
            PopoverOpenTiming.Sink = (n, ms) => seen.Add((n, ms));
            PopoverOpenTiming.Record("TempoFlyoutButton", 187.0);
            Assert.Equal([("TempoFlyoutButton", 187.0)], seen);
        }
        finally
        {
            PopoverOpenTiming.Sink = before;
        }
    }

    [Fact]
    public void MsSince_IsOnTheStopwatchAxis()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var ms = PopoverOpenTiming.MsSince(start);
        Assert.InRange(ms, 0, 10_000);
    }

    [Fact]
    public void TheFlyoutRecords_AndTheAppListens()
    {
        var flyout = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "PrewarmableFlyout.cs"))));
        Assert.Contains("PopoverOpenTiming.Record(", flyout);
        Assert.Contains("override void OnOpened()", flyout);
        // #176: Opening → Opened read 11-17 ms while the owner felt a lag; the
        // content's first layout and render come after Opened, so the open is
        // also timed to the first idle tick, in the same log as Inspector tabs.
        Assert.Contains("SurfaceShowTiming.BeginAt($\"Popover {name}\", openingTicks);", flyout);

        // The composition root is a partial over App*.cs; read the whole set so a
        // move between siblings never blinds this.
        var app = string.Concat(UiSourceTree.GetFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => SourceText.StripComments(UiSourceTree.ReadAllText(f))));
        Assert.Contains("PopoverOpenTiming.Sink =", app);
    }
}
