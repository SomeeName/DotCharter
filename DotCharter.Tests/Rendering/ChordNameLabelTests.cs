using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Rocksmith-style chord names label a shape ONCE. A chord and the hand shape
///     drawn over it share a chord template, so if both passes labelled it the
///     same text would be drawn twice at the same depth — which on a 3D highway
///     reads as a bolder, slightly fringed label rather than as an obvious bug.
///     <para>
///         Chords win; a hand shape only labels itself when no chord starts inside
///         it — a let-ring or arpeggio run, which has no chord of its own but is
///         still a named shape and would otherwise go unlabelled entirely.
///     </para>
/// </summary>
public sealed class ChordNameLabelTests
{
    private static RenderChord[] Chords(params double[] times) =>
        [.. times.Select(t => new RenderChord { Time = t, TemplateId = 0 })];

    [Fact]
    public void AHandShapeOverAChordDefersToTheChordPass()
    {
        var chords = Chords(1000);

        Assert.True(ChordNameRenderer.CoversAChord(chords, chords.Length, 1000, 2000));
    }

    /// <summary>A let-ring / arpeggio run: a named shape with no chord of its own.</summary>
    [Fact]
    public void AHandShapeOverNoChordLabelsItself()
    {
        var chords = Chords(5000);

        Assert.False(ChordNameRenderer.CoversAChord(chords, chords.Length, 1000, 2000));
    }

    /// <summary>
    ///     End is EXCLUSIVE — the same span rule `HandShape.Covers`, the pattern
    ///     matcher and the anchor generator use. A chord landing exactly on the end
    ///     belongs to the NEXT shape, and treating it as inside would silence this
    ///     shape's label and leave the run unnamed.
    /// </summary>
    [Fact]
    public void TheHandShapeEndIsExclusive()
    {
        var chords = Chords(2000);

        Assert.False(ChordNameRenderer.CoversAChord(chords, chords.Length, 1000, 2000));
        Assert.True(ChordNameRenderer.CoversAChord(chords, chords.Length, 2000, 3000));
    }

    /// <summary>
    ///     The snapshot arrays are GC-reused and longer than their live window, so
    ///     a stale chord past `ChordCount` must not suppress a real label.
    /// </summary>
    [Fact]
    public void EntriesPastTheLiveCountAreIgnored()
    {
        var chords = Chords(1500, 1600);

        Assert.False(ChordNameRenderer.CoversAChord(chords, 0, 1000, 2000));
        Assert.True(ChordNameRenderer.CoversAChord(chords, 1, 1000, 2000));
    }

    [Fact]
    public void AnEmptyChordWindowNeverSuppresses() => Assert.False(ChordNameRenderer.CoversAChord([], 0, 0, 10_000));

    /// <summary>
    ///     The label must sit where the CAMERA IS POINTED — not merely somewhere
    ///     inside the anchor span.
    ///     <para>
    ///         "Inside the span" — what the previous version of this test asserted
    ///         — was never the invariant: <c>Fretboard3DEngine.CalculateCameraMvp</c>
    ///         centres on <c>middleFhpPosition * 0.9 + weightedPosition * 0.1</c>,
    ///         and that second term pulls the view toward the neck's centre,
    ///         shifting the visible window off the span, so the span's own edges
    ///         can sit past a clip plane.
    ///     </para>
    ///     <para>
    ///         <b>Worth being honest about what this test can and cannot do.</b>
    ///         It passed through two releases in which the owner could not see a
    ///         single chord name — because the label was not rendering AT ALL (it
    ///         was staged from the transparent pass, before the text batcher had
    ///         an atlas or the scene matrix; see <c>ChordNameComponent</c>). No
    ///         geometry assertion can catch that. <c>HeadlessChordNameTests</c>
    ///         is the one that can, and it is where "the label is visible" is now
    ///         actually pinned; this file pins only WHERE it goes once it draws.
    ///     </para>
    ///     <para>
    ///         So this pins the label against the engine's own
    ///         <c>middleFhpPosition</c> expression, INCLUDING the camera's
    ///         minimum-span widening (<c>if (maxFret - minFret &lt; 3) maxFret =
    ///         minFret + 3</c>) — a width-1 anchor otherwise computes a centre the
    ///         camera never framed. If the camera formula changes, this fails, and
    ///         that is the point: the two must not drift.
    ///     </para>
    ///     <para>
    ///         Kept as a unit test even though a golden frame now exists: this one
    ///         runs in the DEFAULT gate, while the golden frame needs a GPU and
    ///         <c>DOTCHARTER_GPU_TESTS=1</c>.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(1, 1)]   // narrower than the camera's minimum span
    [InlineData(5, 4)]
    [InlineData(12, 5)]
    [InlineData(19, 6)]
    [InlineData(22, 4)]
    [InlineData(23, 6)]  // widened span would run off the 24-fret board
    public void TheLabelSitsLeftOfWhereTheCameraIsPointed(int anchorFret, int anchorWidth)
    {
        // Mirror of CalculateCameraMvp's span resolution.
        var minFret = Math.Max(1, anchorFret);
        var maxFret = minFret + Math.Max(1, anchorWidth) - 1;
        if (maxFret - minFret < 3) maxFret = minFret + 3;
        maxFret = Math.Min(24, maxFret);

        var right = FretboardConstants.GetLinearFretPosition(maxFret);
        var left = FretboardConstants.GetLinearFretPosition(minFret - 1);
        var centre = (right + left) / 2f;
        var x = ChordNameRenderer.LabelX(anchorFret, anchorWidth);

        // LEFT of centre — beside the shape on the nut side, not above it.
        Assert.True(x < centre, $"label x {x} is not left of the framed centre {centre}");

        // ...but still inside the window the camera framed, or it is off screen.
        // This is the pair that matters: v0.41.1 shipped a label 0.9 units left
        // of the span and it was never once visible.
        Assert.True(x >= left || minFret == 1,
            $"label x {x} is left of the framed span ({left}) — outside the camera's window");
    }

