using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI;

/// <summary>
///     The inspector's two multi-selection summaries, folded in ONE pass, still
///     answer exactly what the four passes they replaced answered.
///
///     <para>
///         <c>SelectionShapeSummary</c> and <c>BendSummary</c> are both BOUND, so
///         each is re-evaluated on every <c>NotifySelectionKindChanged</c> — which
///         fires from all three inspector syncs, several times per click. Between
///         them they made four passes over the whole selection per evaluation and
///         allocated a <c>string.Join</c> PER SELECTED CHORD for the distinct-shape
///         count.
///     </para>
///     <para>
///         Every case below compares the fold against a VERBATIM copy of the
///         expressions it replaced, on the same inputs — the shape-count identity
///         most of all, because the pack is a different representation of it and
///         a collision would silently UNDER-count.
///     </para>
/// </summary>
public sealed class SelectionShapeFoldTests
{
    // ---- verbatim copies of what was there ----------------------------------

    private static string SpelledShapeKey(Chord c)
        => string.Join(",", c.ChordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}"));

    private static int OldDistinctShapes(IReadOnlyList<Chord> chords)
        => chords.Select(SpelledShapeKey).Distinct().Count();

    private static int OldWithBends(IReadOnlyList<GuitarNote> notes, IReadOnlyList<Chord> chords)
        => notes.Count(n => n.BendValues.Count > 0)
           + chords.Count(c => c.ChordNotes.Any(cn => cn.BendValues.Count > 0));

    private static (bool Has, int MinFret, int MaxFret, int MinString, int MaxString) OldRanges(
        IReadOnlyList<GuitarNote> notes, IReadOnlyList<Chord> chords)
    {
        var frets = new List<int>();
        var strings = new HashSet<int>();
        foreach (var n in notes)
        {
            frets.Add(n.Fret);
            strings.Add(n.StringIndex);
        }

        foreach (var cn in chords.SelectMany(c => c.ChordNotes))
        {
            frets.Add(cn.Fret);
            strings.Add(cn.StringIndex);
        }

        if (frets.Count == 0) return (false, 0, 0, -1, -1);
        return (true, frets.Min(), frets.Max(), strings.Min(), strings.Max());
    }

    private static void AssertAgrees(List<GuitarNote> notes, List<Chord> chords)
    {
        var fold = SelectionShapeFold.Over(notes, chords);
        var (has, minFret, maxFret, minString, maxString) = OldRanges(notes, chords);

        Assert.Equal(has, fold.HasSounds);
        Assert.Equal(minFret, fold.MinFret);
        Assert.Equal(maxFret, fold.MaxFret);
        Assert.Equal(minString, fold.MinString);
        Assert.Equal(maxString, fold.MaxString);
        Assert.Equal(OldDistinctShapes(chords), fold.DistinctShapes);
        Assert.Equal(OldWithBends(notes, chords), fold.WithBends);
        Assert.Equal(notes.Count + chords.Count, fold.Total);
    }

    // ---- cases ---------------------------------------------------------------

    [Fact]
    public void OnAnEmptySelection_ItAgrees()
        => AssertAgrees([], []);

    [Fact]
    public void OnNotesAlone_ItAgrees()
    {
        AssertAgrees([Note(0, 3), Note(5, 12), Note(2, 0, bend: true)], []);
        AssertAgrees([Note(4, 7)], []);
    }

    [Fact]
    public void OnChordsAlone_ItAgrees()
    {
        var a = Chord((0, 5), (1, 7), (2, 7));
        var b = Chord((0, 5), (1, 7), (2, 7)); // same grip, different instance
        var c = Chord((3, 9), (4, 9));
        AssertAgrees([], [a, b, c]);

        // Not vacuous: the two equal grips really do collapse.
        Assert.Equal(2, SelectionShapeFold.Over([], [a, b, c]).DistinctShapes);
    }

    /// <summary>
    ///     The pack must distinguish grips a naive scheme confuses: the SAME
    ///     frets on different strings, an OPEN string against an absent one, and
    ///     a subset of another grip.
    /// </summary>
    [Fact]
    public void GripsThatOnlyDifferByStringOrByAbsence_StayDistinct()
    {
        var chords = new List<Chord>
        {
            Chord((0, 5), (1, 5)),
            Chord((1, 5), (2, 5)),
            Chord((0, 0), (1, 5)),
            Chord((1, 5)),
            Chord((0, 5), (1, 5), (2, 5))
        };

        AssertAgrees([], chords);
        Assert.Equal(5, SelectionShapeFold.Over([], chords).DistinctShapes);
    }

    /// <summary>The pack is order-independent, exactly as the sorted spelling
    /// was — the same grip authored high-string-first is one shape, not two.</summary>
    [Fact]
    public void TheSameGripInADifferentNoteOrder_IsOneShape()
    {
        var ascending = Chord((0, 5), (1, 7), (2, 7));
        var descending = Chord((2, 7), (1, 7), (0, 5));

        AssertAgrees([], [ascending, descending]);
        Assert.Equal(1, SelectionShapeFold.Over([], [ascending, descending]).DistinctShapes);
    }

