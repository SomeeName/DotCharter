using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     #309: the minimap repainted on every selection change without a throttle — a marquee
///     drag raises one change per item and per pointer move. Selection changes now go through
///     <see cref="TrailingRepaintGate" />: one repaint at once, the rest of a burst coalesced into
///     ONE trailing repaint per interval, so the last change always lands. Injected clock.
/// </summary>
public sealed class TrailingRepaintGateTests
{
    [Fact]
    public void ALoneChange_RepaintsAtOnce()
    {
        var gate = new TrailingRepaintGate(33);
        Assert.Equal(RepaintDecision.Now, gate.Request(1_000));
        Assert.Equal(RepaintDecision.Now, gate.Request(2_000));
    }

    [Fact]
    public void ABurst_IsOneRepaintNow_AndOneTrailing()
    {
        var gate = new TrailingRepaintGate(33);
        var now = 0;
        var trailing = 0;
        Assert.Equal(RepaintDecision.Now, gate.Request(1_000));
        for (var i = 1; i <= 500; i++)   // a marquee selecting 500 notes inside one frame
        {
            switch (gate.Request(1_000 + i / 100))
            {
                case RepaintDecision.Now: now++; break;
                case RepaintDecision.ScheduleTrailing: trailing++; break;
            }
        }

        Assert.Equal(0, now);
        Assert.Equal(1, trailing);

        // The trailing repaint runs, and re-arms the gate from its own time.
        gate.TrailingFired(1_033);
        Assert.Equal(RepaintDecision.ScheduleTrailing, gate.Request(1_040));
        gate.TrailingFired(1_073);
        Assert.Equal(RepaintDecision.Now, gate.Request(1_200));
    }

    [Fact]
    public void ADragAtAHighRefreshRate_RepaintsAtMostOncePerInterval()
    {
        // 144 Hz pointer moves for one second, each changing the selection.
        var gate = new TrailingRepaintGate(RepaintThrottle.DefaultIntervalMs);
        var repaints = 0;
        long? trailingDue = null;
        for (var move = 0; move < 144; move++)
        {
            var t = 10_000 + move * 1000 / 144;
            if (trailingDue is { } due && t >= due)
            {
                gate.TrailingFired(due);
                repaints++;
                trailingDue = null;
            }

            switch (gate.Request(t))
            {
                case RepaintDecision.Now: repaints++; break;
                case RepaintDecision.ScheduleTrailing: trailingDue = t + gate.IntervalMs; break;
            }
        }

        Assert.InRange(repaints, 20, 31);
    }

    [Fact]
    public void TheMinimapsSelectionHandler_GoesThroughTheGate()
    {
        var src = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "MinimapOverviewControl.cs"))));
        Assert.Contains("private TrailingRepaintGate _selectionRepaint = new(RepaintThrottle.DefaultIntervalMs);", src);

        var start = src.IndexOf("private void OnSelectionChanged(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnSelectionChanged has moved — repoint this lint.");
        var body = src[start..src.IndexOf("private void OnSelectionTrailingRepaint()", start, StringComparison.Ordinal)];
        Assert.Contains("_selectionRepaint.Request(Environment.TickCount64)", body);
        Assert.DoesNotContain("InvalidateVisual()", body);
        // The one direct repaint is the leading edge's.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"RequestRepaint\(\)"));
        Assert.Contains("case RepaintDecision.Now:\n                RequestRepaint();", body.Replace("\r\n", "\n"));
    }
}
