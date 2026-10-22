using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     <see cref="RepeatChordLatch" /> is how both render loops reach the repeat
///     decision once per frame without running the whole-level rule per frame.
///     It must recompute on every signal that the answer may have moved — a new
///     list, a count change, an edit (the content version) — and, for an
///     UNVERSIONED caller only, at the staleness bound; otherwise serve the cached
///     set, so playback never pays for the whole-level rule. Clock and version
///     are injected.
/// </summary>
public sealed class RepeatChordLatchTests
{
    private static List<BeatMarker> Beats() =>
        Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 }).ToList();

    /// <summary>Two identical C chords under one hand shape: the second repeats.</summary>
    private static (List<ChordTemplate> Templates, List<GuitarNote> Notes, List<Chord> Chords, List<HandShape> Shapes)
        Level()
    {
        var templates = new List<ChordTemplate> { new() { Id = 1, Name = "C", Frets = { [1] = 3, [2] = 2 } } };
        var chords = Enumerable.Range(0, 2).Select(i =>
        {
            var chord = new Chord { Position = new FractionalPosition(i), TemplateId = 1 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 3 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
            return chord;
        }).ToList();
        var shapes = new List<HandShape>
        { new() { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4), TemplateId = 1 } };
        return (templates, [], chords, shapes);
    }

    [Fact]
    public void TheSecondStrum_IsARepeat()
    {
        var (templates, notes, chords, shapes) = Level();

        var set = new RepeatChordLatch().Get(templates, notes, chords, shapes, Beats(), 0, 0);

        Assert.DoesNotContain(chords[0], set);
        Assert.Contains(chords[1], set);
    }

    [Fact]
    public void WithinTheBound_AndNothingChanged_TheCachedSetIsServed()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();

        var first = latch.Get(templates, notes, chords, shapes, beats, 7, 1_000);
        var second = latch.Get(templates, notes, chords, shapes, beats, 7, 1_000 + RepeatChordLatch.StaleAfterMs - 1);

        Assert.Same(first, second);
        Assert.Equal(1, latch.ComputeCount);
    }

    [Fact]
    public void AnInPlaceEdit_ShowsOnTheNextGet_WhenTheContentVersionMoves()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();
        Assert.Contains(chords[1], latch.Get(templates, notes, chords, shapes, beats, 7, 1_000));

        // A fret edit: same lists, same counts — only the version says so.
        chords[1].ChordNotes[0].Fret = 5;

        Assert.Contains(chords[1], latch.Get(templates, notes, chords, shapes, beats, 7, 1_001));
        Assert.DoesNotContain(chords[1], latch.Get(templates, notes, chords, shapes, beats, 8, 1_002));
    }

    [Fact]
    public void Versioned_NothingRecomputesOnTheClock()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();

        for (var t = 0L; t < 60_000; t += 16)
            latch.Get(templates, notes, chords, shapes, beats, 7, t);

        Assert.Equal(1, latch.ComputeCount);
    }

    [Fact]
    public void Unversioned_AnUnannouncedEdit_ShowsOnceTheBoundExpires()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();
        latch.Get(templates, notes, chords, shapes, beats, -1, 1_000);

        chords[1].ChordNotes[0].Fret = 5;

        Assert.Contains(chords[1], latch.Get(templates, notes, chords, shapes, beats, -1, 1_001));
        Assert.DoesNotContain(chords[1],
            latch.Get(templates, notes, chords, shapes, beats, -1, 1_000 + RepeatChordLatch.StaleAfterMs));
    }

    [Fact]
    public void ACountChange_OrANewList_Recomputes()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();
        latch.Get(templates, notes, chords, shapes, beats, 7, 1_000);

        notes.Add(new GuitarNote { Position = new FractionalPosition(0, new Fraction(1, 2)), StringIndex = 0, Fret = 1 });
        latch.Get(templates, notes, chords, shapes, beats, 7, 1_001);
        Assert.Equal(2, latch.ComputeCount);

        var swapped = new List<HandShape>(shapes);
        latch.Get(templates, notes, chords, swapped, beats, 7, 1_002);
        Assert.Equal(3, latch.ComputeCount);
    }

    /// <summary>The 2D timeline and the 3D render thread read one level on one edit: ONE run, the very
    /// same set — and a second level (a stacked row) is its own run.</summary>
    [Fact]
    public void TheTwoLoops_ShareOneRunPerEdit()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();

        for (long edit = 1; edit <= 3; edit++)
        {
            var timeline = latch.Get(templates, notes, chords, shapes, beats, edit, 0);
            var preview3D = latch.Get(templates, notes, chords, shapes, beats, edit, 0);

            Assert.Same(timeline, preview3D);
            Assert.Equal((int)edit, latch.ComputeCount);
        }

        var (otherTemplates, otherNotes, otherChords, otherShapes) = Level();
        latch.Get(otherTemplates, otherNotes, otherChords, otherShapes, beats, 3, 0);
        Assert.Equal(4, latch.ComputeCount);
    }

    [Fact]
    public void ReadersOnSeveralThreads_WaitForOneRun()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();
        var sets = new IReadOnlySet<Chord>[16];

        Parallel.For(0, sets.Length, new ParallelOptions { MaxDegreeOfParallelism = sets.Length },
            i => sets[i] = latch.Get(templates, notes, chords, shapes, beats, 1, 0));

        Assert.Equal(1, latch.ComputeCount);
        Assert.All(sets, set => Assert.Same(sets[0], set));
    }

    [Fact]
    public void AWarmRead_AllocatesNothing()
    {
        var (templates, notes, chords, shapes) = Level();
        var beats = Beats();
        var latch = new RepeatChordLatch();
        latch.Get(templates, notes, chords, shapes, beats, 1, 0);

        AllocationProbe.AssertAllocationFree("a warm repeat-chord read",
            () => latch.Get(templates, notes, chords, shapes, beats, 1, 0));
    }
}
