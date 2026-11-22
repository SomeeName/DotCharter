using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 bug (a): bend points hold absolute chart positions, so a curve copied onto another sound
///     must follow THAT sound's onset and sustain: each point keeps its fraction of the sustain, and
///     nothing lands outside the target.
/// </summary>
public sealed class BendCurveRemapTests
{
    private static FractionalPosition Beat(int beat, int num = 0, int den = 1) => new(beat, new Fraction(num, den));

    private static double InBeats(FractionalPosition p) => p.BeatId + p.Fraction.DoubleValue;

    private static readonly IReadOnlyList<BendValue> Curve =
        [new BendValue(Beat(0), 0), new BendValue(Beat(0, 1, 4), 1), new BendValue(Beat(1), 2)];

    [Fact]
    public void ALaterLongerTarget_KeepsEachPointsFractionOfTheSustain()
    {
        var remapped = BendCurveRemap.Remap(Curve, Beat(0), Beat(1), Beat(4), Beat(2));

        Assert.Equal([Beat(4), Beat(4, 1, 2), Beat(6)], remapped.Select(v => v.Position));
        Assert.Equal([0.0, 1.0, 2.0], remapped.Select(v => v.Step));
    }

    [Fact]
    public void TheSourceItself_IsUnchanged_AndGetsCopies()
    {
        var remapped = BendCurveRemap.Remap(Curve, Beat(0), Beat(1), Beat(0), Beat(1));

        Assert.Equal(Curve.Select(v => v.Position), remapped.Select(v => v.Position));
        Assert.All(remapped.Zip(Curve), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public void PointsOutsideTheSourceSpan_AreClampedIntoTheTargetSpan()
    {
        IReadOnlyList<BendValue> wide = [new BendValue(Beat(-1), 0), new BendValue(Beat(3), 1)];

        var remapped = BendCurveRemap.Remap(wide, Beat(0), Beat(1), Beat(8), Beat(1, 1, 2));

        Assert.Equal([Beat(8), Beat(9, 1, 2)], remapped.Select(v => v.Position));
    }

    [Fact]
    public void AZeroLengthSource_MovesByTheOnset_ClampedToTheTargetSustain()
    {
        IReadOnlyList<BendValue> curve = [new BendValue(Beat(2), 0), new BendValue(Beat(2, 1, 4), 1), new BendValue(Beat(5), 2)];

        var remapped = BendCurveRemap.Remap(curve, Beat(2), Beat(0), Beat(10), Beat(1));

        Assert.Equal([Beat(10), Beat(10, 1, 4), Beat(11)], remapped.Select(v => v.Position));
    }

    /// <summary>A zero-sustain target has room for one point: the curve's peak, at its onset.</summary>
    [Fact]
    public void AZeroLengthTarget_GetsOnePoint_ThePeakStep_OnItsOnset()
    {
        IReadOnlyList<BendValue> curve =
            [new BendValue(Beat(0), 0), new BendValue(Beat(0, 1, 2), 2), new BendValue(Beat(1), 0)];

        var remapped = BendCurveRemap.Remap(curve, Beat(0), Beat(1), Beat(7), Beat(0));

        var point = Assert.Single(remapped);
        Assert.Equal(Beat(7), point.Position);
        Assert.Equal(2.0, point.Step);
    }

    /// <summary>The curve the charter draws is written to its own note exactly as drawn.</summary>
    [Fact]
    public void AZeroLengthSource_KeepsEveryDrawnPoint()
    {
        IReadOnlyList<BendValue> curve =
            [new BendValue(Beat(7), 0), new BendValue(Beat(7), 2), new BendValue(Beat(7), 0)];

        var remapped = BendCurveRemap.Remap(curve, Beat(7), Beat(0), Beat(7), Beat(0));

        Assert.Equal(curve.Select(v => (v.Position, v.Step)), remapped.Select(v => (v.Position, v.Step)));
        Assert.All(remapped.Zip(curve), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public void AZeroLengthTarget_TakesTheFirstOfTiedPeaks_ByAbsoluteStep()
    {
        IReadOnlyList<BendValue> curve =
            [new BendValue(Beat(0), 0.5), new BendValue(Beat(0, 1, 4), -1.5), new BendValue(Beat(1), 1.5)];

        var remapped = BendCurveRemap.Remap(curve, Beat(0), Beat(1), Beat(3), Beat(0));

        Assert.Equal(-1.5, Assert.Single(remapped).Step);
    }

    [Fact]
    public void AZeroLengthTarget_NeverTripsTheOverlappingBendRule()
    {
        IReadOnlyList<BendValue> curve =
            [new BendValue(Beat(0), 0), new BendValue(Beat(0, 1, 2), 2), new BendValue(Beat(1), 0)];
        var target = new GuitarNote { Position = Beat(2), Sustain = FractionalPosition.Zero, Fret = 5 };
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels[0].Notes.Add(target);

        BendCurveRemap.NoteWrite(curve, Beat(0), Beat(1))(target);
        List<BeatMarker> beats = [.. Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, beats);

        Assert.DoesNotContain(issues, i => i.Type == ArrangementIssueType.OverlappingBendValues);
    }

    /// <summary>Spans whose exact ratio does not fit an int fraction still land inside the target.</summary>
    [Fact]
    public void SpansWithHugeDenominators_DoNotThrow_AndStayInside()
    {
        var sourceSustain = new FractionalPosition(new Fraction(1_000_003, 999_983));
        var targetSustain = new FractionalPosition(new Fraction(999_979, 1_000_033));
        IReadOnlyList<BendValue> curve = [new BendValue(new FractionalPosition(new Fraction(500_001, 999_983)), 1)];

        var remapped = BendCurveRemap.Remap(curve, Beat(0), sourceSustain, Beat(3), targetSustain);

        var p = Assert.Single(remapped).Position;
        Assert.True(p >= Beat(3) && p <= Beat(3) + targetSustain, $"{p} is outside the target");
        Assert.Equal(3.5, p.BeatId + p.Fraction.DoubleValue, 2);
    }

    /// <summary>
    ///     The point's ratio of the source sustain overflows already (offset / sourceLength), and a
    ///     target denominator times 960 overflows too, so neither the exact path nor a Fraction-built
    ///     fallback can stand; the remap still lands inside the target, near the exact answer.
    /// </summary>
    [Fact]
    public void RatioAndFallbackThatOverflow_DoNotThrow_AndStayInside()
    {
        var sourceSustain = new FractionalPosition(new Fraction(1_000_003, 999_983));
        var targetSustain = new FractionalPosition(new Fraction(2_999_999, 3_000_017));
        var point = new FractionalPosition(new Fraction(501_001, 1_000_033));
        IReadOnlyList<BendValue> curve = [new BendValue(point, 1)];

        var remapped = BendCurveRemap.Remap(curve, Beat(0), sourceSustain, Beat(3), targetSustain);

        var p = Assert.Single(remapped).Position;
        Assert.True(p >= Beat(3) && p <= Beat(3) + targetSustain, $"{p} is outside the target");
        var expected = 3 + InBeats(point) / InBeats(sourceSustain) * InBeats(targetSustain);
        Assert.Equal(expected, InBeats(p), 2);
    }

    [Fact]
    public void ANoteWrite_FollowsEachTargetNote()
    {
        var target = new GuitarNote { Position = Beat(6), Sustain = Beat(2) };

        BendCurveRemap.NoteWrite(Curve, Beat(0), Beat(1))(target);

        Assert.Equal([Beat(6), Beat(6, 1, 2), Beat(8)], target.BendValues.Select(v => v.Position));
    }
}
