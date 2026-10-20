using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     The persistent per-string edit mask — EOF's
///     <c>eof_pro_guitar_fret_bitmask</c> / rscharter's
///     <c>getEditedStrings()</c>, promoted out of the chord inspector's
///     checkboxes into editor state (editing-rework audit 03 §6.4).
///     <para>
///         The load-bearing rule is that an EMPTY mask means EVERY string: both
///         reference tools do it, the inspector already did it, and it is what
///         makes the mask a strict no-op until the user sets one.
///     </para>
/// </summary>
public sealed class StringEditMaskTests
{
    [Fact]
    public void AnEmptyMaskIncludesEveryString()
    {
        var mask = StringEditMask.Empty;

        Assert.True(mask.IsEmpty);
        for (var i = 0; i < StringEditMask.MaxStrings; i++)
            Assert.True(mask.Includes(i));
    }

    [Fact]
    public void AnEmptyMaskMarksNoStringExplicit()
    {
        // Includes() and IsExplicit() must disagree while empty, or the lane
        // headers would render "all nine lit" and the user could not see that
        // no mask is set.
        var mask = StringEditMask.Empty;

        for (var i = 0; i < StringEditMask.MaxStrings; i++)
            Assert.False(mask.IsExplicit(i));
    }

    [Fact]
    public void TogglingOneStringExcludesEveryOther()
    {
        var mask = StringEditMask.Empty.Toggle(2);

        Assert.False(mask.IsEmpty);
        Assert.True(mask.Includes(2));
        Assert.False(mask.Includes(0));
        Assert.False(mask.Includes(5));
        Assert.Equal(1, mask.Count);
    }

    [Fact]
    public void TogglingTheLastStringBackOffReturnsToAllStrings()
    {
        var mask = StringEditMask.Empty.Toggle(2).Toggle(4);
        Assert.Equal(2, mask.Count);

        mask = mask.Toggle(2).Toggle(4);

        Assert.True(mask.IsEmpty);
        Assert.True(mask.Includes(0));
    }

    [Fact]
    public void OutOfRangeStringsAreIgnoredRatherThanShiftingTheBits()
    {
        // Toggling a bogus index must not corrupt the payload…
        var mask = StringEditMask.Empty.Toggle(-1).Toggle(StringEditMask.MaxStrings);
        Assert.True(mask.IsEmpty);
        Assert.False(mask.IsExplicit(StringEditMask.MaxStrings));

        // …and a real mask never claims one. (An EMPTY mask deliberately says
        // yes to everything, bogus indices included — that is what keeps it a
        // strict no-op for a sound whose StringIndex is out of range.)
        var real = StringEditMask.FromStrings(0, StringEditMask.MaxStrings + 3);
        Assert.Equal(1, real.Count);
        Assert.False(real.Includes(-1));
        Assert.False(real.Includes(StringEditMask.MaxStrings));
    }

    [Fact]
    public void OnlyReplacesTheWholeMask()
    {
        var mask = StringEditMask.FromStrings(0, 1, 2).Only(4);

        Assert.Equal(1, mask.Count);
        Assert.True(mask.Includes(4));
        Assert.False(mask.Includes(0));
    }

    [Fact]
    public void ToStringIsTheStatusLineForm()
    {
        Assert.Equal("all", StringEditMask.Empty.ToString());
        Assert.Equal("1-3-5", StringEditMask.FromStrings(0, 2, 4).ToString());
    }

    [Fact]
    public void ValueEqualityHolds()
    {
        Assert.Equal(StringEditMask.FromStrings(1, 3), StringEditMask.FromStrings(3, 1));
        Assert.NotEqual(StringEditMask.FromStrings(1, 3), StringEditMask.FromStrings(1, 4));
    }

    // ── narrowing to the arrangement (2026-09-04) ───────────────────────────
    //
    // MaxStrings is a property of the KEYBOARD; the lanes are the ACTIVE
    // arrangement's. The digit families already refuse a string the arrangement
    // has not got, but nothing narrowed a mask that was ALREADY set when the
    // charter switched arrangements — so a Ctrl+6 on a 6-string lead stayed lit
    // over a 4-string bass, with no lane header to draw it on, and every
    // per-string verb became a silent no-op against a mask nothing on screen
    // could show.

    [Fact]
    public void NarrowingDropsEveryBitTheArrangementHasNoLaneFor()
    {
        var mask = StringEditMask.FromStrings(1, 5);

        var narrowed = mask.NarrowTo(4);

        Assert.True(narrowed.IsExplicit(1));
        Assert.False(narrowed.IsExplicit(5));
        Assert.Equal(1, narrowed.Count);
    }

    [Fact]
    public void NarrowingAwayTheLastBitLandsBackOnAllStrings()
    {
        // The same landing the last Ctrl+n toggle has: an empty mask is "all".
        var mask = StringEditMask.FromStrings(5);

        var narrowed = mask.NarrowTo(4);

        Assert.True(narrowed.IsEmpty);
        for (var i = 0; i < 4; i++) Assert.True(narrowed.Includes(i));
    }

    [Fact]
    public void NarrowingToAWiderArrangementKeepsTheWholeMask()
    {
        var mask = StringEditMask.FromStrings(0, 3);

        Assert.Equal(mask, mask.NarrowTo(6));
        Assert.Equal(mask, mask.NarrowTo(StringEditMask.MaxStrings));
    }
}
