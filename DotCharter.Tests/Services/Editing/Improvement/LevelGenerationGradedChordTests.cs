namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     #490: a generated lower level reduces a chord IN STEPS — bass note, then the strings above
///     it in order as the depth rises, then the whole chord — the ladder measured on official
///     Rocksmith content (<see cref="ChordReductionPlanner" />, <c>LocalChordLadderMeasurementTest</c>).
///     Every reduced grip sits on a resolved template fingered like the full chord; no source
///     template is ever touched; hand shapes follow what the level still plays.
/// </summary>
public sealed class LevelGenerationGradedChordTests
{
    /// <summary>Eleven levels: level k sits at depth k/10.</summary>
    private const int ElevenLevels = 11;

    private static readonly Dictionary<int, int> BarreFrets = new() { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 };
    private static readonly Dictionary<int, int> BarreFingers = new() { [0] = 1, [1] = 3, [2] = 4, [3] = 2, [4] = 1, [5] = 1 };

    [Theory]
    [InlineData(0.0, new[] { 0 })]
    [InlineData(0.2, new[] { 0 })]
    [InlineData(0.4, new[] { 0 })]
    [InlineData(0.5, new[] { 0, 1 })]
    [InlineData(0.6, new[] { 0, 1, 2 })]
    [InlineData(0.65, new[] { 0, 1, 2, 3 })]
    [InlineData(1.0, new[] { 0, 1, 2, 3, 4, 5 })]
    public void SixStringBarre_KeepsTheBassFirstThenTheStringsAboveIt(double depth, int[] expected) =>
        Assert.Equal(expected, ChordReductionPlanner.KeptStrings(Barre(), depth));

    [Fact]
    public void EveryChordSize_GrowsMonotonicallyFromTheBassToTheFullChord()
    {
        for (var size = 2; size <= 7; size++)
        {
            var chord = new Chord();
            for (var s = 0; s < size; s++) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 3 });

            var previous = new List<int>();
            for (var step = 0; step <= 20; step++)
            {
                var kept = ChordReductionPlanner.KeptStrings(chord, step / 20.0);
                Assert.Equal(0, kept[0]);
                Assert.True(previous.All(kept.Contains), $"size {size}: depth {step / 20.0} dropped a string a lower depth kept");
                previous = kept;
            }

