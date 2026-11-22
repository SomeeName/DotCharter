namespace DotCharter.Tests.Core.Guitar;

/// <summary>
///     One label per bend GESTURE, as Guitar Pro 8 shows it, shared by the 2D timeline and the 3D
///     highway (<see cref="BendGestures" />). A plain bend through a middle point is one "Full" with an up
///     arrow at its peak, a release is one label with a down arrow where it lands, a prebend says so,
///     and a real multi-step curve (a hold between two rises) still labels each rise.
/// </summary>
public sealed class BendGestureLabelTests
{
    private static string[] Labels(float[] steps, bool firstAtOnset = true, float? carriedIn = null)
    {
        var gestures = new BendGesture[steps.Length];
        BendGestures.Classify(steps, firstAtOnset, carriedIn, gestures);
        return [.. gestures.Select(BendGestures.Text)];
    }

    [Fact]
    public void APlainBendThroughItsMiddlePoint_IsOneFull_AtThePeak() =>
        Assert.Equal(["", "", "↗Full"], Labels([0, 1, 2]));

    [Fact]
    public void ABendWhoseFirstPointIsAfterTheOnset_RisesFromTheString() =>
        Assert.Equal(["", "↗Full"], Labels([1, 2], firstAtOnset: false));

    [Fact]
    public void AReleaseTiedInFromABend_IsOneDownLabel_WhereItLands() =>
        Assert.Equal(["", "", "↘Full"], Labels([2, 1, 0], carriedIn: 2));

    [Fact]
    public void APrebendHeld_IsAPrebend() =>
        Assert.Equal(["PB Full"], Labels([2]));

    [Fact]
    public void APrebendReleased_IsAPrebend_ThenARelease() =>
        Assert.Equal(["PB Full", "", "↘Full"], Labels([2, 2, 0]));

    [Fact]
    public void AStruckNoteStartingHigh_WithoutATie_IsAPrebend_NotARelease() =>
        Assert.Equal(["PB Full", "", "↘Full"], Labels([2, 1, 0]));

    [Fact]
    public void AMultiStepCurve_LabelsEachRise() =>
        Assert.Equal(["", "↗1/2", "", "↗Full"], Labels([0, 1, 1, 2]));

    [Fact]
    public void ABendReleasedAfterAHold_IsOneUpAndOneDown() =>
        Assert.Equal(["", "↗Full", "", "↘Full"], Labels([0, 2, 2, 0]));

    [Fact]
    public void ABendTurningStraightIntoARelease_LabelsThePeakAndTheLanding() =>
        Assert.Equal(["", "↗Full", "↘1/2"], Labels([0, 2, 1]));

    [Fact]
    public void APartialReleaseNamesHowFarItFalls_AndAPrebendBendItsTarget() =>
        Assert.Equal(["PB Full", "↗1 1/2", "↘1/2"], Labels([2, 3, 2]));

    [Fact]
    public void AFlatCurveAtZero_HasNoLabel() =>
        Assert.Equal(["", ""], Labels([0, 0]));

    [Fact]
    public void ATiedNoteHoldingTheCarriedBend_HasNoLabel() =>
        Assert.Equal(["", ""], Labels([2, 2], carriedIn: 2));

    [Theory]
    [InlineData(1f, "1/2")]
    [InlineData(2f, "Full")]
    [InlineData(3f, "1 1/2")]
    [InlineData(4f, "2")]
    [InlineData(0.5f, "0.3")]
    [InlineData(5f, "2.5")]
    public void AmountsReadAsGuitarProWritesThem(float steps, string text) =>
        Assert.Equal(text, BendGestures.AmountText(steps));
}
