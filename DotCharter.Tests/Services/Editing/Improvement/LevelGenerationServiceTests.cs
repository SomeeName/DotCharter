namespace DotCharter.Tests.Services.Editing.Improvement;

public sealed class LevelGenerationServiceTests
{
    private const int LevelCount = 4;

    [Fact]
    public void TryGenerate_ProducesRequestedLevelCount_WithSequentialDifficultyIndices()
    {
        var arrangement = BuildDenseArrangement();

        var changed = new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        Assert.True(changed);
        Assert.Equal(LevelCount, arrangement.Levels.Count);
        Assert.Equal(Enumerable.Range(0, LevelCount), arrangement.Levels.Select(l => l.DifficultyIndex));
    }

    /// <summary>
    ///     Building the levels is the only part of generation whose cost scales
    ///     with the request (up to 20 × every sound of the source level), which
    ///     is why it is the part that reports. One report per level BUILT, in
    ///     order, and nothing at all for a request the method refuses — a bar
    ///     that moved for a no-op is a lie about work.
    /// </summary>
    [Fact]
    public void TryGenerate_ReportsOneStepPerLevelBuilt()
    {
        var arrangement = BuildDenseArrangement();
        var built = new List<int>();

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats(),
            new InlineProgress(built.Add));

        Assert.Equal(Enumerable.Range(1, LevelCount), built);
    }

    [Fact]
    public void TryGenerate_ReportsNothingWhenItRefusesTheRequest()
    {
        var built = new List<int>();

        Assert.False(new LevelGenerationService().TryGenerate(BuildDenseArrangement(), 1, BuildBeats(),
            new InlineProgress(built.Add)));

        Assert.Empty(built);
    }

    private sealed class InlineProgress(Action<int> onReport) : IProgress<int>
    {
        public void Report(int value) => onReport(value);
    }

    [Fact]
    public void TryGenerate_TopLevelIsVerbatimSourceContent()
    {
        var arrangement = BuildDenseArrangement();
        var sourceNoteCount = arrangement.Levels[0].Notes.Count;
        var sourceChordCount = arrangement.Levels[0].Chords.Count;
        var sourceBends = arrangement.Levels[0].Notes.Sum(n => n.BendValues.Count);

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        var top = arrangement.Levels[^1];
        Assert.Equal(sourceNoteCount, top.Notes.Count);
        Assert.Equal(sourceChordCount, top.Chords.Count);
        Assert.Equal(sourceBends, top.Notes.Sum(n => n.BendValues.Count));
    }

    [Fact]
    public void TryGenerate_DensityIsMonotonic_AcrossLevels()
    {
        var arrangement = BuildDenseArrangement();

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        for (var k = 0; k + 1 < arrangement.Levels.Count; k++)
        {
            var lower = arrangement.Levels[k].Notes.Count + arrangement.Levels[k].Chords.Count;
            var higher = arrangement.Levels[k + 1].Notes.Count + arrangement.Levels[k + 1].Chords.Count;
            Assert.True(lower <= higher,
                $"Level {k} has {lower} sounds but level {k + 1} has {higher}.");
        }
    }

    [Fact]
    public void TryGenerate_IsDeterministic()
    {
        var first = BuildDenseArrangement();
        var second = BuildDenseArrangement();

        new LevelGenerationService().TryGenerate(first, LevelCount, BuildBeats());
        new LevelGenerationService().TryGenerate(second, LevelCount, BuildBeats());

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void TryGenerate_AllCollectionsStayPositionSorted()
    {
        var arrangement = BuildDenseArrangement();

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        foreach (var level in arrangement.Levels)
        {
            AssertSorted(level.Notes.Select(n => n.Position));
            AssertSorted(level.Chords.Select(c => c.Position));
            AssertSorted(level.Anchors.Select(a => a.Position));
            AssertSorted(level.HandShapes.Select(h => h.Position));
            AssertSorted(level.FretHandMutes.Select(f => f.Position));
        }
    }

    [Fact]
    public void TryGenerate_PhraseStartingSounds_SurviveToTheLowestLevel()
    {
        var arrangement = BuildDenseArrangement();
        var phraseStarts = arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase))
            .Select(ep => ep.Position)
            .ToList();
        Assert.NotEmpty(phraseStarts);

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        var lowest = arrangement.Levels[0];
        foreach (var boundary in phraseStarts)
        {
            var hasSoundAtOrAfter = lowest.Notes.Any(n => n.Position >= boundary) ||
                                    lowest.Chords.Any(c => c.Position >= boundary);
            Assert.True(hasSoundAtOrAfter, $"No sound survived at/after phrase start {boundary}.");
        }
    }

    [Fact]
    public void TryGenerate_LowLevels_StripTechniques_ButKeepPalmMute()
    {
        var arrangement = BuildDenseArrangement();

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        var lowest = arrangement.Levels[0];
        Assert.NotEmpty(lowest.Notes);
        Assert.All(lowest.Notes, note =>
        {
            Assert.Empty(note.BendValues);
            Assert.Null(note.SlideTo);
            Assert.False(note.UnpitchedSlide);
            Assert.False(note.Tremolo);
            Assert.False(note.Vibrato);
            Assert.Equal(HopoType.None, note.Hopo);
        });
        // The palm-muted downbeat note survives with its mute intact.
        Assert.Contains(lowest.Notes, n => n.Mute == MuteType.Palm);
    }

    [Fact]
    public void TryGenerate_LowLevels_SimplifyChordsToRootNotes()
    {
        var arrangement = BuildDenseArrangement();
        var chordPosition = arrangement.Levels[0].Chords[0].Position;
        var rootChordNote = arrangement.Levels[0].Chords[0].ChordNotes.OrderBy(n => n.StringIndex).First();
        var expectedString = rootChordNote.StringIndex;
        var expectedFret = rootChordNote.Fret;

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        var lowest = arrangement.Levels[0];
        Assert.Empty(lowest.Chords);
        Assert.Empty(lowest.HandShapes);
        var root = Assert.Single(lowest.Notes, n => n.Position == chordPosition);
        Assert.Equal(expectedString, root.StringIndex);
        Assert.Equal(expectedFret, root.Fret);
    }

    [Fact]
    public void TryGenerate_ClearsLinkNext_WhenLinkTargetWasThinnedAway()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel { DifficultyIndex = 0 };
        // Linked pair on string 0: the second note is an off-beat off-phrase
        // sound that the lowest level drops.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(0, new Fraction(1, 2)), LinkNext = true
        });
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0, new Fraction(1, 2)), StringIndex = 0, Fret = 7
        });
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "riff" });

        new LevelGenerationService().TryGenerate(arrangement, 2);

        var lowest = arrangement.Levels[0];
        var survivor = Assert.Single(lowest.Notes);
        Assert.Equal(new FractionalPosition(0), survivor.Position);
        Assert.False(survivor.LinkNext);

        // The top level keeps both notes and the link.
        var top = arrangement.Levels[1];
        Assert.Equal(2, top.Notes.Count);
        Assert.True(top.Notes[0].LinkNext);
    }

    [Fact]
    public void TryGenerate_SetsPhraseInfoMaxDifficulty_ForEveryPhrase()
    {
        var arrangement = BuildDenseArrangement();

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());

        Assert.All(arrangement.PhraseInfos.Values, info => Assert.Equal(LevelCount - 1, info.MaxDifficulty));
        foreach (var phrase in arrangement.EventPoints.Where(ep => ep.Phrase.Length > 0).Select(ep => ep.Phrase))
            Assert.True(arrangement.PhraseInfos.ContainsKey(phrase), $"Missing PhraseInfo for '{phrase}'.");
        // Existing solo flag survives.
        Assert.True(arrangement.PhraseInfos["chorus"].Solo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TryGenerate_ReturnsFalse_ForInvalidLevelCount(int levelCount)
    {
        var arrangement = BuildDenseArrangement();

        Assert.False(new LevelGenerationService().TryGenerate(arrangement, levelCount));
        Assert.Single(arrangement.Levels);
    }

    [Fact]
    public void TryGenerate_ReturnsFalse_WhenArrangementHasNoLevels() => Assert.False(new LevelGenerationService().TryGenerate(new Arrangement(), LevelCount));

    [Fact]
    public void TryGenerate_RoundTripsWithLevelSquash()
    {
        var arrangement = BuildDenseArrangement();
        var sourceNoteCount = arrangement.Levels[0].Notes.Count;
        var sourceChordCount = arrangement.Levels[0].Chords.Count;

        new LevelGenerationService().TryGenerate(arrangement, LevelCount, BuildBeats());
        Assert.Equal(LevelCount, arrangement.Levels.Count);

        // Squash puts every phrase back onto its MaxDifficulty level — which
        // the generator set to the top level, i.e. the verbatim source.
        Assert.True(new LevelSquashService().TrySquash(arrangement));
        var squashed = Assert.Single(arrangement.Levels);
        Assert.Equal(sourceNoteCount, squashed.Notes.Count);
        Assert.Equal(sourceChordCount, squashed.Chords.Count);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>16 beats, a measure every 4 beats, 120 BPM.</summary>
    private static List<BeatMarker> BuildBeats()
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++)
            beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    /// <summary>A single max-difficulty level: two phrases ("verse" @ 0,
    /// "chorus" @ 8), 16th-note runs with techniques, a chord, anchors, a
    /// hand shape and a palm-muted downbeat.</summary>
    private static Arrangement BuildDenseArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel { DifficultyIndex = 0 };

        // Verse: dense run over beats 0..7. Downbeat notes carry techniques
        // too, so the technique-stripping rule is exercised on notes that
        // actually survive to the lowest level.
        for (var beat = 0; beat < 8; beat++)
        {
            for (var sub = 0; sub < 4; sub++)
            {
                var note = new GuitarNote
                {
                    Position = new FractionalPosition(beat, new Fraction(sub, 4)),
                    StringIndex = beat % 3,
                    Fret = 3 + sub
                };
                if (sub == 0 && beat % 2 == 1)
                {
                    note.Vibrato = true;
                    note.VibratoStrength = 40;
                    note.BendValues.Add(new BendValue(note.Position, 1.0));
                }

                if (sub == 1) note.Hopo = HopoType.HammerOn;
                if (sub == 2) note.Tremolo = true;
                if (sub == 3)
                {
                    note.SlideTo = 8;
                    note.Vibrato = true;
                    note.VibratoStrength = 40;
                }

                level.Notes.Add(note);
            }
        }

        // Chorus: chord at its phrase start + sparse notes.
        var chord = new Chord
        {
            Position = new FractionalPosition(8),
            TemplateId = 0,
            ChordNotes =
            {
                new ChordNote { StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1) },
                new ChordNote { StringIndex = 1, Fret = 7 },
                new ChordNote { StringIndex = 2, Fret = 7 }
            }
        };
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8),
            EndPosition = new FractionalPosition(9),
            TemplateId = 0
        });
        for (var beat = 9; beat < 16; beat++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beat),
                StringIndex = 0,
                Fret = 5,
                // Palm-muted downbeat chug on the measure start — must keep
                // its mute at every difficulty.
                Mute = beat == 12 ? MuteType.Palm : MuteType.None
            });
        }

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 3));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 5));
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(12)));

        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Phrase = "chorus" });
        arrangement.PhraseInfos["verse"] = new PhraseInfo(0, false);
        arrangement.PhraseInfos["chorus"] = new PhraseInfo(0, true);
        return arrangement;
    }

    private static string Fingerprint(Arrangement arrangement)
    {
        var parts = new List<string>();
        foreach (var level in arrangement.Levels)
        {
            parts.Add($"L{level.DifficultyIndex}");
            foreach (var n in level.Notes)
            {
                parts.Add(
                    $"N {n.Position} s{n.StringIndex} f{n.Fret} su{n.Sustain} h{n.Hopo} t{n.Tremolo} v{n.Vibrato} sl{n.SlideTo} b{n.BendValues.Count} m{n.Mute} ln{n.LinkNext}");
            }

            foreach (var c in level.Chords)
                parts.Add($"C {c.Position} t{c.TemplateId} n{c.ChordNotes.Count}");
            foreach (var a in level.Anchors) parts.Add($"A {a.Position} f{a.Fret}");
            foreach (var h in level.HandShapes) parts.Add($"H {h.Position}..{h.EndPosition}");
            foreach (var f in level.FretHandMutes) parts.Add($"F {f.Position}");
        }

        return string.Join("|", parts);
    }

    private static void AssertSorted(IEnumerable<FractionalPosition> positions)
    {
        var list = positions.ToList();
        for (var i = 1; i < list.Count; i++)
            Assert.True(list[i - 1] <= list[i], $"Out of order at {i}: {list[i - 1]} > {list[i]}.");
    }
}
