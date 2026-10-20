using Avalonia;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.Views.Panels;

/// <summary>
/// Pure coordinate / hit-testing helpers extracted from <see cref="BendEditorControl"/>.
/// All methods are deterministic and depend only on their arguments, so they are
/// safe to unit test without spinning up Avalonia.
/// </summary>
public static class BendEditorGeometry
{
    public const double PaddingLeft = 32;
    public const double PaddingRight = 32;
    public const double PaddingTop = 16;
    public const double PaddingBottom = 18;
    public const double PointHitRadius = 11.0;
    public const double PointDrawRadius = 5.5;
    public const double SelectedDrawR = 7.5;
    public const double HoverGhostR = 5.0;

    /// <summary>
    /// Y-axis tick marks, top-down. Step is in semitones; label is the GP-style
    /// tone label drawn beside the gridline.
    /// </summary>
    public static readonly (double Step, string Label)[] YTicks =
    [
        (6.0, "3"),
        (5.0, "2½"),
        (4.0, "2"),
        (3.0, "1½"),
        (2.0, "Full"),
        (1.0, "½"),
        (0.0, "0")
    ];

    /// <summary>
    /// Returns true if the supplied tick step lies on a "major" gridline
    /// (octave boundary, full-tone or zero) — drawn solid; minor ticks dashed.
    /// </summary>
    public static bool IsMajorYTick(double step)
        => Math.Abs(step) < 1e-6 || Math.Abs(step - 1.0) < 1e-6 || Math.Abs(step - 3.0) < 1e-6;

    /// <summary>
    /// Compute the available plot area inside the control given its outer
    /// canvas size. Returns false if the canvas is too small to draw into.
    /// </summary>
    public static bool TryGetPlotSize(double canvasWidth, double canvasHeight, out double plotW, out double plotH)
    {
        plotW = Math.Max(1.0, canvasWidth - PaddingLeft - PaddingRight);
        plotH = Math.Max(1.0, canvasHeight - PaddingTop - PaddingBottom);
        return plotW > 1 && plotH > 1;
    }

    /// <summary>
    /// Map a Y-axis step (0..MaxStep semitones) to a canvas-relative Y offset
    /// inside the plot area (0 at top, plotHeight at bottom).
    /// </summary>
    public static double StepToY(double step, double plotHeight)
        => plotHeight * (1.0 - step / BendEditorViewModel.MaxStep);

    /// <summary>Inverse of <see cref="StepToY"/>; clamps to the valid range.</summary>
    public static double YToStep(double y, double plotHeight)
    {
        var t = 1.0 - Math.Clamp(y / plotHeight, 0.0, 1.0);
        return t * BendEditorViewModel.MaxStep;
    }

    /// <summary>Quarter-step quantization used for the ghost preview point.</summary>
    public static double SnapStepForDisplay(double step)
    {
        var clamped = Math.Clamp(step, 0.0, BendEditorViewModel.MaxStep);
        return Math.Round(clamped / BendEditorViewModel.StepQuantum) * BendEditorViewModel.StepQuantum;
    }

    /// <summary>
    /// Convert a (timeSlot, step) pair to a canvas-space point including padding.
    /// </summary>
    public static Point PointToCanvas(int timeSlot, double step, double plotW, double plotH)
    {
        var x = PaddingLeft + plotW * timeSlot / BendEditorViewModel.TimeSlotCount;
        var y = PaddingTop + StepToY(step, plotH);
        return new Point(x, y);
    }

    public static Point PointToCanvas(BendEditorViewModel.ControlPoint p, double plotW, double plotH)
        => PointToCanvas(p.TimeSlot, p.Step, plotW, plotH);

    /// <summary>
    /// Convert a canvas-space point back to (slot, step). Returns slot=null when
    /// the canvas is degenerate (e.g. control not yet measured).
    /// </summary>
    public static (int? slot, double step) CanvasToSlotStep(Point pt, double canvasWidth, double canvasHeight)
    {
        var plotW = canvasWidth - PaddingLeft - PaddingRight;
        var plotH = canvasHeight - PaddingTop - PaddingBottom;
        if (plotW <= 1 || plotH <= 1) return (null, 0);

        var relX = (pt.X - PaddingLeft) / plotW;
        var relY = pt.Y - PaddingTop;

        var slot = (int)Math.Round(Math.Clamp(relX, 0.0, 1.0) * BendEditorViewModel.TimeSlotCount);
        var step = YToStep(relY, plotH);
        return (slot, step);
    }

