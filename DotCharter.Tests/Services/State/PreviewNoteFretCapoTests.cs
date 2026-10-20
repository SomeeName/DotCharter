namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>ProjectState.PreviewNoteFret</c> is the fret the next CREATED sound
///     gets — the wheel's preview ghost, the pen's typed digit, the pointer's
///     right-click placement and the inspector's queue readout all read the one
///     value, and both <c>ApplyNotePlacementCommand</c> call sites take it
///     verbatim.
///     <para>
///         Every writer clamped it to a plain <c>0..24</c>, so on a capo'd chart
///         the editor could arm — and then CREATE — a sound at a fret the neck
///         has not got: inspector rule <c>L01</c> (<c>NoteFretBelowCapo</c>), the
///         malformed shape the app's own validator flags and
///         <c>ArrangementAutoFixService.FixCapoFrets</c> raises silently on the
///         next auto-fix. Snapping in the setter rather than in the placement
///         command is deliberate: the ghost and the note it becomes must be the
///         same fret.
///     </para>
/// </summary>
public sealed class PreviewNoteFretCapoTests
{
    [Fact]
    public void ArmingAFretBehindTheCapoLandsOnTheCapo()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(5), PreviewNoteFret = 3
        };

        Assert.Equal(5, state.PreviewNoteFret);
    }

    [Fact]
    public void TheOpenStringIsStillArmable()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(5), PreviewNoteFret = 0
        };

        Assert.Equal(0, state.PreviewNoteFret);
    }

    [Fact]
    public void WithNoCapoTheValueIsWhateverWasWritten()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(0), PreviewNoteFret = 3
        };

        Assert.Equal(3, state.PreviewNoteFret);
    }

    [Fact]
    public void SwitchingToAnArrangementDoesNotRetroactivelyBreakTheArmedFret()
    {
        // The snap is per WRITE, on the arrangement live at that moment. Arming
        // fret 3 on an un-capo'd chart and then opening a capo-5 one leaves 3
        // armed; the next write (the wheel, a typed digit) lands it on the neck.
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(0), PreviewNoteFret = 3
        };

        state.ActiveArrangement = Capoed(5);
        state.PreviewNoteFret = 3;

        Assert.Equal(5, state.PreviewNoteFret);
    }

    private static Arrangement Capoed(int capo) => new()
    {
        Name = "Lead",
        ArrangementType = ArrangementType.Lead,
        Capo = capo,
        Tuning = ["E", "A", "D", "G", "B", "e"]
    };
}
