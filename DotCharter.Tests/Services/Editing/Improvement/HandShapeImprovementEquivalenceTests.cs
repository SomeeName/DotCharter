
namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     <c>ChartImprovementService.FixChordSlideHandShapes</c> and
///     <c>ImproveHandShapeLengths</c> answered three questions PER HAND SHAPE by
///     walking a whole collection each time — the shape at a chord's position,
///     the last chord inside a bar, the first anchor/sound after it — i.e. O(N×M)
///     on the UI thread behind Tools ▸ Improve Chart. They are indexed now.
///     <para>
///         An indexed rewrite of a heuristic is only allowed to be faster, never
///         different, and the heuristic is not obviously order-independent: it
///         MUTATES each bar's end as it walks and the next iteration reads the
///         mutated value. So the guard is an equivalence test — the OLD bodies,
///         copied verbatim below, run against a deep copy of the same generated
///         level, and every hand-shape end plus the reported change count must
///         agree. <b>Keep the copies dumb</b>; the moment they are "tidied" they
///         stop being a reference.
///     </para>
/// </summary>
public sealed class HandShapeImprovementEquivalenceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(404)]
    [InlineData(20260903)]
    public void TheIndexedPass_MatchesTheOldScans_OnAGeneratedLevel(int seed)
    {
        var beats = Beats(64);
        var mine = GenerateLevel(seed);
        var reference = Clone(mine);

        var mineSlide = InvokeFixChordSlideHandShapes(mine);
        var mineLengths = InvokeImproveHandShapeLengths(mine, beats);

        var refSlide = OldFixChordSlideHandShapes(reference);
        var refLengths = OldImproveHandShapeLengths(reference, beats);

        Assert.Equal(refSlide, mineSlide);
        Assert.Equal(refLengths, mineLengths);

        Assert.Equal(reference.HandShapes.Count, mine.HandShapes.Count);
        for (var i = 0; i < mine.HandShapes.Count; i++)
        {
            Assert.Equal(reference.HandShapes[i].Position, mine.HandShapes[i].Position);
            Assert.Equal(reference.HandShapes[i].EndPosition, mine.HandShapes[i].EndPosition);
        }

        // Not vacuous by construction: the generated levels must actually make
        // the pass do work, or "identical" would mean "both did nothing".
        Assert.True(refSlide + refLengths > 0, "the generated level changed nothing");
    }

    // ---- the shipped pass, reached through ImproveProject's private helpers ----

    private static int InvokeFixChordSlideHandShapes(ChartLevel level) =>
        (int)typeof(ChartImprovementService)
            .GetMethod("FixChordSlideHandShapes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [level])!;

    private static int InvokeImproveHandShapeLengths(ChartLevel level, List<BeatMarker> beats) =>
        (int)typeof(ChartImprovementService)
            .GetMethod("ImproveHandShapeLengths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [level, beats])!;

    // ---- the OLD bodies, verbatim ------------------------------------------

    private static int OldFixChordSlideHandShapes(ChartLevel level)
    {
        var changes = 0;
        foreach (var chord in level.Chords)
        {
            if (!chord.ChordNotes.Any(n => n.LinkNext && n.SlideTo.HasValue)) continue;

            var handShape = level.HandShapes.FirstOrDefault(h => h.Position == chord.Position);
            if (handShape == null) continue;

            var chordEnd = chord.Position + chord.GetMaxSustain();
            if (handShape.EndPosition <= chordEnd) continue;

            handShape.EndPosition = chordEnd;
            changes++;
        }

        return changes;
    }

    private static int OldImproveHandShapeLengths(ChartLevel level, IList<BeatMarker> beats)
    {
        if (beats.Count < 2 || level.HandShapes.Count == 0) return 0;

        var changes = 0;
        var orderedHandShapes = level.HandShapes.OrderBy(h => h.Position).ToList();
        for (var i = 0; i < orderedHandShapes.Count; i++)
        {
            var handShape = orderedHandShapes[i];
            var originalEnd = handShape.EndPosition;
            var lastChord = level.Chords
                .Where(c => c.Position >= handShape.Position && c.Position <= handShape.EndPosition)
                .OrderByDescending(c => c.Position)
                .FirstOrDefault();

            if (lastChord != null && handShape.EndPosition - lastChord.Position <=
                OldGetBeatFraction(lastChord.Position, beats, 4))
            {
                var extendedEnd = handShape.EndPosition + OldGetBeatFraction(lastChord.Position, beats, 4);
                var nextLimit = OldGetNextHandShapeLimit(orderedHandShapes, i, level);
                handShape.EndPosition = nextLimit.HasValue
                    ? FractionalPosition.Min(extendedEnd, nextLimit.Value)
                    : extendedEnd;
            }

            if (i + 1 < orderedHandShapes.Count)
            {
                var nextHandShape = orderedHandShapes[i + 1];
                var minGap = OldGetBeatFraction(handShape.EndPosition, beats, 8);
                var latestEnd = nextHandShape.Position - minGap;
                if (latestEnd > handShape.Position && handShape.EndPosition > latestEnd)
                    handShape.EndPosition = latestEnd;
            }

            if (handShape.EndPosition < handShape.Position)
                handShape.EndPosition = handShape.Position;

            if (handShape.EndPosition != originalEnd)
                changes++;
        }

        return changes;
    }

    private static FractionalPosition? OldGetNextHandShapeLimit(
        IReadOnlyList<HandShape> handShapes, int currentIndex, ChartLevel level)
    {
        var candidates = new List<FractionalPosition>();
        if (currentIndex + 1 < handShapes.Count)
            candidates.Add(handShapes[currentIndex + 1].Position);

        var current = handShapes[currentIndex];
        candidates.AddRange(level.Anchors
            .Where(a => a.Position > current.EndPosition)
            .Select(a => a.Position));

        var nextContent = level.Notes
            .Select(n => n.Position)
            .Concat(level.Chords.Select(c => c.Position))
            .Where(p => p > current.EndPosition)
            .DefaultIfEmpty()
            .Min();

        if (nextContent != default)
            candidates.Add(nextContent);

        return candidates.Count == 0 ? null : candidates.Min();
    }

    private static FractionalPosition OldGetBeatFraction(FractionalPosition position, IList<BeatMarker> beats,
        int denominator)
    {
        var beatIndex = Math.Clamp(position.BeatId, 0, beats.Count - 1);
        var beatsInMeasure = beats[beatIndex].BeatsInMeasure > 0 ? beats[beatIndex].BeatsInMeasure : 4;
        return new FractionalPosition(new Fraction(beatsInMeasure, denominator));
    }

    // ---- generation --------------------------------------------------------

    private static List<BeatMarker> Beats(int count)
    {
        const double ms = 60000.0 / 120;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker
        {
            Time = i * ms, Bpm = 120, IsMeasure = i % 4 == 0,
            // Vary the meter so GetBeatFraction is not a constant.
            BeatsInMeasure = i % 8 == 0 ? 3 : 4
        })];
    }

    private static ChartLevel GenerateLevel(int seed)
    {
        var rng = new Random(seed);
        var level = new ChartLevel();

        for (var i = 0; i < 40; i++)
        {
            var beat = rng.Next(0, 55);
            var chord = new Chord { Position = Pos(beat, rng) };
            var slide = rng.Next(0, 3) == 0;
            for (var s = 0; s < 2; s++)
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = s,
                    Fret = 3 + s,
                    Sustain = new FractionalPosition(rng.Next(0, 3)),
                    LinkNext = slide,
                    SlideTo = slide ? 7 : null
                });
            }

            level.Chords.Add(chord);
        }

        for (var i = 0; i < 25; i++)
            level.Notes.Add(new GuitarNote { Position = Pos(rng.Next(0, 55), rng), StringIndex = 0, Fret = 5 });

        for (var i = 0; i < 10; i++)
            level.Anchors.Add(new FretAnchor { Position = new FractionalPosition(rng.Next(0, 55)), Fret = 5 });

        // Bars: some starting exactly on a chord (so the slide fix can bite),
        // some nested, some zero-width, some overlapping the next one.
        foreach (var chord in level.Chords.Take(18))
        {
            level.HandShapes.Add(new HandShape
            {
                Position = chord.Position,
                EndPosition = chord.Position + new FractionalPosition(rng.Next(0, 6)),
                TemplateId = 0
            });
        }

        for (var i = 0; i < 6; i++)
        {
            var start = rng.Next(0, 55);
            level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(start + rng.Next(0, 9)),
                TemplateId = 0
            });
        }

        return level;
    }

    private static FractionalPosition Pos(int beat, Random rng) =>
        rng.Next(0, 2) == 0 ? new FractionalPosition(beat) : new FractionalPosition(beat, new Fraction(1, 2));

    private static ChartLevel Clone(ChartLevel level)
    {
        var copy = new ChartLevel();
        foreach (var c in level.Chords)
        {
            var chord = new Chord { Position = c.Position };
            foreach (var cn in c.ChordNotes)
            {
                chord.ChordNotes.Add(new ChordNote
                {
                    StringIndex = cn.StringIndex, Fret = cn.Fret, Sustain = cn.Sustain,
                    LinkNext = cn.LinkNext, SlideTo = cn.SlideTo
                });
            }

            copy.Chords.Add(chord);
        }

        foreach (var n in level.Notes)
            copy.Notes.Add(new GuitarNote { Position = n.Position, StringIndex = n.StringIndex, Fret = n.Fret });
        foreach (var a in level.Anchors)
            copy.Anchors.Add(new FretAnchor { Position = a.Position, Fret = a.Fret });
        foreach (var h in level.HandShapes)
        {
            copy.HandShapes.Add(new HandShape
            {
                Position = h.Position, EndPosition = h.EndPosition, TemplateId = h.TemplateId
            });
        }

        return copy;
    }
}
