using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     The context rules #494 added to the FHP walk, one pattern per test, each
///     with the case that must NOT change: the free double-stop hold, the bounce
///     collapse, the bass pull-back and the guitar abandonment look-ahead.
/// </summary>
public sealed class FretAnchorContextTests
{
    private static readonly Func<ChordTemplate, ChordTemplate?> AllFree = t => t;

    private static GuitarNote Note(int beat, int fret, int stringIndex = 0) =>
        new() { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };

    private static Chord Chord(int beat, int templateId, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static ChordTemplate Template(int id, IDictionary<int, int> frets, IDictionary<int, int> fingers) =>
        new() { Id = id, Frets = new Dictionary<int, int>(frets), Fingers = new Dictionary<int, int>(fingers) };

    private static HandShape Shape(int start, int end, int templateId = -1) =>
        new() { Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end), TemplateId = templateId };

    private static string Describe(IEnumerable<FretAnchor> anchors) =>
        string.Join(" ", anchors.Select(a => $"{a.Position.BeatId}:{a.Fret}"));

    /// <summary>
    ///     Single notes with the index on fret 8, then the double stop G9+B9
    ///     (template 0, fingered 1-1 by the suggester), then fret 8 again.
    /// </summary>
    private static (ChartLevel Level, List<ChordTemplate> Templates) DoubleStopAfterLine(
        int doubleStopFret, int lowString = 3)
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 8, 3));
        level.Notes.Add(Note(1, 8, 4));
        level.Chords.Add(Chord(2, 0, (lowString, doubleStopFret), (lowString + 1, doubleStopFret)));
        level.HandShapes.Add(Shape(2, 4, 0));
        level.Notes.Add(Note(4, 8, 3));
        List<ChordTemplate> templates =
        [
            Template(0, new Dictionary<int, int> { [lowString] = doubleStopFret, [lowString + 1] = doubleStopFret },
                new Dictionary<int, int> { [lowString] = 1, [lowString + 1] = 1 })
        ];
        return (level, templates);
    }

    // ─── Free double stops ──────────────────────────────────────────────────

    [Fact]
    public void AFreeDoubleStopOneFretUp_IsPlayedFromTheHandPositionAlreadyInForce()
    {
        var (level, templates) = DoubleStopAfterLine(9);

        var held = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
            freeGrip: AllFree);

        Assert.Equal("0:8", Describe(held));
    }

    [Fact]
    public void AnAuthoredDoubleStop_StillPinsTheIndexAnchor()
    {
        // No predicate = every fingering is authored: the index-on-9 grip is a
        // stated hand position, so the walk moves there and back (the pre-#494
        // walk, and the hold's vacuity keeper).
        var (level, templates) = DoubleStopAfterLine(9);

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates);

        Assert.Equal("0:8 2:9 4:8", Describe(anchors));
    }

    [Fact]
    public void ADoubleStopBeyondTheSpareFingers_IsARealPositionShift()
    {
        // Index fret 12 is four frets above the hand: no grip reaches it from 8.
        var (level, templates) = DoubleStopAfterLine(12);

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
            freeGrip: AllFree);

        Assert.Contains(anchors, a => a.Position == new FractionalPosition(2) && a.Fret == 12);
    }

    [Fact]
    public void APowerChordGripOnTheLowStrings_IsNotHeld()
    {
        // The same grip on the E and A strings is a power-chord-like shape the
        // charters move the index onto (held only 18-44% of the time).
        var (level, templates) = DoubleStopAfterLine(9, lowString: 0);
        level.Notes.Clear();
        level.Notes.Add(Note(0, 8, 0));
        level.Notes.Add(Note(1, 8, 1));
        level.Notes.Add(Note(4, 8, 0));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
            freeGrip: AllFree);

        Assert.Contains(anchors, a => a.Position == new FractionalPosition(2) && a.Fret == 9);
    }

    [Theory]
    // A chord with the index ON fret 8, then the double stop, then a line that
    // stays above 8: the hand is moving up — no hold.
    [InlineData(new[] { 10, 11, 10, 12 }, 9)]
    // ...and the same, but the line comes back down to 8: the hand stays.
    [InlineData(new[] { 10, 8, 10, 8 }, 8)]
    public void ADoubleStopAfterAnIndexChord_FollowsWhereTheLineGoesNext(int[] after, int expectedAtDoubleStop)
    {
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, 1, (3, 8), (4, 8)));
        level.HandShapes.Add(Shape(0, 2, 1));
        level.Chords.Add(Chord(2, 0, (3, 9), (4, 9)));
        level.HandShapes.Add(Shape(2, 4, 0));
        for (var i = 0; i < after.Length; i++) level.Notes.Add(Note(4 + i, after[i], 3));
        List<ChordTemplate> templates =
        [
            Template(0, new Dictionary<int, int> { [3] = 9, [4] = 9 }, new Dictionary<int, int> { [3] = 1, [4] = 1 }),
            Template(1, new Dictionary<int, int> { [3] = 8, [4] = 8 }, new Dictionary<int, int> { [3] = 1, [4] = 1 })
        ];

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
            freeGrip: AllFree).ToList();

        var governing = anchors.Last(a => a.Position <= new FractionalPosition(2));
        Assert.Equal(expectedAtDoubleStop, governing.Fret);
    }

    [Fact]
    public void ABassDoubleStop_IsNeverHeld()
    {
        var (level, templates) = DoubleStopAfterLine(9, lowString: 2);
        level.Notes.Clear();
        level.Notes.Add(Note(0, 8, 2));
        level.Notes.Add(Note(1, 8, 3));
        level.Notes.Add(Note(4, 8, 2));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
            bassArrangement: true, freeGrip: AllFree);

        Assert.Contains(anchors, a => a.Position == new FractionalPosition(2) && a.Fret == 9);
    }

    // ─── Bounce collapse ────────────────────────────────────────────────────

    [Fact]
    public void ABounceInsideOneHandSpan_CollapsesToTheOneAnchor()
    {
        // 5 6 | shape locked on 7: 7 8 | (rest) 5 6 — the walk plants 5 -> 7 -> 5,
        // but everything under the 7 is already in reach of the hand at 5. (The
        // rest keeps the 5 off the shape's End, which an arpeggio root includes.)
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5));
        level.Notes.Add(Note(1, 6));
        level.Notes.Add(Note(2, 7, 1));
        level.Notes.Add(Note(3, 8, 1));
        level.HandShapes.Add(Shape(2, 4));
        level.Notes.Add(Note(5, 5));
        level.Notes.Add(Note(6, 6));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal("0:5", Describe(anchors));
    }

    [Fact]
    public void ABounceOutOfReach_KeepsItsAnchors()
    {
        // The same shape on 9-10: fret 10 is out of the hand at 5. A real shift.
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5));
        level.Notes.Add(Note(1, 6));
        level.Notes.Add(Note(2, 9, 1));
        level.Notes.Add(Note(3, 10, 1));
        level.HandShapes.Add(Shape(2, 4));
        level.Notes.Add(Note(5, 5));
        level.Notes.Add(Note(6, 6));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal("0:5 2:9 5:5", Describe(anchors));
    }

    [Fact]
    public void ASlideLanding_IsNeverCollapsed()
    {
        // 5, then a pitched slide 5 -> 7 that MOVES the hand, a phrase at 7-8 and
        // back to 5. Everything after the landing is in reach of 5, but the slide
        // put the hand there: the landing anchor stays.
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5));
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), Fret = 5, SlideTo = 7, Sustain = new FractionalPosition(1)
        });
        int[] phrase = [7, 8, 7, 8, 7];
        for (var i = 0; i < phrase.Length; i++) level.Notes.Add(Note(2 + i, phrase[i], 1));
        level.Notes.Add(Note(8, 5));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Contains(anchors, a => a.Position == new FractionalPosition(2) && a.Fret == 7);
        Assert.Contains(anchors, a => a.Position == new FractionalPosition(8) && a.Fret == 5);
    }

    // ─── Bass pull-back ─────────────────────────────────────────────────────

    [Fact]
    public void Bass_AForcedUpShift_IsSetWhereTheClimbStarts()
    {
        // 3 | 5 6 7: the hand at 3 reaches 5 and 6, and 7 forces a move. A
        // bassist sets the hand at 5 for the whole climb.
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 3));
        level.Notes.Add(Note(1, 5));
        level.Notes.Add(Note(2, 6));
        level.Notes.Add(Note(3, 7));

        var bass = new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: true);
        var guitar = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal("0:3 1:5", Describe(bass));
        // Guitar only: measured a loss there.
        Assert.Equal("0:3 3:7", Describe(guitar));
    }

    // ─── Guitar abandonment ─────────────────────────────────────────────────

    [Fact]
    public void Guitar_ReAnchorsUp_WhenAWholePhraseHasLeftTheLowFret()
    {
        // Fret 2, then sixteen notes on 4/5 that never come back: the guitar
        // look-ahead (HandAbandonment.GuitarHold = 14) is satisfied, so the hand moves.
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        for (var b = 1; b <= 16; b++) level.Notes.Add(Note(b, b % 2 == 1 ? 4 : 5));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level);

        Assert.Equal("0:2 1:4", Describe(anchors));
    }

    // ─── Templates are resolved by Id ───────────────────────────────────────

    [Fact]
    public void TheWalk_ResolvesAChordsTemplateById_NotByListPosition()
    {
        // G9+B10 fingered 2-3 (index implied on 8), stored under id 7 at list
        // position 0. By list position the walk saw no fingering and anchored on
        // the lowest fret, 9.
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, 7, (3, 9), (4, 10)));
        List<ChordTemplate> templates =
        [
            Template(7, new Dictionary<int, int> { [3] = 9, [4] = 10 }, new Dictionary<int, int> { [3] = 2, [4] = 3 })
        ];

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates);

        Assert.Equal("0:8", Describe(anchors));
    }

    // ─── Capo ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Each #494 path as a function of the capo: every fretted note sits
    ///     <paramref name="capo" /> frets higher and an OPEN note (at the capo)
    ///     is mixed in, so a capoed chart is the uncapoed one transposed.
    /// </summary>
    private static (ChartLevel Level, List<ChordTemplate> Templates, bool Bass) ContextPath(string path, int capo)
    {
        var level = new ChartLevel();
        List<ChordTemplate> templates = [];
        var bass = false;
        switch (path)
        {
            case "hold":
            {
                // The free double stop played from the line's anchor.
                level.Notes.Add(Note(0, 5 + capo, 3));
                level.Notes.Add(Note(1, capo, 2));
                level.Chords.Add(Chord(2, 0, (3, 6 + capo), (4, 6 + capo)));
                level.HandShapes.Add(Shape(2, 4, 0));
                level.Notes.Add(Note(4, 5 + capo, 3));
                templates.Add(Template(0, new Dictionary<int, int> { [3] = 6 + capo, [4] = 6 + capo },
                    new Dictionary<int, int> { [3] = 1, [4] = 1 }));
                break;
            }
            case "bounce":
            {
                int[] frets = [1, 2, 3, 4, -1, 1, 2];
                for (var i = 0; i < frets.Length; i++)
                    level.Notes.Add(frets[i] < 0 ? Note(i, capo, 2) : Note(i, frets[i] + capo, i is 2 or 3 ? 1 : 0));
                level.HandShapes.Add(Shape(2, 4));
                break;
            }
            case "pull-back":
            {
                bass = true;
                int[] frets = [1, -1, 3, 4, 5];
                for (var i = 0; i < frets.Length; i++)
                    level.Notes.Add(frets[i] < 0 ? Note(i, capo, 1) : Note(i, frets[i] + capo));
                break;
            }
            case "abandonment":
            {
                level.Notes.Add(Note(0, 1 + capo));
                for (var b = 1; b <= 16; b++) level.Notes.Add(Note(b, (b % 2 == 1 ? 3 : 4) + capo));
                level.Notes.Add(Note(17, capo, 2));
                break;
            }
        }

        return (level, templates, bass);
    }

    [Theory]
    [InlineData("hold", 3)]
    [InlineData("bounce", 2)]
    [InlineData("pull-back", 2)]
    [InlineData("abandonment", 4)]
    public void UnderACapo_EveryContextPath_IsTheUncapoedResultTransposed_AndNeverAtTheCapo(string path, int capo)
    {
        var (open, openTemplates, bass) = ContextPath(path, 0);
        var (capoed, capoedTemplates, _) = ContextPath(path, capo);

        var plain = new FretAnchorGenerationService().GenerateAnchors(open, chordTemplates: openTemplates,
            bassArrangement: bass, freeGrip: AllFree);
        var anchors = new FretAnchorGenerationService().GenerateAnchors(capoed, chordTemplates: capoedTemplates,
            capo: capo, bassArrangement: bass, freeGrip: AllFree);

        Assert.Equal(Describe(plain.Select(a => new FretAnchor(a.Position, a.Fret + capo, a.Width))), Describe(anchors));
        Assert.All(anchors, a => Assert.True(a.Fret > capo, $"anchor {a.Fret} at or below capo {capo}"));
    }

    [Fact]
    public void UnderACapo_TheContextPathsStillFire()
    {
        // The vacuity keeper for the theory above: each scenario really runs its
        // path (uncapoed), so "transposed" compares a hold, a collapsed bounce, a
        // pull-back and an abandonment, not four empty walks.
        static string Run(string path)
        {
            var (level, templates, bass) = ContextPath(path, 0);
            return Describe(new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: templates,
                bassArrangement: bass, freeGrip: AllFree));
        }

        Assert.Equal("0:5", Run("hold"));
        Assert.Equal("0:1", Run("bounce"));
        Assert.Equal("0:1 2:3", Run("pull-back"));
        Assert.Equal("0:1 1:3", Run("abandonment"));
    }
}
