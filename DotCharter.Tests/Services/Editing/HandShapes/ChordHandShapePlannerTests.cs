using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The hand-shape grouping rule as a pure planner (#478): the tab importer, the
///     editor's "Generate hand shapes (arrangement)" and the D11 auto-fix all run it.
///     These pin the grouping itself on a synthetic chart, through the EDITOR's grip
///     source and options (<see cref="ArrangementHandShapeGenerator" />); the importer's
///     side is pinned by <c>ChordHandShapeBuilderTests</c> and the equivalence test.
/// </summary>
public sealed class ChordHandShapePlannerTests
{
    internal static Arrangement Arr(int levels = 1)
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        for (var i = 0; i < levels; i++) arr.Levels.Add(new ChartLevel { DifficultyIndex = i });
        return arr;
    }

    /// <summary>A chord at <paramref name="beat" /> playing (string, fret) pairs, every
    /// note sustained <paramref name="sustain" /> beats.</summary>
    internal static Chord Chord(double beat, double sustain, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = Pos(beat) };
        foreach (var (s, f) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = Pos(sustain) });
        return chord;
    }

    internal static FractionalPosition Pos(double beats) =>
        new(0, new Fraction((int)Math.Round(beats * 4), 4));

    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];
    private static readonly (int, int)[] A5 = [(0, 5), (1, 7), (2, 7)];

    private static List<PlannedHandShape> PlanOnly(Arrangement arr) =>
        ArrangementHandShapeGenerator.Plan(arr).Levels.SelectMany(l => l.HandShapes).ToList();

    [Fact]
    public void RepeatedStrumsWithinTwoBeats_ShareOneBar()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Chord(0, 1, G5));
        level.Chords.Add(Chord(1, 1, G5));
        level.Chords.Add(Chord(3.5, 0.5, G5)); // 1.5 beats after the previous one's end

        var bar = Assert.Single(PlanOnly(arr));

        Assert.Equal(Pos(0), bar.Position);
        Assert.Equal(Pos(4), bar.EndPosition);
        Assert.Equal(3, bar.Chords.Count);
    }

    [Fact]
    public void AStrumMoreThanTwoBeatsAfterTheLastOnesEnd_OpensANewBar()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Chord(0, 1, G5));
        level.Chords.Add(Chord(3.25, 1, G5)); // 2.25 beats of gap

        var bars = PlanOnly(arr);

        Assert.Equal(2, bars.Count);
        Assert.Same(bars[0].Template, bars[1].Template); // one shape, minted once
        Assert.Single(ArrangementHandShapeGenerator.Plan(arr).MintedTemplates);
    }

    [Fact]
    public void AFretChange_OpensANewBar_AndTheFirstBarEndsOnIt()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Chord(0, 2, G5));
        level.Chords.Add(Chord(1, 1, A5)); // starts under the G5's sustain

        var bars = PlanOnly(arr);

        Assert.Equal(2, bars.Count);
        Assert.Equal(Pos(1), bars[0].EndPosition); // clipped at the next sound
        Assert.Equal(Pos(1), bars[1].Position);
        Assert.NotSame(bars[0].Template, bars[1].Template);
        Assert.Equal(5, bars[1].Template.Frets[0]);
    }

    [Fact]
    public void ACoveredChord_GetsNoBar_AndTheExistingBarIsNotTouched()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        var existing = new HandShape { Position = Pos(0), EndPosition = Pos(2), TemplateId = 7, Name = "mine" };
        level.HandShapes.Add(existing);
        level.Chords.Add(Chord(0, 1, G5));
        level.Chords.Add(Chord(1, 1, G5));

        var plan = ArrangementHandShapeGenerator.Plan(arr);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.MintedTemplates);
        Assert.Same(existing, Assert.Single(level.HandShapes));
        Assert.Equal(Pos(2), existing.EndPosition);
    }

    [Fact]
    public void AChordOnABarsEnd_IsOutsideIt_AndGetsItsOwnBar()
    {
        // Half-open: [0, 2) does not hold a chord at 2.
        var arr = Arr();
        var level = arr.Levels[0];
        level.HandShapes.Add(new HandShape { Position = Pos(0), EndPosition = Pos(2), TemplateId = -1 });
        level.Chords.Add(Chord(0, 1, G5));
        level.Chords.Add(Chord(2, 1, G5));

        var bar = Assert.Single(PlanOnly(arr));

        Assert.Equal(Pos(2), bar.Position);
        Assert.Equal(Pos(3), bar.EndPosition);
    }

    [Fact]
    public void ANewBar_StopsAtTheNextBarThatExists()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        level.HandShapes.Add(new HandShape { Position = Pos(2), EndPosition = Pos(4), TemplateId = -1 });
        level.Chords.Add(Chord(0, 4, G5)); // sustain runs into the existing bar

        var bar = Assert.Single(PlanOnly(arr));

        Assert.Equal(Pos(2), bar.EndPosition);
    }

    [Fact]
    public void ARunBreaksAtAnAnchor_SoNoBarHoldsOneInside()
    {
        // An anchor strictly inside a hand shape is I20.
        var arr = Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Chord(0, 1, G5));
        level.Chords.Add(Chord(1, 1, G5));
        level.Anchors.Add(new FretAnchor { Position = Pos(1), Fret = 3, Width = 4 });

        var bars = PlanOnly(arr);

        Assert.Equal(2, bars.Count);
        Assert.Equal(Pos(1), bars[0].EndPosition);
        Assert.All(bars, b => Assert.DoesNotContain(level.Anchors,
            a => a.Position > b.Position && a.Position < b.EndPosition));
    }

    [Fact]
    public void ATaillessChord_IsStillCovered()
    {
        // A chord drawn in the editor usually has no sustain: its authored span is
        // zero, and a bar ending ON its onset covers nothing (half-open).
        var arr = Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Chord(0, 0, G5));
        level.Chords.Add(Chord(6, 0, A5));

        var bars = PlanOnly(arr);

        Assert.Equal(2, bars.Count);
        Assert.Equal(Pos(1), bars[0].EndPosition); // a beat, the next sound is far
        Assert.Equal(Pos(7), bars[1].EndPosition);
        Assert.All(bars, b => Assert.True(b.ToHandShape().Covers(b.Chords[0].Position)));
    }

    [Fact]
    public void EveryDifficultyLevelIsPlanned_AndAShapeOnSeveralLevelsMintsOnce()
    {
        var arr = Arr(levels: 3);
        arr.Levels[0].Chords.Add(Chord(0, 1, G5));
        arr.Levels[1].Chords.Add(Chord(0, 1, G5));
        arr.Levels[2].Chords.Add(Chord(0, 1, G5));
        arr.Levels[2].Chords.Add(Chord(2, 1, A5));
        // Level 1 already has its bar.
        arr.Levels[1].HandShapes.Add(new HandShape { Position = Pos(0), EndPosition = Pos(1), TemplateId = -1 });

        var plan = ArrangementHandShapeGenerator.Plan(arr);

        Assert.Equal([arr.Levels[0], arr.Levels[2]], plan.Levels.Select(l => l.Level));
        Assert.Equal(3, plan.HandShapeCount);
        Assert.Equal(2, plan.MintedTemplates.Count); // G5 once, A5 once
        Assert.Same(plan.Levels[0].HandShapes[0].Template, plan.Levels[1].HandShapes[0].Template);
    }

    [Fact]
    public void AChordKeepsItsOwnTemplate_AndAnArpeggioChordGetsAnArpeggioBar()
    {
        var arr = Arr();
        var arp = new ChordTemplate { Id = 0, Name = "G5", Arpeggio = true };
        foreach (var (s, f) in G5) arp.Frets[s] = f;
        arr.ChordTemplates.Add(arp);
        var level = arr.Levels[0];
        var arpChord = Chord(0, 1, G5);
        arpChord.TemplateId = arp.Id;
        level.Chords.Add(arpChord);
        level.Chords.Add(Chord(1, 1, G5)); // same frets, on the sentinel ⇒ PLAIN

        var plan = ArrangementHandShapeGenerator.Plan(arr);
        var bars = plan.Levels.Single().HandShapes;

        Assert.Equal(2, bars.Count); // a plain strum does not join an arpeggio bar
        Assert.Same(arp, bars[0].Template);
        var plain = Assert.Single(plan.MintedTemplates);
        Assert.Same(plain, bars[1].Template);
        Assert.False(plain.IsArpeggio);
    }

    [Fact]
    public void ThePlannerNeverWritesTheLevel()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        var chord = Chord(0, 2, G5);
        level.Chords.Add(chord);

        _ = ArrangementHandShapeGenerator.Plan(arr);

        Assert.Empty(level.HandShapes);
        Assert.Empty(arr.ChordTemplates);
        Assert.Equal(-1, chord.TemplateId);
        Assert.All(chord.ChordNotes, n => Assert.Equal(Pos(2), n.Sustain));
    }
}
