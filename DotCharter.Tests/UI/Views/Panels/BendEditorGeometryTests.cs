using Avalonia;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Panels;

namespace DotCharter.Tests.UI.Views.Panels;

public class BendEditorGeometryTests
{
    private const double CanvasW = 400;
    private const double CanvasH = 200;

    [Fact]
    public void StepToY_TopOfPlot_WhenStepEqualsMaxStep()
    {
        var y = BendEditorGeometry.StepToY(BendEditorViewModel.MaxStep, 100);
        Assert.Equal(0.0, y, 6);
    }

    [Fact]
    public void StepToY_BottomOfPlot_WhenStepIsZero()
    {
        var y = BendEditorGeometry.StepToY(0.0, 100);
        Assert.Equal(100.0, y, 6);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(BendEditorViewModel.MaxStep, BendEditorViewModel.MaxStep)]
    [InlineData(2.0, 2.0)]
    public void YToStep_RoundTripsStepToY(double step, double expected)
    {
        var y = BendEditorGeometry.StepToY(step, 200);
        Assert.Equal(expected, BendEditorGeometry.YToStep(y, 200), 6);
    }

    [Fact]
    public void YToStep_ClampsBelowZero()
        => Assert.Equal(BendEditorViewModel.MaxStep, BendEditorGeometry.YToStep(-50, 100), 6);