            Assert.Equal(size, previous.Count);
        }
    }

    [Fact]
    public void TryGenerate_ReducesTheBarreInSteps_OnResolvedTemplatesFingeredLikeTheBarre()
    {
        var arrangement = BarreArrangement(out var source);
        var snapshot = source.Clone();

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, ElevenLevels));

        for (var k = 0; k <= 4; k++)
        {
            var level = Level(arrangement, k);
            Assert.Empty(level.Chords);
            var bass = Assert.Single(level.Notes);
            Assert.Equal((0, 5), (bass.StringIndex, bass.Fret));
        }

        AssertReduced(arrangement, Level(arrangement, 5), [0, 1], source);
        AssertReduced(arrangement, Level(arrangement, 6), [0, 1, 2], source);
        for (var k = 7; k <= 10; k++)
        {
            var full = Assert.Single(Level(arrangement, k).Chords);
            Assert.Equal(source.Id, full.TemplateId);
            Assert.Equal(6, full.ChordNotes.Count);
        }

        // The source template is the same INSTANCE, unchanged, and still first.
        Assert.Same(source, arrangement.ChordTemplates[0]);
        Assert.Equal(snapshot, source);
        Assert.Equal(3, arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void TryGenerate_KeepsTheSourcesArpeggioFlavour_InBothSpellings()
    {
        foreach (var flavour in new[] { new ArpeggioFlavour(true, false), new ArpeggioFlavour(false, true) })
        {
            var arrangement = BarreArrangement(out var source);
            flavour.ApplyTo(source);

            Assert.True(new LevelGenerationService().TryGenerate(arrangement, ElevenLevels));

            var reduced = TemplateOf(arrangement, Assert.Single(Level(arrangement, 5).Chords));
            Assert.Equal(flavour, ArpeggioFlavour.Of(reduced));
            Assert.NotSame(source, reduced);
        }
    }

    /// <summary>A same-fret template with the hand somewhere else is NOT the reduced grip;
    /// one fingered exactly as the barre fingers those strings is, and is reused.</summary>
    [Fact]
    public void TryGenerate_ReusesOnlyATemplateWithTheBarresOwnFingering()
    {
        var arrangement = BarreArrangement(out _);
        var otherHand = new ChordTemplate { Id = 1, Name = "A5", Frets = { [0] = 5, [1] = 7 }, Fingers = { [0] = 1, [1] = 4 } };
        var sameHand = new ChordTemplate { Id = 2, Name = "A5", Frets = { [0] = 5, [1] = 7, [2] = 7 }, Fingers = { [0] = 1, [1] = 3, [2] = 4 } };
        arrangement.ChordTemplates.Add(otherHand);
        arrangement.ChordTemplates.Add(sameHand);

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, ElevenLevels));

        var twoStrings = TemplateOf(arrangement, Assert.Single(Level(arrangement, 5).Chords));
        Assert.NotSame(otherHand, twoStrings);
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 3 }, twoStrings.Fingers);
        Assert.Same(sameHand, TemplateOf(arrangement, Assert.Single(Level(arrangement, 6).Chords)));
        Assert.Equal(4, arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void TryGenerate_Twice_MintsNoSecondSetOfTemplates()
    {
        var arrangement = BarreArrangement(out _);
        var service = new LevelGenerationService();
        Assert.True(service.TryGenerate(arrangement, ElevenLevels));
        var afterFirst = arrangement.ChordTemplates.ToList();

        Assert.True(service.TryGenerate(arrangement, ElevenLevels));

        Assert.Equal(afterFirst, arrangement.ChordTemplates, ReferenceEqualityComparer.Instance);
    }

    /// <summary>Official ladders put a reduced grip's bar on the REDUCED template (99.5–100 %)
    /// and drop the bar of a chord that came down to its bass note (96–100 %).</summary>
    [Fact]
    public void TryGenerate_HandShapeFollowsWhatTheLevelPlays()
    {
        var arrangement = BarreArrangement(out var source);
        arrangement.Levels[0].HandShapes.Add(new HandShape
        { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(1), TemplateId = source.Id });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, ElevenLevels));

        Assert.Empty(Level(arrangement, 2).HandShapes);
        var reducedChord = Assert.Single(Level(arrangement, 5).Chords);
        Assert.Equal(reducedChord.TemplateId, Assert.Single(Level(arrangement, 5).HandShapes).TemplateId);
        Assert.NotEqual(source.Id, reducedChord.TemplateId);
        Assert.Equal(source.Id, Assert.Single(Level(arrangement, 8).HandShapes).TemplateId);
    }

    /// <summary>An arpeggio passage keeps its bar on the EASIEST level while two or more of its
    /// notes are still there; a bar over a lone note on a low level goes.</summary>
    [Fact]
    public void TryGenerate_ArpeggioPassageKeepsItsBarOnTheLowestLevel()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        arrangement.ChordTemplates.Add(new ChordTemplate
        { Id = 0, Name = "A", Arpeggio = true, Frets = { [1] = 0, [2] = 2, [3] = 2, [4] = 2 } });
        for (var beat = 0; beat < 4; beat++)
        {
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = $"p{beat}" });
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat + 1, Fret = beat == 0 ? 0 : 2 });
        }

        level.HandShapes.Add(new HandShape
        { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4), TemplateId = 0 });
        // A lone note under its own bar, later in the song.
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "p8" });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(8), StringIndex = 0, Fret = 3 });
        level.HandShapes.Add(new HandShape
        { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), TemplateId = 0 });

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, 5));

        var easiest = Level(arrangement, 0);
        Assert.Equal(5, easiest.Notes.Count);
        var bar = Assert.Single(easiest.HandShapes);
        Assert.Equal((new FractionalPosition(0), new FractionalPosition(4), 0), (bar.Position, bar.EndPosition, bar.TemplateId));
        Assert.Equal(2, Level(arrangement, 4).HandShapes.Count);
    }

    /// <summary>A link INTO a chord lands on that chord's string; a level that reduced the
    /// string away cannot spell the link, so it is cleared there and kept where the chord is whole.</summary>
    [Fact]
    public void TryGenerate_ClearsALinkIntoAStringTheReducedChordDropped()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "G", Frets = { [0] = 3, [1] = 5, [2] = 5, [3] = 4 } });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "a" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1), Phrase = "b" });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
        });
        level.Chords.Add(FourStringChord(new FractionalPosition(1)));

        Assert.True(new LevelGenerationService().TryGenerate(arrangement, ElevenLevels));

        // Depth 0.6: a four-string chord keeps strings 0 and 1 — nothing lands on string 2.
        Assert.Equal(2, Assert.Single(Level(arrangement, 6).Chords).ChordNotes.Count);
        Assert.False(Assert.Single(Level(arrangement, 6).Notes).LinkNext);
        Assert.Equal(4, Assert.Single(Level(arrangement, 7).Chords).ChordNotes.Count);
        Assert.True(Assert.Single(Level(arrangement, 7).Notes).LinkNext);
    }

    private static Chord FourStringChord(FractionalPosition position) => new()
    {
        Position = position, TemplateId = 0,
        ChordNotes =
        {
            new ChordNote { StringIndex = 0, Fret = 3 }, new ChordNote { StringIndex = 1, Fret = 5 },
            new ChordNote { StringIndex = 2, Fret = 5 }, new ChordNote { StringIndex = 3, Fret = 4 }
        }
    };

    private static void AssertReduced(Arrangement arrangement, ChartLevel level, int[] strings, ChordTemplate source)
    {
        var chord = Assert.Single(level.Chords);
        Assert.Empty(level.Notes);
        Assert.Equal(strings, chord.ChordNotes.Select(n => n.StringIndex).Order());
        var template = TemplateOf(arrangement, chord);
        Assert.NotSame(source, template);
        Assert.Equal(strings.ToDictionary(s => s, s => BarreFrets[s]), template.Frets);
        Assert.Equal(strings.ToDictionary(s => s, s => BarreFingers[s]), template.Fingers);
        Assert.True(template.DescribesChordNotes(chord.ChordNotes));
        Assert.Equal(ArpeggioFlavour.Of(source), ArpeggioFlavour.Of(template));
    }

    private static ChordTemplate TemplateOf(Arrangement arrangement, Chord chord) =>
        Assert.Single(arrangement.ChordTemplates, t => t.Id == chord.TemplateId);

    private static ChartLevel Level(Arrangement arrangement, int difficulty) =>
        Assert.Single(arrangement.Levels, l => l.DifficultyIndex == difficulty);

    private static Chord Barre()
    {
        var chord = new Chord { Position = new FractionalPosition(0), TemplateId = 0 };
        foreach (var (s, fret) in BarreFrets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        return chord;
    }

    private static Arrangement BarreArrangement(out ChordTemplate source)
    {
        var arrangement = new Arrangement { Name = "Rhythm" };
        source = new ChordTemplate { Id = 0, Name = "A", Frets = new(BarreFrets), Fingers = new(BarreFingers) };
        arrangement.ChordTemplates.Add(source);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "verse" });
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Chords.Add(Barre());
        arrangement.Levels.Add(level);
        return arrangement;
    }
}
