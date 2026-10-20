using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A let-ring run that RE-FRETS a string it is already holding is two hand
///     positions, not one. <see cref="GpHandShapeBuilder" /> aggregates a run's
///     shape as "first fret wins per string", so an unsplit run declares its
///     FIRST fret on that string and every later note there contradicts the very
///     bar covering it.
///     <para>
///     Provenance: owner report 2026-08-21 against a real GP8 file. One let-ring
///     bar ran across five measures (748 0/1 → 770 0/1) and the inspector raised
///     E05 twice inside it — "this sound plays string 1 at fret 4, but the hand
///     shape 'F#min11' covering it declares fret 2 there".
///     </para>
/// </summary>
public sealed class GpLetRingRefretSplitTests
{
    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret,
        Sustain = new FractionalPosition(1)
    };

    private static (ChartLevel Level, Arrangement Arr) Run(params GuitarNote[] notes)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        foreach (var n in notes)
        {
            level.Notes.Add(n);
            letRing.Add(n.Position);
        }

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);
        return (level, arr);
    }

    /// <summary>The inspector E05 rule, asserted directly: no sound may play a
    /// fret its covering hand shape declares differently on that string.</summary>
    private static void AssertNoShapeContradictsItsSounds(ChartLevel level, Arrangement arr)
    {
        foreach (var shape in level.HandShapes)
        {
            var template = arr.ChordTemplates.Single(t => t.Id == shape.TemplateId);
            foreach (var note in level.Notes)
            {
                if (note.Position.CompareTo(shape.Position) < 0 ||
                    note.Position.CompareTo(shape.EndPosition) >= 0)
                {
                    continue;
                }

                if (!template.Frets.TryGetValue(note.StringIndex, out var declared)) continue;
                Assert.True(declared == note.Fret,
                    $"sound at {note.Position} plays string {note.StringIndex} fret {note.Fret}, " +
                    $"but the shape at {shape.Position} declares fret {declared} there");
            }
        }
    }

    [Fact]
    public void RefrettingAHeldString_SplitsTheRunIntoTwoShapes()
    {
        // s0 is held at fret 2, then re-fretted to 4 at beat 3: the hand moved.
        var (level, arr) = Run(
            Note(0, 0, 2), Note(1, 1, 2), Note(2, 2, 4),
            Note(3, 0, 4), Note(4, 1, 4));

        Assert.Equal(2, level.HandShapes.Count);
        var ordered = level.HandShapes.OrderBy(h => h.Position).ToList();
        Assert.Equal(new FractionalPosition(0), ordered[0].Position);
        Assert.Equal(new FractionalPosition(3), ordered[1].Position);
        AssertNoShapeContradictsItsSounds(level, arr);
    }

    [Fact]
    public void UnsplitRun_WouldContradictItself_SoTheGuardIsNotVacuous()
    {
        // The same five notes, and the invariant the split exists to keep: if the
        // run were still one shape this assertion is what would fail.
        var (level, arr) = Run(
            Note(0, 0, 2), Note(1, 1, 2), Note(2, 2, 4),
            Note(3, 0, 4), Note(4, 1, 4));

        AssertNoShapeContradictsItsSounds(level, arr);
        Assert.All(level.HandShapes, h =>
            Assert.True(h.EndPosition.CompareTo(new FractionalPosition(5)) <= 0));
    }

    [Fact]
    public void ReplayingTheSameFret_KeepsOneShape()
    {
        // Re-striking a string at the SAME fret is the ordinary let-ring arpeggio
        // pattern — the hand has not moved and must not be cut.
        var (level, arr) = Run(
            Note(0, 0, 2), Note(1, 1, 4), Note(2, 0, 2), Note(3, 1, 4));

        Assert.Single(level.HandShapes);
        AssertNoShapeContradictsItsSounds(level, arr);
    }

    [Fact]
    public void AStringTheRunHasNotTouched_DoesNotSplitIt()
    {
        var (level, _) = Run(Note(0, 0, 2), Note(1, 1, 3), Note(2, 2, 4), Note(3, 3, 5));
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void ANoteThatWouldStretchTheGripPastAHand_SplitsTheRun()
    {
        // A string the run has never touched slips past the re-fret rule, so it
        // used to be folded into the aggregate however far away it was. Real hit
        // (independent re-import check, 2026-08-22): a run holding {8,10,8,9,8}
        // absorbed a lone fret-2 note nine beats later and synthesized a
        // 2-8-10-8-9-8 template — an 8-fret stretch no hand holds, and INVISIBLE
        // to the E05 fret check, because a shape built FROM the notes it covers
        // can never contradict them.
        // The trailing note gives the SECOND fragment its two-beat floor; without
        // it the split is real but the fragment is dropped and only one shape
        // survives, which would read as "no split".
        var (level, arr) = Run(Note(0, 1, 8), Note(1, 2, 10), Note(2, 3, 9),
            Note(3, 0, 2), Note(4, 4, 3));

        Assert.Equal(2, level.HandShapes.Count);
        Assert.All(arr.ChordTemplates, t =>
        {
            var fretted = t.Frets.Values.Where(f => f > 0).ToList();
            if (fretted.Count < 2) return;
            Assert.True(fretted.Max() - fretted.Min() <= 5,
                $"template spans {fretted.Max() - fretted.Min()} frets: no hand holds that");
        });
    }

    [Fact]
    public void AGripInsideAHandsReach_IsNotSplit()
    {
        // The other side of the same rule: 5 frets IS reachable, so a run that
        // stays inside it must stay one shape.
        var (level, _) = Run(Note(0, 1, 5), Note(1, 2, 7), Note(2, 3, 10), Note(3, 4, 6));
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void AnOpenStringNeverWidensTheGrip()
    {
        // Fret 0 asks nothing of the fret hand, so it cannot stretch it.
        var (level, _) = Run(Note(0, 1, 9), Note(1, 2, 10), Note(2, 3, 0), Note(3, 4, 9));
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void ARunStartingOnAnExistingShape_SupersedesItInsteadOfCollapsingIt()
    {
        // Two shapes sharing a Position make ClampHandShapes clamp the first to
        // ZERO WIDTH — a bar covering nothing that still holds a template id and
        // would export as an RS handShape with startTime == endTime. The run
        // describes the same grip over a longer span, so it replaces it.
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        foreach (var n in new[] { Note(0, 1, 8), Note(1, 2, 10), Note(2, 3, 9) })
        {
            level.Notes.Add(n);
            letRing.Add(n.Position);
        }

        // The chord builder's own bar for the strike the run starts on.
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(1),
            TemplateId = 0
        });

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);
        GpPostProcessor.ClampHandShapes(level);

        Assert.DoesNotContain(level.HandShapes, h => h.EndPosition.CompareTo(h.Position) <= 0);
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void AShapeThatOutlivesTheRun_IsLeftAlone()
    {
        // Only a shape the run fully CONTAINS is superseded; one that reaches
        // past it is a different hand position and belongs to the clamp.
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        foreach (var n in new[] { Note(0, 1, 8), Note(1, 2, 10) })
        {
            level.Notes.Add(n);
            letRing.Add(n.Position);
        }

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(99),
            TemplateId = 0
        });

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        Assert.Equal(2, level.HandShapes.Count);
    }

    [Fact]
    public void ASplitFragmentShorterThanTwoBeats_IsNotGivenAShape()
    {
        // The run-length floor is unchanged: a lone note is not a hand shape.
        var (level, _) = Run(Note(0, 0, 2), Note(1, 1, 2), Note(2, 0, 4));
        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(0), shape.Position);
        Assert.True(shape.EndPosition.CompareTo(new FractionalPosition(2)) <= 0,
            "the shape must not extend over the re-fretted note it excluded");
    }
}