    [Fact]
    public void YToStep_ClampsAbovePlot()
        => Assert.Equal(0.0, BendEditorGeometry.YToStep(500, 100), 6);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.124, 0.0)]
    [InlineData(0.13, 0.25)]
    [InlineData(0.30, 0.25)]
    [InlineData(0.40, 0.50)]
    [InlineData(6.5, BendEditorViewModel.MaxStep)] // clamped
    [InlineData(-1.0, 0.0)]                        // clamped
    public void SnapStepForDisplay_ClampsAndQuantizesToQuarter(double input, double expected)
        => Assert.Equal(expected, BendEditorGeometry.SnapStepForDisplay(input), 6);

    [Fact]
    public void TryGetPlotSize_FailsForTinyCanvas()
    {
        var ok = BendEditorGeometry.TryGetPlotSize(10, 10, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryGetPlotSize_StripsPaddingFromBothAxes()
    {
        var ok = BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        Assert.True(ok);
        Assert.Equal(CanvasW - BendEditorGeometry.PaddingLeft - BendEditorGeometry.PaddingRight, w, 6);
        Assert.Equal(CanvasH - BendEditorGeometry.PaddingTop - BendEditorGeometry.PaddingBottom, h, 6);
    }

    [Fact]
    public void PointToCanvas_FirstSlotMaxStep_LandsAtTopLeftOfPlot()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var pt = BendEditorGeometry.PointToCanvas(0, BendEditorViewModel.MaxStep, w, h);
        Assert.Equal(BendEditorGeometry.PaddingLeft, pt.X, 6);
        Assert.Equal(BendEditorGeometry.PaddingTop, pt.Y, 6);
    }

    [Fact]
    public void PointToCanvas_LastSlotZeroStep_LandsAtBottomRightOfPlot()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var pt = BendEditorGeometry.PointToCanvas(BendEditorViewModel.TimeSlotCount, 0.0, w, h);
        Assert.Equal(BendEditorGeometry.PaddingLeft + w, pt.X, 6);
        Assert.Equal(BendEditorGeometry.PaddingTop + h, pt.Y, 6);
    }

    [Fact]
    public void CanvasToSlotStep_TopLeftCorner_MapsToSlotZeroAtMaxStep()
    {
        var (slot, step) = BendEditorGeometry.CanvasToSlotStep(
            new Point(BendEditorGeometry.PaddingLeft, BendEditorGeometry.PaddingTop), CanvasW, CanvasH);
        Assert.Equal(0, slot);
        Assert.Equal(BendEditorViewModel.MaxStep, step, 6);
    }

    [Fact]
    public void CanvasToSlotStep_BottomRightCorner_MapsToLastSlotAtZero()
    {
        var (slot, step) = BendEditorGeometry.CanvasToSlotStep(
            new Point(CanvasW - BendEditorGeometry.PaddingRight, CanvasH - BendEditorGeometry.PaddingBottom),
            CanvasW, CanvasH);
        Assert.Equal(BendEditorViewModel.TimeSlotCount, slot);
        Assert.Equal(0.0, step, 6);
    }

    [Fact]
    public void CanvasToSlotStep_PointsLeftOfPlot_StillClampToSlotZero()
    {
        var (slot, _) = BendEditorGeometry.CanvasToSlotStep(new Point(0, 50), CanvasW, CanvasH);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void CanvasToSlotStep_PointsRightOfPlot_ClampToLastSlot()
    {
        var (slot, _) = BendEditorGeometry.CanvasToSlotStep(new Point(CanvasW + 200, 50), CanvasW, CanvasH);
        Assert.Equal(BendEditorViewModel.TimeSlotCount, slot);
    }

    [Fact]
    public void CanvasToSlotStep_DegenerateCanvas_ReturnsNullSlot()
    {
        var (slot, _) = BendEditorGeometry.CanvasToSlotStep(new Point(0, 0), 10, 10);
        Assert.Null(slot);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(3.0, true)]
    [InlineData(0.5, false)]
    [InlineData(2.0, false)]
    [InlineData(5.0, false)]
    public void IsMajorYTick_OnlyZeroOneAndThreeAreMajor(double step, bool expected)
        => Assert.Equal(expected, BendEditorGeometry.IsMajorYTick(step));

    [Fact]
    public void IsInsideCanvas_ChecksBothAxes()
    {
        Assert.True(BendEditorGeometry.IsInsideCanvas(new Point(10, 10), 100, 100));
        Assert.True(BendEditorGeometry.IsInsideCanvas(new Point(0, 0), 100, 100));
        Assert.True(BendEditorGeometry.IsInsideCanvas(new Point(100, 100), 100, 100));
        Assert.False(BendEditorGeometry.IsInsideCanvas(new Point(-1, 50), 100, 100));
        Assert.False(BendEditorGeometry.IsInsideCanvas(new Point(50, -1), 100, 100));
        Assert.False(BendEditorGeometry.IsInsideCanvas(new Point(101, 50), 100, 100));
        Assert.False(BendEditorGeometry.IsInsideCanvas(new Point(50, 101), 100, 100));
    }

    [Fact]
    public void HitTestPoint_ReturnsExactPointAtItsCenter()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var pts = new List<BendEditorViewModel.ControlPoint>
        {
            new(0, 0),
            new(6, 2.0),
            new(BendEditorViewModel.TimeSlotCount, BendEditorViewModel.MaxStep)
        };

        var center = BendEditorGeometry.PointToCanvas(pts[1], w, h);
        var hit = BendEditorGeometry.HitTestPoint(pts, center, w, h);
        Assert.Same(pts[1], hit);
    }

    [Fact]
    public void HitTestPoint_ReturnsNullWhenCursorIsFarFromAnyPoint()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var pts = new List<BendEditorViewModel.ControlPoint> { new(0, 0) };
        var hit = BendEditorGeometry.HitTestPoint(pts, new Point(CanvasW, 0), w, h);
        Assert.Null(hit);
    }

    [Fact]
    public void HitTestPoint_HitsWithinRadius_MissesJustOutside()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var p = new BendEditorViewModel.ControlPoint(4, 1.0);
        var pts = new List<BendEditorViewModel.ControlPoint> { p };
        var center = BendEditorGeometry.PointToCanvas(p, w, h);

        var inside = new Point(center.X + BendEditorGeometry.PointHitRadius - 0.5, center.Y);
        var outside = new Point(center.X + BendEditorGeometry.PointHitRadius + 0.5, center.Y);

        Assert.Same(p, BendEditorGeometry.HitTestPoint(pts, inside, w, h));
        Assert.Null(BendEditorGeometry.HitTestPoint(pts, outside, w, h));
    }

    [Fact]
    public void HitTestPoint_PrefersFirstMatchInIterationOrder()
    {
        BendEditorGeometry.TryGetPlotSize(CanvasW, CanvasH, out var w, out var h);
        var a = new BendEditorViewModel.ControlPoint(4, 1.0);
        var b = new BendEditorViewModel.ControlPoint(4, 1.0); // overlapping
        var pts = new List<BendEditorViewModel.ControlPoint> { a, b };
        var center = BendEditorGeometry.PointToCanvas(a, w, h);

        Assert.Same(a, BendEditorGeometry.HitTestPoint(pts, center, w, h));
    }

    [Fact]
    public void YTicks_CoverFullSemitoneRangeTopDown()
    {
        var ticks = BendEditorGeometry.YTicks;
        Assert.Equal(BendEditorViewModel.MaxStep, ticks[0].Step, 6);
        Assert.Equal(0.0, ticks[^1].Step, 6);

        for (var i = 1; i < ticks.Length; i++)
        {
            Assert.True(ticks[i].Step < ticks[i - 1].Step,
                $"YTicks must be strictly decreasing; idx {i - 1}->{i} broke ordering.");
        }
    }

    [Theory]
    [InlineData(1.0, 1.0, true)]
    [InlineData(1.0, 1.0005, true)]
    [InlineData(1.0, 1.002, false)]
    [InlineData(0.0, 2.0, false)]
    public void IsHoldSegment_TreatsSubMilliDeltaAsHold(double a, double b, bool expected) => Assert.Equal(expected, BendEditorGeometry.IsHoldSegment(a, b));

    [Fact]
    public void GetCurveBezierControls_PutsControlsAtGpRatios()
    {
        // Pin the magic 0.55 / 0.15 ratios — a long flat lead-in and a
        // short snap-up at the end.
        var p0 = new Point(0, 50);
        var p1 = new Point(100, 0);
        var (c0, c1) = BendEditorGeometry.GetCurveBezierControls(p0, p1);

        // c0 is on p0's Y, 55% along the way.
        Assert.Equal(55.0, c0.X, 6);
        Assert.Equal(50.0, c0.Y, 6);
        // c1 is on p1's Y, 15% before the end (i.e. at 85%).
        Assert.Equal(85.0, c1.X, 6);
        Assert.Equal(0.0, c1.Y, 6);
    }

    [Fact]
    public void GetCurveBezierControls_HandlesNegativeDx()
    {
        // Bend going backwards in time (shouldn't happen in practice but
        // pin the math anyway — controls must stay on the source/target Y).
        var p0 = new Point(100, 0);
        var p1 = new Point(0, 50);
        var (c0, c1) = BendEditorGeometry.GetCurveBezierControls(p0, p1);
        Assert.Equal(0.0, c0.Y, 6);
        Assert.Equal(50.0, c1.Y, 6);
    }

    private static BendEditorViewModel.ControlPoint[] Pts(params (int Slot, double Step)[] pts)
        => [.. pts.Select(p => new BendEditorViewModel.ControlPoint(p.Slot, p.Step))];

    [Fact]
    public void BuildSustainSegments_RisesFromZeroBeforeTheFirstPoint_AndHoldsAfterTheLast()
    {
        // The owner's case: ½ at slot 3, Full at slot 8. Playback starts at 0 on
        // the attack and rises to ½ (not a hold at ½), and holds Full to the end.
        var segs = BendEditorGeometry.BuildSustainSegments(Pts((3, 1.0), (8, 2.0)));

        Assert.Equal(
        [
            new BendEditorGeometry.SustainSegment(0, 0.0, 3, 1.0),
            new BendEditorGeometry.SustainSegment(3, 1.0, 8, 2.0),
            new BendEditorGeometry.SustainSegment(8, 2.0, BendEditorViewModel.TimeSlotCount, 2.0)
        ], segs);
        Assert.False(segs[0].IsHold);
        Assert.True(segs[^1].IsHold);
    }

    [Fact]
    public void BuildSustainSegments_PreBendOnTheAttack_HoldsFromTheStart()
    {
        var segs = BendEditorGeometry.BuildSustainSegments(Pts((0, 2.0), (6, 2.0)));

        Assert.Equal(0, segs[0].FromSlot);
        Assert.Equal(2.0, segs[0].FromStep, 6);
        Assert.All(segs, s => Assert.True(s.IsHold));
        Assert.Equal(BendEditorViewModel.TimeSlotCount, segs[^1].ToSlot);
    }

    [Fact]
    public void BuildSustainSegments_SinglePoint_CoversTheWholeSustain()
    {
        var segs = BendEditorGeometry.BuildSustainSegments(Pts((4, 2.0)));

        Assert.Equal(
        [
            new BendEditorGeometry.SustainSegment(0, 0.0, 4, 2.0),
            new BendEditorGeometry.SustainSegment(4, 2.0, BendEditorViewModel.TimeSlotCount, 2.0)
        ], segs);
    }

    [Fact]
    public void BuildSustainSegments_PointsOnBothEnds_AddNothing()
    {
        var segs = BendEditorGeometry.BuildSustainSegments(Pts((0, 0.0), (BendEditorViewModel.TimeSlotCount, 2.0)));

        Assert.Equal([new BendEditorGeometry.SustainSegment(0, 0.0, BendEditorViewModel.TimeSlotCount, 2.0)], segs);
    }

    [Fact]
    public void BuildSustainSegments_NoPoints_DrawsNothing()
        => Assert.Empty(BendEditorGeometry.BuildSustainSegments([]));
}