    /// <summary>True if the point is inside the outer canvas bounds.</summary>
    public static bool IsInsideCanvas(Point p, double canvasWidth, double canvasHeight)
        => p.X >= 0 && p.X <= canvasWidth && p.Y >= 0 && p.Y <= canvasHeight;

    /// <summary>
    /// Return the first control point whose drawn dot is within
    /// <see cref="PointHitRadius"/> of the cursor, or null if none.
    /// Mirrors the radial hit-test in the original control.
    /// </summary>
    public static BendEditorViewModel.ControlPoint? HitTestPoint(
        IReadOnlyList<BendEditorViewModel.ControlPoint> points,
        Point pt,
        double plotW,
        double plotH)
    {
        var rSq = PointHitRadius * PointHitRadius;
        foreach (var p in points)
        {
            var cp = PointToCanvas(p, plotW, plotH);
            var dx = cp.X - pt.X;
            var dy = cp.Y - pt.Y;
            if (dx * dx + dy * dy <= rSq) return p;
        }

        return null;
    }

    /// <summary>
    /// Threshold below which two consecutive control-point steps are
    /// considered equal — drawn as a "hold" (horizontal dashed segment)
    /// rather than a curved bend.
    /// </summary>
    public const double HoldEpsilon = 0.001;

    /// <summary>
    /// True if the segment between two adjacent control points should
    /// render as a hold (no pitch movement) rather than a bend curve.
    /// </summary>
    public static bool IsHoldSegment(double prevStep, double curStep)
        => Math.Abs(prevStep - curStep) < HoldEpsilon;

    /// <summary>One drawn stretch of the bend graph, in (slot, step) space.</summary>
    public readonly record struct SustainSegment(int FromSlot, double FromStep, int ToSlot, double ToStep)
    {
        /// <summary>Flat — drawn as a straight hold, not a bend curve.</summary>
        public bool IsHold => IsHoldSegment(FromStep, ToStep);
    }

    /// <summary>
    /// The segments that draw <paramref name="points" /> (sorted by slot) over the
    /// note's WHOLE sustain, slot 0 to <see cref="BendEditorViewModel.TimeSlotCount" />,
    /// matching what playback does (synth <c>ChartNoteTable::bendAt</c>, voice
    /// <c>dcvoice_timeline bendAt</c>, highway <c>NoteRenderMath.GetBendOffset</c>):
    /// before the first point the bend rises from 0 at the attack — it is NOT
    /// held at the first value, unless that point sits on the attack (a
    /// pre-bend); between points it moves point to point; after the last point
    /// it holds the last value to the end of the sustain.
    /// </summary>
    public static IReadOnlyList<SustainSegment> BuildSustainSegments(
        IReadOnlyList<BendEditorViewModel.ControlPoint> points)
    {
        var segments = new List<SustainSegment>(points.Count + 1);
        if (points.Count == 0) return segments;

        var first = points[0];
        if (first.TimeSlot > 0) segments.Add(new SustainSegment(0, 0.0, first.TimeSlot, first.Step));

        for (var i = 1; i < points.Count; i++)
            segments.Add(new SustainSegment(points[i - 1].TimeSlot, points[i - 1].Step, points[i].TimeSlot, points[i].Step));

        var last = points[^1];
        if (last.TimeSlot < BendEditorViewModel.TimeSlotCount)
            segments.Add(new SustainSegment(last.TimeSlot, last.Step, BendEditorViewModel.TimeSlotCount, last.Step));

        return segments;
    }

    /// <summary>
    /// Compute the cubic-bezier control points that draw a single bend
    /// segment. Mirrors GP's signature curve shape: a long flat lead-in
    /// (55% of dx along the source's Y) and a short snap-up at the end
    /// (15% before the destination, at the destination's Y).
    /// </summary>
    public static (Point C0, Point C1) GetCurveBezierControls(Point p0, Point p1)
    {
        var dx = p1.X - p0.X;
        var c0 = new Point(p0.X + dx * 0.55, p0.Y);
        var c1 = new Point(p1.X - dx * 0.15, p1.Y);
        return (c0, c1);
    }
}
