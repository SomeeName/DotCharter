using Avalonia.Input;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The chord box's hit-testing and typing helpers. Both are PURE (the whole
///     reason they were lifted off the control), so they run in the default gate
///     without an Avalonia visual tree.
/// </summary>
public class ChordBoxGeometryTests
{
    private static ChordBoxGeometry Sixer(int minFret = 1, int maxFret = 5) =>
        new(300, 140, 6, minFret, maxFret);

    /// <summary>
    ///     String 0 — the LOWEST — is drawn at the TOP, the way the highway draws
    ///     it (<c>ChartRenderContext.LaneTopFor</c>, lane 0 at the top).
    ///     <para>
    ///         This row asserted the opposite until 0.61 and passed for it: the
    ///         control drew the high E on top while the highway drew the low E
    ///         there, so the two were mirror images and this test was pinning the
    ///         mirror. Reported as "instead of red (low E) being the top the
    ///         purple (high E) is on the top". The flip cases live in
    ///         <c>UI/ChordBoxStringOrderTests</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void StringZeroIsDrawnAtTheTop()
    {
        var g = Sixer();
        Assert.True(g.StringY(0) < g.StringY(5));
    }

    [Fact]
    public void EveryStringLaneHitsItsOwnIndex()
    {
        var g = Sixer();
        for (var i = 0; i < 6; i++)
            Assert.Equal(i, g.HitString(g.StringY(i)));
    }

    [Fact]
    public void OutsideTheLaneAreaHitsNothing()
    {
        var g = Sixer();
        Assert.Equal(-1, g.HitString(0));
        Assert.Equal(-1, g.HitString(g.Height));
    }

    [Fact]
    public void TheMaskPipColumnIsLeftOfTheOpenStringColumn()
    {
        Assert.True(ChordBoxGeometry.IsOverMaskPip(1));
        Assert.True(ChordBoxGeometry.IsOverMaskPip(ChordBoxGeometry.PipZoneWidth - 0.1));
        Assert.False(ChordBoxGeometry.IsOverMaskPip(ChordBoxGeometry.PipZoneWidth));
        // The open-string column sits between the pip and the nut, so a click
        // meaning "open string" can never be mistaken for "toggle the mask".
        Assert.True(ChordBoxGeometry.PipZoneWidth < ChordBoxGeometry.FretboardStartX);
    }

    [Fact]
    public void LeftOfTheNutIsTheOpenString()
    {
        var g = Sixer();
        Assert.True(g.TryHit(ChordBoxGeometry.FretboardStartX - 1, g.StringY(2), capo: 0,
            out var s, out var fret));
        Assert.Equal(2, s);
        Assert.Equal(0, fret);
    }

    [Fact]
    public void WithACapoTheOpenStringIsTheCapoFret()
    {
        var g = Sixer(4, 8);
        Assert.True(g.TryHit(ChordBoxGeometry.FretboardStartX - 1, g.StringY(2), capo: 3,
            out _, out var fret));
        Assert.Equal(3, fret);
    }

    [Fact]
    public void AFrettedCellRoundTripsThroughItsCentre()
    {
        var g = Sixer(3, 9);
        for (var fret = 3; fret <= 9; fret++)
        {
            Assert.True(g.TryHit(g.FretCenterX(fret), g.StringY(1), capo: 0, out var s, out var hit));
            Assert.Equal(1, s);
            Assert.Equal(fret, hit);
        }
    }

    [Fact]
    public void PastTheLastFretHitsNothing()
    {
        var g = Sixer(1, 5);
        Assert.False(g.TryHit(g.Width, g.StringY(0), capo: 0, out _, out _));
    }

    // ---- the display window ------------------------------------------------

    [Fact]
    public void AnEmptyShapeShowsFiveFretsFromTheFirst()
    {
        Assert.Equal((1, 5), ChordBoxGeometry.DisplayFrets(null, capo: 0));
        Assert.Equal((1, 5), ChordBoxGeometry.DisplayFrets(Array.Empty<int>(), capo: 0));
    }

