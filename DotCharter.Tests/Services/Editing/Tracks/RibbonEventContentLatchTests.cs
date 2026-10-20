using System.Collections.ObjectModel;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing.Tracks;

/// <summary>
///     #309: <c>ChartRenderer.TopRibbon</c> walked the whole event list on every call — every
///     frame and every hit test — on a chart with phrases but no sections, because the walk only
///     stops once it has seen both. The answer is latched now; these pin when it is re-walked.
/// </summary>
public sealed class RibbonEventContentLatchTests
{
    private static ObservableCollection<EventPoint> PhrasesOnly(int count)
    {
        var events = new ObservableCollection<EventPoint>();
        for (var i = 0; i < count; i++)
            events.Add(new EventPoint { Position = new FractionalPosition(i), Phrase = "riff" + i });
        return events;
    }

    [Fact]
    public void APhrasesOnlyChart_IsWalkedOnce_ForAFrameBurst()
    {
        var latch = new RibbonEventContentLatch();
        var events = PhrasesOnly(400);

        for (var frame = 0; frame < 120; frame++)
            Assert.Equal((false, true), latch.Get(events, 7, 1_000 + frame));

        Assert.Equal(1, latch.ScanCount);
    }

    [Fact]
    public void AnInPlaceEdit_ThatMovesTheContentVersion_IsSeenAtOnce()
    {
        var latch = new RibbonEventContentLatch();
        var events = PhrasesOnly(10);
        Assert.Equal((false, true), latch.Get(events, 7, 1_000));

        events[4].Section = "chorus";  // a MutateEventPointsAction writes in place, then history moves
        Assert.Equal((false, true), latch.Get(events, 7, 1_001));
        Assert.Equal((true, true), latch.Get(events, 8, 1_002));
        Assert.Equal(2, latch.ScanCount);
    }

    [Fact]
    public void AnAddOrAnotherList_IsSeenWithoutAVersion()
    {
        var latch = new RibbonEventContentLatch();
        var events = PhrasesOnly(10);
        latch.Get(events, 7, 1_000);

        events.Add(new EventPoint { Position = new FractionalPosition(20), Section = "verse" });
        Assert.Equal((true, true), latch.Get(events, 7, 1_001));

        Assert.Equal((false, false), latch.Get(new ObservableCollection<EventPoint>(), 7, 1_002));
        Assert.Equal(3, latch.ScanCount);
    }

    [Fact]
    public void AnEditNoVersionSaw_IsSeenWithinTheStalenessBound()
    {
        var latch = new RibbonEventContentLatch();
        var events = PhrasesOnly(10);
        latch.Get(events, 7, 1_000);

        events[2].Section = "intro";
        Assert.Equal((false, true), latch.Get(events, 7, 1_000 + RibbonEventContentLatch.StaleAfterMs - 1));
        Assert.Equal((true, true), latch.Get(events, 7, 1_000 + RibbonEventContentLatch.StaleAfterMs));
    }

    /// <summary>The renderer asks the latch, and no longer walks the list itself.</summary>
    [Fact]
    public void TopRibbon_GoesThroughTheLatch()
    {
        var src = SourceText.StripCommentLines(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ChartRenderer.cs")));
        var start = src.IndexOf("public RibbonLayout TopRibbon", StringComparison.Ordinal);
        Assert.True(start >= 0, "TopRibbon has moved — repoint this lint.");
        var end = src.IndexOf("public double TopMargin", start, StringComparison.Ordinal);
        var getter = src[start..end];

        Assert.Contains("_ribbonEvents.Get(ctx.EventPoints, Editor.HistoryRevision, Environment.TickCount64)", getter);
        Assert.DoesNotContain("for (", getter);
        Assert.DoesNotContain("foreach", getter);
    }
}
