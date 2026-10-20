using DotCharter.Rendering.Data.Render;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI;

/// <summary>
///     0.19 B1 — pure density-LOD decision for the 2D timeline.
///     px-per-column = windowSpanMs × pixelsPerMs / columns; the thresholds
///     live in <see cref="ChartEditorConstants.RenderMetrics" />.
/// </summary>
public class NoteLodTests
{
    private const double WindowMs = 10_000.0;
    private const double Zoom = 0.2; // px per ms → 2000 px window

    [Fact]
    public void SparseChart_RendersFullDetail() =>
        // 2000 px / 50 columns = 40 px per note — plenty of room.
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, Zoom, 50));

    [Fact]
    public void MediumDensity_DropsLabels() =>
        // 2000 px / 150 columns ≈ 13 px per note — gems fit, labels don't.
        Assert.Equal(NoteLevelOfDetail.Compact, NoteLod.Compute(WindowMs, Zoom, 150));

    [Fact]
    public void HighDensity_RendersMarks() =>
        // 2000 px / 500 columns = 4 px per note — sub-gem density.
        Assert.Equal(NoteLevelOfDetail.Marks, NoteLod.Compute(WindowMs, Zoom, 500));

    [Fact]
    public void Thresholds_AreExclusiveUpperBounds()
    {
        // Exactly AT a threshold stays in the higher-detail tier.
        var compactMax = ChartEditorConstants.RenderMetrics.NoteLodCompactMaxPxPerColumn;
        var marksMax = ChartEditorConstants.RenderMetrics.NoteLodMarksMaxPxPerColumn;

        var columnsAtCompactMax = (int)(WindowMs * Zoom / compactMax);
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, Zoom, columnsAtCompactMax));
        Assert.Equal(NoteLevelOfDetail.Compact, NoteLod.Compute(WindowMs, Zoom, columnsAtCompactMax + 1));

        var columnsAtMarksMax = (int)(WindowMs * Zoom / marksMax);
        Assert.Equal(NoteLevelOfDetail.Compact, NoteLod.Compute(WindowMs, Zoom, columnsAtMarksMax));
        Assert.Equal(NoteLevelOfDetail.Marks, NoteLod.Compute(WindowMs, Zoom, columnsAtMarksMax + 1));
    }

    [Theory]
    [InlineData(0)] // empty window
    [InlineData(-3)] // defensive
    public void NoColumns_IsAlwaysFullDetail(int columns) => Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, Zoom, columns));

    [Fact]
    public void DegenerateGeometry_IsAlwaysFullDetail()
    {
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(0, Zoom, 100));
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, 0, 100));
    }

    [Fact]
    public void ZoomingIn_RestoresDetail()
    {
        // Same 500-column chart: 0.2 px/ms → Marks, 2.0 px/ms → Full.
        Assert.Equal(NoteLevelOfDetail.Marks, NoteLod.Compute(WindowMs, 0.2, 500));
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, 2.0, 500));
    }

    // ─── The count must describe the VIEWPORT, not the snapshot window ────────
    // The snapshot spans the view plus both ±5 s culling buffers, so counting
    // over it let off-screen chart decide the on-screen level of detail.

    private static RenderNote[] NotesAt(params double[] times)
    {
        var notes = new RenderNote[times.Length];
        for (var i = 0; i < times.Length; i++) notes[i] = new RenderNote { Time = times[i] };
        return notes;
    }

    [Fact]
    public void CountInWindow_CountsOnlyTheColumnsInsideTheViewport()
    {
        // In window: 0, 500, 1000, 1920 (endMs is inclusive). Out: the two before
        // the view start and the two past its end.
        var notes = NotesAt(-4000, -10, 0, 500, 1000, 1920, 2000, 7000);

        Assert.Equal(4, NoteLod.CountInWindow(notes.AsSpan(), 0, 1920));
    }

    [Fact]
    public void CountInWindow_StopsAtTheFirstColumnPastTheViewport()
    {
        // The arrays are time-ascending; the early break is what keeps this cheap
        // enough to run per frame.
        var notes = NotesAt(0, 100, 5000, 6000, 7000);

        Assert.Equal(2, NoteLod.CountInWindow(notes.AsSpan(), 0, 1920));
    }

    [Fact]
    public void CountInWindow_ChordsUseTheSameRule()
    {
        var chords = new[]
        {
            new RenderChord { Time = -3000 },
            new RenderChord { Time = 250 },
            new RenderChord { Time = 9000 }
        };

        Assert.Equal(1, NoteLod.CountInWindow(chords.AsSpan(), 0, 1920));
    }

    [Fact]
    public void AnOffScreenShredSection_DoesNotStripLabelsFromASparseViewport()
    {
        // A 1920 px viewport zoomed out to 0.2 px/ms spans 9,600 ms, and the
        // snapshot adds both ±5 s buffers on top (19,600 ms). 40 sparse columns on
        // screen sit at 48 px each — comfortably Full. A shred section of 200
        // columns inside the off-screen buffer drags the SNAPSHOT average to 16 px
        // and strips the visible notes' fret labels for no visible reason.
        const double zoom = 0.2;
        const double viewportMs = 1920 / zoom;
        const double snapshotWindowMs = viewportMs + 2 * 5000.0;
        const int onScreen = 40;
        const int offScreen = 200;

        Assert.Equal(NoteLevelOfDetail.Compact,
            NoteLod.Compute(snapshotWindowMs, zoom, onScreen + offScreen));

        Assert.Equal(NoteLevelOfDetail.Full,
            NoteLod.Compute(viewportMs, zoom, onScreen));
    }

    // ─── Hysteresis (#114) ────────────────────────────────────────────────────
    // Scrolling changes the visible column count by one as a note enters on the
    // right before or after one leaves on the left. Without a band, a passage
    // whose width/N and width/(N+1) straddle a threshold flipped style on every
    // such step.

    private const double ViewportPx = 900.0;
    private const double FrameMs = 1000.0 / 60.0;

    /// <summary>LOD changes over a 60 fps scroll, counted AFTER the first frame
    /// settles the level (that one is a decision, not a flip).</summary>
    private static int LodChangesWhileScrolling(RenderNote[] notes, double zoom, double firstViewStartMs,
        int frames, bool hysteresis)
    {
        var spanMs = ViewportPx / zoom;
        var changes = 0;
        var level = NoteLevelOfDetail.Full;
        for (var f = 0; f < frames; f++)
        {
            var start = firstViewStartMs + f * FrameMs;
            var count = NoteLod.CountInWindow(notes.AsSpan(), start, start + spanMs);
            var next = hysteresis
                ? NoteLod.Compute(spanMs, zoom, count, level)
                : NoteLod.Compute(spanMs, zoom, count);
            if (f > 0 && next != level) changes++;
            level = next;
        }

        return changes;
    }

    private static RenderNote[] EvenlySpaced(double fromMs, double toMs, double spacingMs)
    {
        var count = (int)((toMs - fromMs) / spacingMs);
        var notes = new RenderNote[count];
        for (var i = 0; i < count; i++) notes[i] = new RenderNote { Time = fromMs + i * spacingMs };
        return notes;
    }

    [Fact]
    public void AConstantDensityPassage_NeverChangesLevelWhileScrolling()
    {
        // The issue's probe: sixteenths at 97 BPM (154.64 ms apart) at 0.03867
        // px/ms — ~5.98 px per column, right on the Marks threshold — scrolled at
        // 60 fps for 10 s. The window always holds 150 or 151 columns.
        const double spacingMs = 60_000.0 / 97 / 4;
        const double zoom = 0.03867;
        var notes = EvenlySpaced(0, 60_000, spacingMs);

        var withoutBand = LodChangesWhileScrolling(notes, zoom, 5_000, 600, hysteresis: false);
        var withBand = LodChangesWhileScrolling(notes, zoom, 5_000, 600, hysteresis: true);

        // The fixture really sits on the threshold: the bare classification flips
        // dozens of times (129 in the issue's probe). Otherwise the 0 below proves
        // nothing.
        Assert.True(withoutBand > 50, $"fixture does not straddle the threshold: {withoutBand} flips without hysteresis");
        Assert.Equal(0, withBand);
    }

    [Theory]
    [InlineData(false)] // sparse → dense
    [InlineData(true)] // dense → sparse
    public void ARealDensityChange_StillChangesLevelExactlyOnce(bool denseFirst)
    {
        // 0.2 px/ms, 900 px = a 4,500 ms window. Sparse: 125 ms apart = 25 px per
        // column (Full). Dense: 50 ms apart = 10 px (Compact). The scroll runs the
        // window from wholly one passage to wholly the other.
        const double zoom = 0.2;
        const double boundaryMs = 10_000;
        var (firstMs, secondMs) = denseFirst ? (50.0, 125.0) : (125.0, 50.0);
        var notes = EvenlySpaced(0, boundaryMs, firstMs)
            .Concat(EvenlySpaced(boundaryMs, 30_000, secondMs)).ToArray();

        // viewStart 2,000 → 2,000 + 1,200 frames × 16.7 ms = 22,000.
        Assert.Equal(1, LodChangesWhileScrolling(notes, zoom, 2_000, 1_200, hysteresis: true));
    }

    [Fact]
    public void TheBand_HoldsThePreviousLevelNearAThreshold_AndLetsAClearMoveThrough()
    {
        var compactMax = ChartEditorConstants.RenderMetrics.NoteLodCompactMaxPxPerColumn;
        var marksMax = ChartEditorConstants.RenderMetrics.NoteLodMarksMaxPxPerColumn;
        var h = ChartEditorConstants.RenderMetrics.NoteLodHysteresis;

        // One column over a window of `px` ms at 1 px/ms is `px` px per column.
        static NoteLevelOfDetail At(double pxPerColumn, NoteLevelOfDetail previous) =>
            NoteLod.Compute(pxPerColumn, 1.0, 1, previous);

        // Just past a threshold keeps the level it came from …
        Assert.Equal(NoteLevelOfDetail.Full, At(compactMax * (1 - h / 2), NoteLevelOfDetail.Full));
        Assert.Equal(NoteLevelOfDetail.Compact, At(compactMax * (1 + h / 2), NoteLevelOfDetail.Compact));
        Assert.Equal(NoteLevelOfDetail.Compact, At(marksMax * (1 - h / 2), NoteLevelOfDetail.Compact));
        Assert.Equal(NoteLevelOfDetail.Marks, At(marksMax * (1 + h / 2), NoteLevelOfDetail.Marks));

        // … and clearing the band changes it.
        Assert.Equal(NoteLevelOfDetail.Compact, At(compactMax * (1 - 2 * h), NoteLevelOfDetail.Full));
        Assert.Equal(NoteLevelOfDetail.Full, At(compactMax * (1 + 2 * h), NoteLevelOfDetail.Compact));
        Assert.Equal(NoteLevelOfDetail.Marks, At(marksMax * (1 - 2 * h), NoteLevelOfDetail.Compact));
        Assert.Equal(NoteLevelOfDetail.Compact, At(marksMax * (1 + 2 * h), NoteLevelOfDetail.Marks));

        // A jump across both thresholds lands where it belongs in one frame.
        Assert.Equal(NoteLevelOfDetail.Marks, At(1, NoteLevelOfDetail.Full));
        Assert.Equal(NoteLevelOfDetail.Full, At(100, NoteLevelOfDetail.Marks));
    }

    [Fact]
    public void TheHysteresisOverload_KeepsTheEmptyAndDegenerateRules()
    {
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, Zoom, 0, NoteLevelOfDetail.Marks));
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(0, Zoom, 100, NoteLevelOfDetail.Marks));
        Assert.Equal(NoteLevelOfDetail.Full, NoteLod.Compute(WindowMs, 0, 100, NoteLevelOfDetail.Compact));
    }
}