    [Fact]
    public void TheWindowNeverStartsBelowTheCapo()
    {
        // Frets at or under the capo are OPEN strings: they live on the rail
        // and must not drag the fret window down onto unplayable ground. Only
        // fret 9 is really fretted here, so the window frames IT.
        var (min, max) = ChordBoxGeometry.DisplayFrets(new[] { 0, 2, 9 }, capo: 5);
        Assert.Equal(8, min);
        Assert.Equal(12, max);

        // An all-open capo'd shape falls back to the capo floor, never fret 1.
        var (openMin, openMax) = ChordBoxGeometry.DisplayFrets(new[] { 0, 2 }, capo: 5);
        Assert.Equal(6, openMin);
        Assert.Equal(10, openMax);
    }

    [Fact]
    public void ACapoOfZeroIsAStrictNoOp()
    {
        Assert.Equal(ChordBoxGeometry.DisplayFrets(new[] { 7, 9 }, capo: 0),
            ChordBoxGeometry.DisplayFrets(new[] { 7, 9 }, capo: 0));
        var (min, max) = ChordBoxGeometry.DisplayFrets(new[] { 7, 9 }, capo: 0);
        Assert.Equal(6, min);
        Assert.Equal(10, max);
    }

    [Fact]
    public void AWideShapeWidensTheWindowInsteadOfClippingIt()
    {
        var (min, max) = ChordBoxGeometry.DisplayFrets(new[] { 3, 15 }, capo: 0);
        Assert.Equal(2, min);
        Assert.Equal(16, max);
    }

    [Fact]
    public void EveryOpenShapeStillShowsAtLeastFiveFrets()
    {
        var (min, max) = ChordBoxGeometry.DisplayFrets(new[] { 0, 0, 0 }, capo: 0);
        Assert.Equal(1, min);
        Assert.Equal(5, max);
    }

    // ---- the typing helpers ------------------------------------------------

    [Theory]
    [InlineData(Key.D0, 0)]
    [InlineData(Key.D9, 9)]
    [InlineData(Key.NumPad0, 0)]
    [InlineData(Key.NumPad7, 7)]
    [InlineData(Key.A, -1)]
    [InlineData(Key.F1, -1)]
    public void DigitKeysMapToTheirDigit(Key key, int expected) =>
        Assert.Equal(expected, ChordBoxControl.DigitFor(key));

    [Fact]
    public void TIsTheThumbAndTakesNoModifier()
    {
        Assert.Equal(0, ChordBoxControl.FingerFor(Key.T, KeyModifiers.None));
        Assert.Null(ChordBoxControl.FingerFor(Key.T, KeyModifiers.Shift));
    }

    [Fact]
    public void FingersOneToFourNeedShiftSoBareDigitsCanStayFrets()
    {
        // Bare 1..4 are FRETS on this control (0..24 in two digits). The finger
        // family therefore takes Shift, mirroring the app's global Shift+Alt
        // fingering row. Key.D1 + Shift is layout-independent — Shift+1 prints
        // "!" on a German keyboard but the Key is still D1.
        for (var d = 1; d <= 4; d++)
        {
            Assert.Equal(d, ChordBoxControl.FingerFor(Key.D0 + d, KeyModifiers.Shift));
            Assert.Null(ChordBoxControl.FingerFor(Key.D0 + d, KeyModifiers.None));
        }

        Assert.Null(ChordBoxControl.FingerFor(Key.D5, KeyModifiers.Shift));
    }

    [Fact]
    public void CtrlIsNeverAFingerBecauseItIsTheMaskModifier()
    {
        Assert.Null(ChordBoxControl.FingerFor(Key.D1, KeyModifiers.Control));
        Assert.Null(ChordBoxControl.FingerFor(Key.T, KeyModifiers.Control));
    }
}