    /// <summary>A chord with no notes has the empty key and is one shape — the
    /// spelling produced <c>""</c> and so must the fold.</summary>
    [Fact]
    public void EmptyChords_AreOneShapeBetweenThem()
    {
        var chords = new List<Chord> { Chord(), Chord(), Chord((0, 3)) };

        AssertAgrees([], chords);
        Assert.Equal(2, SelectionShapeFold.Over([], chords).DistinctShapes);
    }

    /// <summary>
    ///     The FALLBACK path: grips the pack refuses (a string past its slots, a
    ///     fret past 126, two notes on one string) still get the original
    ///     spelling, so their identity is unchanged rather than approximated.
    /// </summary>
    [Fact]
    public void GripsThePackRefuses_StillAgreeWithTheSpelling()
    {
        var chords = new List<Chord>
        {
            Chord((11, 4)),            // string past the packed slots
            Chord((11, 4)),            // …and its duplicate
            Chord((0, 400)),           // fret past the packed range
            Chord((0, 3), (0, 5)),     // two notes on one string
            Chord((0, 3), (0, 5)),     // …and its duplicate
            Chord((0, 3))              // packs fine, and is a DIFFERENT shape
        };

        AssertAgrees([], chords);
        Assert.Equal(4, SelectionShapeFold.Over([], chords).DistinctShapes);
    }

    /// <summary>
    ///     The repeated-string refusal, at the one input where dropping it
    ///     really loses a shape: <c>0:1</c> plus <c>0:2</c> ORs into the same
    ///     slot value a lone <c>0:2</c> packs to (2|3 == 3), so without the guard
    ///     two different grips collapse into one. Every other repeated-string
    ///     case in this class happens to survive the OR, which is exactly why
    ///     this one is spelled out.
    /// </summary>
    [Fact]
    public void ARepeatedStringThatWouldOrIntoAnotherGrip_StaysDistinct()
    {
        var chords = new List<Chord> { Chord((0, 1), (0, 2)), Chord((0, 2)) };

        AssertAgrees([], chords);
        Assert.Equal(2, SelectionShapeFold.Over([], chords).DistinctShapes);
    }

    /// <summary>Mixed selections, and bends on both sides.</summary>
    [Fact]
    public void OnAMixedSelection_ItAgrees()
    {
        var notes = new List<GuitarNote> { Note(0, 3), Note(1, 5, bend: true) };
        var chords = new List<Chord>
        {
            Chord((2, 7), (3, 7)),
            ChordWithBend((4, 9), (5, 9))
        };

        AssertAgrees(notes, chords);
        var fold = SelectionShapeFold.Over(notes, chords);
        Assert.Equal(2, fold.WithBends);
        Assert.Equal(4, fold.Total);
    }

    /// <summary>A pseudo-random sweep, so agreement is not just on the shapes
    /// somebody thought to write down.</summary>
    [Fact]
    public void OverAPseudoRandomSweep_ItAlwaysAgrees()
    {
        var rng = new Random(20260904);
        for (var trial = 0; trial < 400; trial++)
        {
            var notes = new List<GuitarNote>();
            for (var i = rng.Next(0, 5); i > 0; i--)
                notes.Add(Note(rng.Next(0, 7), rng.Next(0, 25), rng.Next(0, 3) == 0));

            var chords = new List<Chord>();
            for (var i = rng.Next(0, 6); i > 0; i--)
            {
                var pairs = new List<(int, int)>();
                var stringsUsed = new HashSet<int>();
                for (var k = rng.Next(0, 4); k > 0; k--)
                {
                    // Deliberately allowed to repeat a string sometimes: that is
                    // the case the pack refuses and the spelling handles.
                    var s = rng.Next(0, 7);
                    if (rng.Next(0, 8) != 0 && !stringsUsed.Add(s)) continue;
                    pairs.Add((s, rng.Next(0, 25)));
                }

                chords.Add(rng.Next(0, 6) == 0
                    ? ChordWithBend(pairs.ToArray())
                    : Chord(pairs.ToArray()));
            }

            AssertAgrees(notes, chords);
        }
    }

    // ---- fixture -------------------------------------------------------------

    private static GuitarNote Note(int stringIndex, int fret, bool bend = false)
    {
        var note = new GuitarNote { StringIndex = stringIndex, Fret = fret };
        if (bend) note.BendValues.Add(new BendValue { Position = new FractionalPosition(0), Step = 1 });
        return note;
    }

    private static Chord Chord(params (int StringIndex, int Fret)[] notes)
    {
        var chord = new Chord();
        foreach (var (s, f) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    private static Chord ChordWithBend(params (int StringIndex, int Fret)[] notes)
    {
        var chord = Chord(notes);
        if (chord.ChordNotes.Count > 0)
            chord.ChordNotes[^1].BendValues.Add(new BendValue { Position = new FractionalPosition(0), Step = 1 });
        return chord;
    }
}