    /// <summary>A malformed anchor must not push the label off the nut end.</summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-3, 4)]
    [InlineData(-3, 0)]
    public void ANonPositiveAnchorStillLandsOnTheBoard(int anchorFret, int anchorWidth)
    {
        var x = ChordNameRenderer.LabelX(anchorFret, anchorWidth);
        Assert.True(x >= 0f, $"label x {x} is off the nut end");
        Assert.True(x <= FretboardConstants.GetLinearFretPosition(24), $"label x {x} is off the board");
    }

    // ---- one name per hand shape ---------------------------------------------

    private static RenderHandShape[] Shapes(params (double Start, double End)[] spans) =>
        [.. spans.Select(s => new RenderHandShape { Time = s.Start, EndTime = s.End })];

    /// <summary>
    ///     ONE name per hand shape. A shape is one hand position, so it gets one
    ///     label — at its first chord.
    ///     <para>
    ///         Owner report (0.42.4): a shape holding two strums of A7 drew "A7"
    ///         TWICE, at two depths. The renderer labelled every CHORD, and
    ///         re-strumming a held shape is an ordinary thing for a chart to
    ///         contain, so the duplicate was not an edge case at all.
    ///     </para>
    /// </summary>
    [Fact]
    public void OnlyTheFirstChordInAShapeCarriesTheName()
    {
        var chords = Chords(1000, 1500, 1800);
        var shapes = Shapes((1000, 2000));

        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 1));
        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 2));
    }

    /// <summary>Index order is not guaranteed to be time order — the snapshot's
    /// arrays are windowed and GC-reused — so the lead is chosen by TIME.</summary>
    [Fact]
    public void TheEarliestChordWinsRegardlessOfIndexOrder()
    {
        var chords = Chords(1800, 1000);
        var shapes = Shapes((1000, 2000));

        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 1));
    }

    /// <summary>A chord with no shape over it names itself — plenty of charts
    /// have bare chords, and they must not go unlabelled.</summary>
    [Fact]
    public void ABareChordStillNamesItself()
    {
        var chords = Chords(5000);
        var shapes = Shapes((1000, 2000));

        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
    }

    /// <summary>Each shape gets its own name — the rule is per shape, not one
    /// label for the whole chart.</summary>
    [Fact]
    public void EachShapeCarriesItsOwnName()
    {
        var chords = Chords(1000, 1500, 3000, 3500);
        var shapes = Shapes((1000, 2000), (3000, 4000));

        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 1));
        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 2));
        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 3));
    }

    /// <summary>
    ///     Shapes ABUT, so coverage is start-inclusive / end-EXCLUSIVE — the same
    ///     rule the matcher and the anchor generator use. A chord exactly on a
    ///     boundary belongs to the LATER shape, and must lead it.
    /// </summary>
    [Fact]
    public void AChordOnTheBoundaryLeadsTheLaterShape()
    {
        var chords = Chords(1000, 2000);
        var shapes = Shapes((1000, 2000), (2000, 3000));

        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 1));
    }

    /// <summary>Two chords at the SAME instant inside one shape: exactly one is
    /// the lead, or the shape would go unlabelled entirely.</summary>
    [Fact]
    public void ATieIsBrokenSoTheShapeIsStillLabelled()
    {
        var chords = Chords(1000, 1000);
        var shapes = Shapes((1000, 2000));

        Assert.True(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 0));
        Assert.False(ChordNameRenderer.IsShapeLeadChord(chords, chords.Length, shapes, shapes.Length, 1));
    }
}
