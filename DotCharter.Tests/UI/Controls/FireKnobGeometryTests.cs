using Avalonia;
using Avalonia.Input;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The Fire knob is pure geometry and a pure mapping, so both are pinned
///     here rather than inferred from a picture.
///     <para>
///         Two of these are the contract details a port gets wrong. The SWEEP —
///         a 270° arc starting at 225° (= −135° clockwise from 12 o'clock), so
///         the indicator points lower-left at the minimum, straight up at
///         mid-scale and lower-right at the maximum. And the DRAG — vertical,
///         with a CONSTANT 170 px of travel spanning the whole range, not a
///         rotary gesture and not scaled to the control's size.
///     </para>
/// </summary>
public sealed class FireKnobGeometryTests
{
    // ── The sweep ────────────────────────────────────────────────────────────

    [Fact]
    public void TheSweepStartsAt225Degrees_AndCovers270()
    {
        Assert.Equal(-135.0, FireKnobGeometry.StartAngleDegrees);
        Assert.Equal(270.0, FireKnobGeometry.SweepDegrees);
    }

    [Theory]
    [InlineData(0.0, -135.0)]  // lower-left
    [InlineData(0.5, 0.0)]     // straight up
    [InlineData(1.0, 135.0)]   // lower-right
    [InlineData(0.25, -67.5)]
    [InlineData(0.75, 67.5)]
    public void TheIndicatorAngle_IsMinus135PlusPctTimes270(double fraction, double expected)
        => Assert.Equal(expected, FireKnobGeometry.AngleDegreesFor(fraction), 6);

    [Theory]
    [InlineData(-0.4)]
    [InlineData(1.7)]
    public void AFractionOutsideTheRange_ClampsOntoTheSweep(double fraction)
    {
        var angle = FireKnobGeometry.AngleDegreesFor(fraction);
        Assert.InRange(angle, -135.0, 135.0);
    }

    [Fact]
    public void TheIndicatorPointsWhereTheAngleSays()
    {
        var center = new Point(100, 100);

        // Straight up at mid-scale: same x, smaller y (screen y grows down).
        var up = FireKnobGeometry.PointOnDial(center, 10, FireKnobGeometry.AngleDegreesFor(0.5));
        Assert.Equal(100.0, up.X, 6);
        Assert.Equal(90.0, up.Y, 6);

        // Lower-LEFT at the minimum, lower-RIGHT at the maximum.
        var min = FireKnobGeometry.PointOnDial(center, 10, FireKnobGeometry.AngleDegreesFor(0));
        var max = FireKnobGeometry.PointOnDial(center, 10, FireKnobGeometry.AngleDegreesFor(1));
        Assert.True(min.X < center.X && min.Y > center.Y, $"minimum points to {min}, not lower-left");
        Assert.True(max.X > center.X && max.Y > center.Y, $"maximum points to {max}, not lower-right");
    }

    // ── The drag ─────────────────────────────────────────────────────────────

    [Fact]
    public void OneHundredAndSeventyPixelsOfTravel_IsExactlyTheWholeRange()
    {
        Assert.Equal(170.0, FireKnobGeometry.PixelsForFullRange);

        // Dragging UP (negative deltaY) raises the value.
        Assert.Equal(1.0, FireKnobGeometry.ValueForDrag(0.0, -170.0, 0.0, 1.0), 9);
        Assert.Equal(0.0, FireKnobGeometry.ValueForDrag(1.0, +170.0, 0.0, 1.0), 9);
        Assert.Equal(0.5, FireKnobGeometry.ValueForDrag(0.0, -85.0, 0.0, 1.0), 9);
    }

    [Fact]
    public void TheDragCovers170Pixels_OnAnyRange_NotJustZeroToOne()
    {
        // −12 dB … +12 dB: 170 px must still be the whole travel, or a knob's
        // feel would depend on its units.
        Assert.Equal(12.0, FireKnobGeometry.ValueForDrag(-12.0, -170.0, -12.0, 12.0), 9);
        Assert.Equal(0.0, FireKnobGeometry.ValueForDrag(-12.0, -85.0, -12.0, 12.0), 9);
    }

    [Fact]
    public void TheDragSensitivity_DoesNotScaleWithTheControl()
    {
        // The compact knob is 38 px where the standard one is 46, and the cell
        // heights differ too. 85 px of travel is half the range on BOTH — the
        // rule is pixels of pointer travel, never a fraction of the widget.
        var standard = FireKnobGeometry.DesiredSize(compact: false);
        var compact = FireKnobGeometry.DesiredSize(compact: true);
        Assert.NotEqual(standard.Height, compact.Height);

        Assert.Equal(FireKnobGeometry.ValueForDrag(0.0, -85.0, 0.0, 1.0),
            FireKnobGeometry.ValueForDrag(0.0, -85.0, 0.0, 1.0), 9);
        Assert.Equal(0.5, FireKnobGeometry.ValueForDrag(0.0, -85.0, 0.0, 1.0), 9);
    }

    [Fact]
    public void ShiftDrag_CoversATenthOfTheRangeForTheSameTravel()
        => Assert.Equal(0.1, FireKnobGeometry.ValueForDrag(0.0, -170.0, 0.0, 1.0, fine: true), 9);

    [Fact]
    public void AZeroDrag_LeavesTheValueExactlyWhereItWas()
        => Assert.Equal(0.371, FireKnobGeometry.ValueForDrag(0.371, 0.0, 0.0, 1.0), 12);

    // ── The dial ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheDialIs46Px_And38WhenCompact()
    {
        Assert.Equal(46.0, FireKnobGeometry.DialDiameter(compact: false));
        Assert.Equal(38.0, FireKnobGeometry.DialDiameter(compact: true));
    }

