using System.Diagnostics;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Static perf audit 2026-08-29 P1 #10/#11. <see cref="LinkNextRule" /> runs
///     on the inspector's 150 ms debounce after every history step, over every
///     level of a DD chart, and asked two questions per linked sound by full
///     scan: the chord-note half scanned <c>level.Notes</c> from index 0 for
///     the next note on the string, and BOTH halves scanned every chord for one
///     starting where the sustain ends. Both now come from per-level indexes on
///     <see cref="ArrangementValidationContext" />.
///     <para>
///         The indexes must answer exactly what the scans did — including on a
///         level whose notes are NOT position-sorted, which an import can leave
///         behind — so the scans are kept here verbatim as references and the
///         two are compared query for query on a random chart. The timing is
///         logged, never asserted.
///     </para>
/// </summary>
public sealed class LinkNextIndexTests(ITestOutputHelper output)
{
    /// <summary>The old <c>LinkNextRule.FindNextNoteOnSameStringAfter</c>, verbatim.</summary>
    private static GuitarNote? ReferenceNextNoteAfter(IList<GuitarNote> notes, FractionalPosition after, int stringIndex)
    {
        foreach (var note in notes)
        {
            if (note.StringIndex == stringIndex && note.Position > after)
                return note;
        }

        return null;
    }

    /// <summary>The old static <c>FindChordLinkedTo</c>, verbatim.</summary>
    private static Chord? ReferenceChordLinkedTo(ChartLevel level, FractionalPosition endPosition, int stringIndex)
    {
        foreach (var c in level.Chords)
        {
            if (c.Position != endPosition) continue;
            if (c.ChordNotes.Count == 0) continue;
            foreach (var cn in c.ChordNotes)
                if (cn.StringIndex == stringIndex) return c;
        }
        return null;
    }

    private static ChartLevel RandomLevel(Random rng, int notes, int chords, bool sorted)
    {
        var level = new ChartLevel();
        for (var i = 0; i < notes; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(rng.Next(notes / 2), new Fraction(rng.Next(4), 4)),
                StringIndex = rng.Next(6),
                Fret = rng.Next(20),
                Sustain = new FractionalPosition(rng.Next(3)),
                LinkNext = rng.Next(5) == 0
            });
        }

        if (sorted)
        {
            var ordered = level.Notes.OrderBy(n => n.Position).ToList();
            level.Notes.Clear();
            foreach (var n in ordered) level.Notes.Add(n);
        }

        for (var i = 0; i < chords; i++)
        {
            var chord = new Chord { Position = new FractionalPosition(rng.Next(notes / 2), new Fraction(rng.Next(4), 4)) };
            var strings = rng.Next(4);
            for (var s = 0; s < strings; s++)
                chord.ChordNotes.Add(new ChordNote { StringIndex = rng.Next(6), Fret = rng.Next(12), LinkNext = rng.Next(4) == 0 });
            level.Chords.Add(chord);
        }

        return level;
    }

    private static List<BeatMarker> Beats(int count)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        return beats;
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 5)]
    public void TheIndexes_AnswerExactlyWhatTheScansDid(bool sorted, int seed)
    {
        var rng = new Random(seed);
        var arrangement = new Arrangement();
        var level = RandomLevel(rng, 1_500, 400, sorted);
        arrangement.Levels.Add(level);
        var context = ArrangementValidationContext.Build(arrangement, Beats(1_000), null);

        var queries = 0;
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                var expected = ReferenceNextNoteAfter(level.Notes, chord.Position, chordNote.StringIndex);
                var actual = context.NextNoteOnStringAfter(level, chord.Position, chordNote.StringIndex);
                Assert.Same(expected, actual);
                queries++;
            }
        }

        foreach (var note in level.Notes)
        {
            var expected = ReferenceChordLinkedTo(level, note.EndPosition, note.StringIndex);
            var actual = context.ChordLinkedTo(level, note.EndPosition, note.StringIndex);
            Assert.Same(expected, actual);
            // The static one-off form stays in step with the index too.
            Assert.Same(expected, ArrangementValidationContext.FindChordLinkedTo(level, note.EndPosition, note.StringIndex));
            queries++;
        }

        Assert.True(queries > 1_000, "the random chart produced too few queries to mean anything");
    }

    [Fact]
    public void TheRule_OverADdChart_IsLogged()
    {
        var rng = new Random(11);
        var arrangement = new Arrangement();
        for (var l = 0; l < 12; l++) arrangement.Levels.Add(RandomLevel(rng, 4_000, 800, sorted: true));
        var beats = Beats(3_000);

        var rule = new LinkNextRule();
        var issues = new List<ArrangementIssue>();
        // Warm once (JIT), then time one pass with a fresh context — which is
        // what every debounced validation gets.
        rule.Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        issues.Clear();
        var sw = Stopwatch.StartNew();
        rule.Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        sw.Stop();

        var linked = arrangement.Levels.Sum(l => l.Notes.Count(n => n.LinkNext) + l.Chords.Sum(c => c.ChordNotes.Count(cn => cn.LinkNext)));
        output.WriteLine($"LinkNextRule over 12 levels x 4,000 notes / 800 chords ({linked} linked sounds): {sw.Elapsed.TotalMilliseconds:0.0} ms, {issues.Count} issues.");
        Assert.NotEmpty(issues);
    }
}
