using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The grid-snap picker's labels (P1-9) are DERIVED from what the divisor
///     does, and these tests pin both halves: the derivation itself, and the
///     fact that the divisor really denominates a BEAT (so "1/8" is an eighth
///     of a beat, and a label written against a measure or a whole note would
///     be a lie).
/// </summary>
public sealed class GridSnapOptionTests
{
    /// <summary>
    ///     The label IS the divisor, spelled the way EOF spells it
    ///     (<c>EOF_SRC/menu/edit.c</c>). It read "1/16T" for divisor 24 until
    ///     0.61 — internally consistent and, per the owner, unusable: the
    ///     divisor is the number a charter thinks in, and re-deriving it into a
    ///     note name hid the whole triplet half of the ladder.
    /// </summary>
    [Theory]
    [InlineData(4, "1/4")]
    [InlineData(8, "1/8")]
    [InlineData(12, "1/12")]
    [InlineData(16, "1/16")]
    [InlineData(24, "1/24")]
    [InlineData(32, "1/32")]
    [InlineData(48, "1/48")]
    [InlineData(64, "1/64")]
    [InlineData(96, "1/96")]
    public void LabelFor_IsTheDivisor(int divisor, string expected) => Assert.Equal(expected, GridSnapOption.LabelFor(divisor));

    /// <summary>
    ///     The ladder is EOF's, entry for entry: five straight steps and four
    ///     triplet ones. Losing the triplet half is the reported bug.
    /// </summary>
    [Theory]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(12, true)]
    [InlineData(16, false)]
    [InlineData(24, true)]
    [InlineData(32, false)]
    [InlineData(48, true)]
    [InlineData(64, false)]
    [InlineData(96, true)]
    public void TheTripletStepsAreTheOnesDivisibleByThree(int divisor, bool isTriplet)
    {
        Assert.Equal(isTriplet, GridSnapOption.IsTripletDivisor(divisor));
        Assert.Equal(isTriplet, GridSnapOption.For(divisor).IsTriplet);
    }

    [Fact]
    public void TheLadderKeepsBothHalvesOfEofsSnapMenu()
    {
        var straight = TransportViewModel.GridSnapOptions.Where(o => !o.IsTriplet).Select(o => o.Divisor);
        var triplets = TransportViewModel.GridSnapOptions.Where(o => o.IsTriplet).Select(o => o.Divisor);
        Assert.Equal(new[] { 4, 8, 16, 32, 64 }, straight);
        Assert.Equal(new[] { 12, 24, 48, 96 }, triplets);
    }

    /// <summary>
    ///     The hint the T stands for: "1/NT" is three notes in the space of two
    ///     1/N steps, i.e. 2/(3N) of a beat — which must equal 1/divisor or the
    ///     hint lies about the step it names.
    /// </summary>
    [Fact]
    public void TheNoteNameOfATripletStep_MeansExactlyThatDivisor()
    {
        foreach (var option in TransportViewModel.GridSnapOptions.Where(o => o.IsTriplet))
        {
            var n = int.Parse(option.NoteName[2..^1]);
            Assert.Equal(1.0 / option.Divisor, 2.0 / (3.0 * n), precision: 12);
        }
    }

    [Fact]
    public void AStraightStepsNoteName_IsJustItself()
    {
        foreach (var option in TransportViewModel.GridSnapOptions.Where(o => !o.IsTriplet))
            Assert.Equal(option.Label, option.NoteName);
    }

    [Fact]
    public void TheLadder_IsTheHistoricalDivisorList_EachWithADistinctLabel()
    {
        Assert.Equal(new[] { 4, 8, 12, 16, 24, 32, 48, 64, 96 },
            TransportViewModel.GridSnapOptions.Select(o => o.Divisor));
        Assert.Equal(TransportViewModel.GridSnapOptions.Count,
            TransportViewModel.GridSnapOptions.Select(o => o.Label).Distinct().Count());
        // The ComboBox renders items via ToString, so it must BE the label.
        Assert.All(TransportViewModel.GridSnapOptions, o => Assert.Equal(o.Label, o.ToString()));
    }

    /// <summary>
    ///     The unit claim: the divisor denominates the interval between two BEAT
    ///     markers. With beats at 0 and 1000 ms and divisor 8, snapping lands on
    ///     125 ms steps — an eighth of a BEAT, exactly what the label "1/8" says.
    /// </summary>
    [Fact]
    public void TheDivisor_DenominatesTheBeat_NotTheMeasure()
    {
        var beats = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true },
            new() { Time = 1000 }
        };

        var snapped = new TempoService(new FakeAppLogger()).SnapToGrid(130, beats, 8);

        Assert.Equal(0, snapped.BeatId);
        Assert.Equal(1, snapped.Fraction.Numerator);
        Assert.Equal(8, snapped.Fraction.Denominator);
    }
}