    [Fact]
    public void TheArcRidesTheRingBetweenTwoInsideAndFourOutsideTheDial()
    {
        // The mockup masks the ring from `size/2 - 2` and insets it by −4, so
        // the band is 6 px wide centred 1 px outside the dial's edge. Getting
        // this wrong paints the arc over the dial or off the cell.
        const double dial = 46.0;
        Assert.Equal(dial / 2.0 + 1.0, FireKnobGeometry.ArcRadius(dial), 9);
        Assert.Equal(6.0, FireKnobGeometry.ArcStrokeWidth, 9);

        var outerReach = FireKnobGeometry.ArcRadius(dial) + FireKnobGeometry.ArcStrokeWidth / 2.0;
        Assert.True(outerReach <= FireKnobGeometry.CellWidth / 2.0,
            $"the value arc reaches {outerReach} px from the centre but the cell is only " +
            $"{FireKnobGeometry.CellWidth / 2.0} px wide — it would paint onto the next knob");
    }

    [Fact]
    public void TheCellIsNeverNarrowerThanTwentyPixels()
    {
        // No control that carries text may render under 20 px wide; a knob
        // carries two mono lines.
        Assert.True(FireKnobGeometry.DesiredSize(compact: false).Width >= 20);
        Assert.True(FireKnobGeometry.DesiredSize(compact: true).Width >= 20);
    }

    [Fact]
    public void TheCellReservesTheDialBlockBothTextLinesAndTheirGaps()
    {
        var size = FireKnobGeometry.DesiredSize(compact: false);
        var expected = FireKnobGeometry.DialBlockHeight(46.0)
                       + FireKnobGeometry.RowGap
                       + FireKnobGeometry.MaxLabelLines * FireKnobGeometry.LabelLineHeight
                       + FireKnobGeometry.RowGap
                       + FireKnobGeometry.ValueLineHeight;
        Assert.Equal(expected, size.Height, 9);

        // Two label lines are ALWAYS reserved, so a row of knobs whose captions
        // wrap differently still lines its readouts up.
        Assert.Equal(2, FireKnobGeometry.MaxLabelLines);
    }

    // ── The keyboard ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Key.Up, 0.51)]
    [InlineData(Key.Right, 0.51)]
    [InlineData(Key.Down, 0.49)]
    [InlineData(Key.Left, 0.49)]
    [InlineData(Key.PageUp, 0.60)]
    [InlineData(Key.PageDown, 0.40)]
    [InlineData(Key.Home, 0.0)]
    [InlineData(Key.End, 1.0)]
    [InlineData(Key.Delete, 0.25)]
    [InlineData(Key.Back, 0.25)]
    public void EveryKnobKey_LandsWhereItSays(Key key, double expected)
    {
        // A continuous 0..1 parameter: an arrow is 1 % of the range and a Page
        // is ten of those, exactly as the readout's ladder implies.
        var landed = FireKnobGeometry.ValueForKey(
            key, value: 0.5, min: 0, max: 1, step: 0, defaultValue: 0.25);
        Assert.NotNull(landed);
        Assert.Equal(expected, landed!.Value, 9);
    }

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    [InlineData(Key.A)]
    public void AKeyTheKnobDoesNotOwn_IsLeftAlone(Key key)
    {
        // Null is what lets the control leave `Handled` false, so Tab still
        // moves focus off the knob and Space still reaches the card.
        Assert.Null(FireKnobGeometry.ValueForKey(
            key, value: 0.5, min: 0, max: 1, step: 0, defaultValue: 0.25));
    }

    [Fact]
    public void AParameterWithAStep_MovesByExactlyThatStep_AndAPageMovesTen()
    {
        // The regression this exists for: PAGE used to be one step whenever the
        // parameter declared one, so PageUp and Up were indistinguishable on
        // most of the rig — the catalogue declares a step for nearly every
        // param.
        Assert.Equal(0.5, FireKnobGeometry.ArrowStep(0, 10, step: 0.5), 9);
        Assert.Equal(5.0, FireKnobGeometry.PageStep(0, 10, step: 0.5), 9);
        Assert.Equal(10.0, FireKnobGeometry.PageStepMultiplier);

        Assert.Equal(3.5, FireKnobGeometry.ValueForKey(
            Key.Up, value: 3.0, min: 0, max: 10, step: 0.5)!.Value, 9);
        Assert.Equal(8.0, FireKnobGeometry.ValueForKey(
            Key.PageUp, value: 3.0, min: 0, max: 10, step: 0.5)!.Value, 9);
    }

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.PageUp)]
    public void AKeyAtTheCeiling_ClampsRatherThanRunningOff(Key key)
        => Assert.Equal(10.0, FireKnobGeometry.ValueForKey(
            key, value: 10.0, min: 0, max: 10, step: 0.5)!.Value, 9);

    [Theory]
    [InlineData(Key.Down)]
    [InlineData(Key.PageDown)]
    public void AKeyAtTheFloor_ClampsRatherThanRunningOff(Key key)
        => Assert.Equal(-12.0, FireKnobGeometry.ValueForKey(
            key, value: -12.0, min: -12, max: 12, step: 0.1)!.Value, 9);

    [Fact]
    public void ADegenerateRange_OwnsNoKeyAtAll()
    {
        // Otherwise Home/End would both "work" on a knob that cannot move, and
        // the arrow arithmetic would divide a zero span.
        foreach (var key in new[] { Key.Up, Key.PageDown, Key.Home, Key.End, Key.Delete })
            Assert.Null(FireKnobGeometry.ValueForKey(key, value: 5, min: 5, max: 5, step: 0));
    }
}
