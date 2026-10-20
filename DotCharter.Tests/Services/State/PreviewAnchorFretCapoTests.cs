using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>ProjectState.PreviewAnchorFret</c> is the fret the next PLACED FHP
///     anchor opens on, and it beats the suggester by design:
///     <c>FretAnchorInteractionTool.AddAnchorAt</c> says "an explicit preview
///     fret is the user having already said which fret they want, so it always
///     wins" and only falls back to the capo-aware
///     <see cref="AnchorFretSuggester" /> when the preview is <c>0</c>.
///     <para>
///         So the preview needs the SAME floor, and it did not have one. Every
///         writer clamped at a bare <c>1</c>, so on a capo'd chart wheeling the
///         FHP ghost down and clicking placed an anchor on or below the capo —
///         inspector rule <c>L02</c> (<c>CapoAnchorRangeRule</c>), which
///         <c>ArrangementAutoFixService</c> then raises silently on the next
///         pass. The keyboard nudge one line away in <c>AppKeybindRegistrar</c>
///         has always read the capo at press time; this was the pointer half.
///     </para>
/// </summary>
public sealed class PreviewAnchorFretCapoTests
{
    [Fact]
    public void ArmingAnAnchorFretOnOrBelowTheCapoRaisesItToTheFloor()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(5), PreviewAnchorFret = 3
        };
        Assert.Equal(6, state.PreviewAnchorFret);

        state.PreviewAnchorFret = 5;
        Assert.Equal(6, state.PreviewAnchorFret);
    }

    [Fact]
    public void ZeroStaysTheNothingArmedSentinel()
    {
        // Every reader tests `PreviewAnchorFret > 0` before preferring it over
        // the suggester — raising 0 to the floor would make the preview
        // permanently armed and the suggester dead.
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(5), PreviewAnchorFret = 9
        };

        state.PreviewAnchorFret = 0;

        Assert.Equal(0, state.PreviewAnchorFret);
    }

    [Fact]
    public void WithNoCapoTheFloorIsTheSameOneAsBefore()
    {
        var state = new ProjectState
        {
            ActiveArrangement = Capoed(0), PreviewAnchorFret = 1
        };
        Assert.Equal(1, state.PreviewAnchorFret);

        state.PreviewAnchorFret = 30;
        Assert.Equal(24, state.PreviewAnchorFret);
    }

    [Fact]
    public void TheFloorNeverInvertsTheClampEvenAtAFullNeckCapo()
    {
        // capo + 1 unclamped is 25 at capo 24, and Math.Clamp(v, 25, 24) THROWS
        // — the reason AnchorFretSuggester.MinFret clamps at all.
        Assert.Equal(24, AnchorFretSuggester.MinFret(24));
        Assert.Equal(1, AnchorFretSuggester.MinFret(0));
        Assert.Equal(1, AnchorFretSuggester.MinFret(-3));

        var state = new ProjectState
        {
            ActiveArrangement = Capoed(24), PreviewAnchorFret = 1
        };

        Assert.Equal(24, state.PreviewAnchorFret);
    }

    private static Arrangement Capoed(int capo) => new()
    {
        Name = "Lead",
        ArrangementType = ArrangementType.Lead,
        Capo = capo,
        Tuning = ["E", "A", "D", "G", "B", "e"]
    };
}
