using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     The string-edit mask is addressed by DIGIT KEYS (nine of them) and DRAWN
///     on the ACTIVE arrangement's lanes, so switching arrangements has to
///     narrow it.
///     <para>
///         <c>StringDigitTargeting</c> already refuses a <c>Ctrl+n</c> for a
///         string the arrangement has not got — but only at the KEYPRESS. A mask
///         already set on a 6-string lead survived the switch to a 4-string bass
///         with bit 5 still lit: <c>LaneLayer</c> draws four lane headers, so
///         nothing on screen showed it, and every per-string verb (fret ±/set,
///         mute, HOPO, harmonic, picking, vibrato, tremolo, link-next) became a
///         silent no-op because no sound on the bass plays a masked string.
///         That is precisely the invisible modal state the mask contract forbids
///         — rscharter's <c>typingNumber</c> mistake, the one thing the editing
///         audit says outright not to copy.
///     </para>
/// </summary>
public sealed class StringEditMaskArrangementScopeTests
{
    [Fact]
    public void SwitchingToAShorterArrangementDropsTheLanesItHasNotGot()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Guitar(), StringEditMask = StringEditMask.FromStrings(1, 5)
        };

        state.ActiveArrangement = Bass();

        Assert.False(state.StringEditMask.IsExplicit(5));
        Assert.True(state.StringEditMask.IsExplicit(1));
    }

    [Fact]
    public void AMaskThatOnlyHeldMissingLanesLandsBackOnAllStrings()
    {
        // "Every per-string verb is a no-op" and "every per-string verb edits
        // every string" are both legible states; "the mask says string 6 and the
        // bass has four" is not.
        var state = new ProjectState
        {
            ActiveArrangement = Guitar(), StringEditMask = StringEditMask.FromStrings(4, 5)
        };

        state.ActiveArrangement = Bass();

        Assert.True(state.StringEditMask.IsEmpty);
    }

    [Fact]
    public void SwitchingToAWiderArrangementLeavesTheMaskAlone()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Bass(), StringEditMask = StringEditMask.FromStrings(0, 3)
        };

        state.ActiveArrangement = Guitar();

        Assert.Equal(StringEditMask.FromStrings(0, 3), state.StringEditMask);
    }

    [Fact]
    public void AnArrangementWithNoTuningNarrowsNothing()
    {
        // Dropping a bit needs POSITIVE knowledge that the lane is gone. An
        // absent tuning is "unknown", and silently narrowing to the default six
        // would throw away a mask the charter can still see on a wider chart.
        var state = new ProjectState
        {
            ActiveArrangement = Guitar(), StringEditMask = StringEditMask.FromStrings(6)
        };

        state.ActiveArrangement = new Arrangement { Name = "Untuned", Tuning = [] };

        Assert.True(state.StringEditMask.IsExplicit(6));

        state.ActiveArrangement = null;

        Assert.True(state.StringEditMask.IsExplicit(6));
    }

    private static Arrangement Guitar() => new()
    {
        Name = "Lead",
        ArrangementType = ArrangementType.Lead,
        Tuning = ["E", "A", "D", "G", "B", "e"]
    };

    private static Arrangement Bass() => new()
    {
        Name = "Bass",
        ArrangementType = ArrangementType.Bass,
        Tuning = ["E", "A", "D", "G"]
    };
}
