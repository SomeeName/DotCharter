using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Catalog;

/// <summary>
///     A catalog entry is a tiny <c>.rscp</c>, so these drive the REAL project
///     serializer: write a fragment, read it back off disk, and assert the phrase
///     survived field for field. That is the whole reason the format was reused
///     instead of hand-writing a DTO — if it round-trips a project, it round-trips
///     a phrase.
/// </summary>
public class PhraseCatalogFragmentTests : IDisposable
{
    [Fact]
    public void Capture_PreservesEveryTemplateField_AndKeepsAuthoredVariantsDistinct()
    {
        var id = new FieldExclusion(typeof(ChordTemplate), nameof(ChordTemplate.Id),
            "Template ids belong to the fragment and are remapped on capture.");
        Assert.NotNull(id.Owner.GetProperty(id.Property));
        foreach (var property in DomainFieldProbe.SettableProperties(typeof(ChordTemplate)))
        {
            if (property.Name == id.Property) continue;
            var source = SourceArrangement();
            var first = DomainFieldProbe.Populated<ChordTemplate>();
            first.Id = 7;
            var second = first.Clone();
            property.SetValue(second, DomainFieldProbe.Alternate(property.PropertyType));
            second.Id = 8;
            source.ChordTemplates.Add(first);
            source.ChordTemplates.Add(second);
            var bars = new[]
            {
                new HandShape { TemplateId = 7, EndPosition = new FractionalPosition(1) },
                new HandShape { TemplateId = 8, Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3) }
            };

            var fragment = PhraseCatalogFragment.Build("Variants", source, [], [], bars, [])!;

            var captured = fragment.Arrangements[0];
            Assert.Equal(2, captured.ChordTemplates.Count);
            var ignored = new HashSet<string> { "Template.Id" };
            for (var i = 0; i < bars.Length; i++)
            {
                var actual = captured.ChordTemplates.Single(t => t.Id == captured.Levels[0].HandShapes[i].TemplateId);
                var expected = i == 0 ? first : second;
                Assert.Empty(DeepCompare.Diff("Template", expected, actual, ignored));
            }
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-phrase-" + Guid.NewGuid().ToString("N"));
    private readonly RscpChartSerializer _serializer = new();

    [Fact]
    public void AuthoredTemplateFlags_SurviveTheCatalogFileRoundTrip()
    {
        var source = SourceArrangement();
        var authored = new ChordTemplate
        {
            Id = 7, Name = string.Empty, Fingerless = true, GhostMask = 1,
            Pinned = true, Unnamed = true, NopInRs = true,
            Frets = new() { [0] = 3, [1] = 5 }
        };
        source.ChordTemplates.Add(authored);
        var bar = new HandShape { TemplateId = 7, EndPosition = new FractionalPosition(2) };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Authored", source, [], [], [bar], [])!);

        var actual = entry.Payload.ChordTemplates[Assert.Single(entry.Payload.HandShapes).TemplateId];
        Assert.Empty(DeepCompare.Diff("Template", authored, actual, new HashSet<string> { "Template.Id" }));
    }

    public void Dispose()
    {
        BestEffortDelete.Directory(_dir);

        GC.SuppressFinalize(this);
    }

    private static Arrangement SourceArrangement(int capo = 0, int strings = 6, string tuning = "E_STANDARD")
    {
        var arrangement = new Arrangement
        {
            Name = "Guitar 1", ArrangementType = ArrangementType.Rhythm,
            Capo = capo, StringCount = strings, TuningType = tuning
        };
        foreach (var step in TuningUtilities.GetOffsetsForTuningType(tuning, strings))
            arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Tuning = TuningUtilities.GetTuningNames(strings,
            arrangement.TuningPitches.Select(t => t.Step).ToArray());
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private PhraseCatalogEntry RoundTrip(ChartProject fragment, string id = "phrase")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, id + ".rscp");
        _serializer.Save(path, fragment);
        var reloaded = _serializer.Load(path);
        var entry = PhraseCatalogFragment.Read(id, reloaded, DateTime.UtcNow);
        Assert.NotNull(entry);
        return entry!;
    }

    [Fact]
    public void AnEmptySelectionProducesNoFragment()
    {
        Assert.Null(PhraseCatalogFragment.Build("x", SourceArrangement(),
            [], [], [], []));
    }

    [Fact]
    public void ThePhraseIsReAnchoredSoTheFirstSoundSitsOnBeatZero()
    {
        // Captured from bar 40 of one song; it must land wherever the playhead
        // is in the next one, so the stored positions are OFFSETS.
        var notes = new[]
        {
            new GuitarNote { Position = new FractionalPosition(160), StringIndex = 5, Fret = 3 },
            new GuitarNote { Position = new FractionalPosition(162), StringIndex = 4, Fret = 5 }
        };

        var fragment = PhraseCatalogFragment.Build("Riff", SourceArrangement(), notes, [], [], []);
        var entry = RoundTrip(fragment!);

        Assert.Equal(FractionalPosition.Zero, entry.Payload.BasePosition);
        Assert.Equal(0, entry.Payload.Notes[0].Position.BeatId);
        Assert.Equal(2, entry.Payload.Notes[1].Position.BeatId);
    }

    [Fact]
    public void SubBeatRhythmSurvivesTheRoundTrip()
    {
        var notes = new[]
        {
            new GuitarNote { Position = new FractionalPosition(4), StringIndex = 5, Fret = 3 },
            new GuitarNote
            {
                Position = new FractionalPosition(4, new Fraction(1, 3)), StringIndex = 5, Fret = 5
            }
        };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Triplet", SourceArrangement(), notes, [], [], [])!);

        var second = entry.Payload.Notes.Single(n => n.Fret == 5);
        Assert.Equal(0, second.Position.BeatId);
        Assert.Equal(1, second.Position.Fraction.Numerator);
        Assert.Equal(3, second.Position.Fraction.Denominator);
    }

    [Fact]
    public void NoteTechniqueSurvivesTheRoundTrip()
    {
        var note = new GuitarNote
        {
            Position = new FractionalPosition(0),
            StringIndex = 2, Fret = 7,
            Sustain = new FractionalPosition(1),
            Hopo = HopoType.HammerOn,
            Harmonic = HarmonicType.Pinch,
            Mute = MuteType.Palm,
            Vibrato = true,
            Tremolo = true,
            Accent = true,
            SlideTo = 9,
            LinkNext = true
        };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Tech", SourceArrangement(), [note], [], [], [])!);

        var read = Assert.Single(entry.Payload.Notes);
        Assert.Equal(2, read.StringIndex);
        Assert.Equal(7, read.Fret);
        Assert.Equal(HopoType.HammerOn, read.Hopo);
        Assert.Equal(HarmonicType.Pinch, read.Harmonic);
        Assert.Equal(MuteType.Palm, read.Mute);
        Assert.True(read.Vibrato);
        Assert.True(read.Tremolo);
        Assert.True(read.Accent);
        Assert.Equal(9, read.SlideTo);
        Assert.True(read.LinkNext);
        Assert.Equal(1, read.Sustain.BeatId);
    }

    [Fact]
    public void AChordCarriesItsShapeNotItsSourceTemplateId()
    {
        // Template ids are POSITIONAL and renumbered on every save, so an id is
        // meaningless outside the arrangement that minted it. What has to
        // survive is the SHAPE.
        var source = SourceArrangement();
        var template = new ChordTemplate { Id = 42, Name = "Dmin" };
        template.Frets[3] = 7;
        template.Frets[4] = 6;
        template.Frets[5] = 5;
        template.Fingers[3] = 3;
        template.Fingers[4] = 2;
        template.Fingers[5] = 1;
        source.ChordTemplates.Add(template);

        var chord = new Chord { Position = new FractionalPosition(8), TemplateId = 42 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 6 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 5 });

        var entry = RoundTrip(PhraseCatalogFragment.Build("Chord", source, [], [chord], [], [])!);

        var read = Assert.Single(entry.Payload.Chords);
        var carried = entry.Payload.ChordTemplates[read.TemplateId];
        Assert.Equal(new Dictionary<int, int> { [3] = 7, [4] = 6, [5] = 5 }, carried.Frets);
        Assert.Equal("Dmin", carried.Name);
    }

    [Fact]
    public void AuthoredFingeringSurvivesInsteadOfBeingReSuggested()
    {
        // ChordTemplateResolver re-suggests fingers for every minted template
        // (it is a pure function of the shape). A phrase captured with a
        // hand-authored or GP-imported fingering must keep it.
        var source = SourceArrangement();
        var template = new ChordTemplate { Id = 3, Name = "F" };
        for (var s = 0; s < 6; s++) template.Frets[s] = s == 0 ? 1 : 3;
        template.Fingers[0] = 1;
        for (var s = 1; s < 6; s++) template.Fingers[s] = 4;
        source.ChordTemplates.Add(template);

        var chord = new Chord { Position = FractionalPosition.Zero, TemplateId = 3 };
        for (var s = 0; s < 6; s++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = s == 0 ? 1 : 3 });

        var entry = RoundTrip(PhraseCatalogFragment.Build("F barre", source, [], [chord], [], [])!);

        var carried = entry.Payload.ChordTemplates[entry.Payload.Chords[0].TemplateId];
        Assert.Equal(1, carried.Fingers[0]);
        Assert.Equal(4, carried.Fingers[5]);
    }

    [Fact]
    public void HandShapesAndAnchorsTravelWithThePhrase()
    {
        var source = SourceArrangement();
        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[4] = 7;
        template.Frets[5] = 7;
        source.ChordTemplates.Add(template);

        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 7 });

        var shape = new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = 0
        };
        var anchor = new FretAnchor(new FractionalPosition(4), 7, 5);

        var entry = RoundTrip(PhraseCatalogFragment.Build("Power", source, [], [chord], [shape], [anchor])!);

        var readShape = Assert.Single(entry.Payload.HandShapes);
        Assert.Equal(0, readShape.Position.BeatId);
        Assert.Equal(2, readShape.EndPosition.BeatId);
        Assert.True(entry.Payload.ChordTemplates.ContainsKey(readShape.TemplateId));

        var readAnchor = Assert.Single(entry.Payload.Anchors);
        Assert.Equal(0, readAnchor.Position.BeatId);
        Assert.Equal(7, readAnchor.Fret);
        Assert.Equal(5, readAnchor.Width);
    }

    [Fact]
    public void AHandShapeThatAdoptsItsChordsGrip_InheritsThatGripsGhostMask()
    {
        var source = SourceArrangement();
        source.ChordTemplates.Add(new ChordTemplate
        {
            Id = 7, GhostMask = 1 << 4, Frets = new() { [4] = 5, [5] = 7 }
        });
        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 7 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 7 });
        var shape = new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6),
            TemplateId = -1, GhostMask = 1 << 5
        };

        var fragment = PhraseCatalogFragment.Build("Grip", source, [], [chord], [shape], [])!;

        var captured = Assert.Single(fragment.Arrangements);
        var level = Assert.Single(captured.Levels);
        var capturedShape = Assert.Single(level.HandShapes);
        var grip = Assert.Single(captured.ChordTemplates);
        Assert.Equal(Assert.Single(level.Chords).TemplateId, capturedShape.TemplateId);
        Assert.Equal(grip.Id, capturedShape.TemplateId);
        Assert.Null(capturedShape.GhostMask);
        Assert.Equal(1 << 4, capturedShape.GetGhostMask(grip));
        Assert.Equal(1 << 5, shape.GhostMask);
    }

    [Fact]
    public void AnUntemplatedHandShapeWithNoChordUnderItIsDropped()
    {
        // The rscp writer coerces HandShape.TemplateId -1 to 0 — i.e. a real
        // shape — so storing such a shape would silently point it at somebody
        // else's chord. It carries no information anyway.
        var shape = new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2), TemplateId = -1
        };
        var note = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 5, Fret = 3 };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Loose", SourceArrangement(), [note], [], [shape], [])!);

        Assert.Empty(entry.Payload.HandShapes);
        Assert.Single(entry.Payload.Notes);
    }

    [Fact]
    public void TheSourceTuningCapoAndStringCountAreRecorded()
    {
        var source = SourceArrangement(capo: 3, strings: 7, tuning: "DROP_D");
        var note = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 6, Fret = 5 };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Seven", source, [note], [], [], [])!);

        Assert.Equal(3, entry.Source.Capo);
        Assert.Equal(7, entry.Source.StringCount);
        Assert.Equal(
            TuningUtilities.GetOffsetsForTuningType("DROP_D", 7),
            entry.Source.TuningOffsets.ToArray());
    }

    [Fact]
    public void AnInconsistentTuningIsWrittenAsCustomSoThePitchesSurvive()
    {
        // The rscp writer only serializes explicit offsets for CUSTOM; every
        // other type is re-derived from its NAME. An arrangement whose offsets
        // disagree with its declared type would otherwise come back retuned.
        var source = SourceArrangement();
        source.TuningPitches.Clear();
        foreach (var step in new[] { -4, -2, -2, -2, -2, -2 }) source.TuningPitches.Add(new TuningOffset(step));
        var note = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 0, Fret = 5 };

        var entry = RoundTrip(PhraseCatalogFragment.Build("Odd", source, [note], [], [], [])!);

        Assert.Equal(new[] { -4, -2, -2, -2, -2, -2 }, entry.Source.TuningOffsets.ToArray());
    }

    [Fact]
    public void TheNameIsCarriedInsideTheFragmentNotTheFileName()
    {
        var note = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 5, Fret = 3 };

        var entry = RoundTrip(
            PhraseCatalogFragment.Build("Chorus riff #2 (½ time)", SourceArrangement(), [note], [], [], [])!,
            "chorus-riff-2");

        Assert.Equal("Chorus riff #2 (½ time)", entry.Name);
        Assert.Equal("chorus-riff-2", entry.Id);
    }

    [Fact]
    public void CapturingDoesNotMutateTheSourceObjects()
    {
        var source = SourceArrangement();
        var note = new GuitarNote { Position = new FractionalPosition(40), StringIndex = 5, Fret = 3 };

        PhraseCatalogFragment.Build("Riff", source, [note], [], [], []);

        Assert.Equal(40, note.Position.BeatId);
        Assert.Empty(source.ChordTemplates);
    }

    [Fact]
    public void ReadReturnsNullForAProjectWithNoPhraseContent()
    {
        var empty = new ChartProject();
        empty.Arrangements.Add(SourceArrangement());

        Assert.Null(PhraseCatalogFragment.Read("x", empty, DateTime.UtcNow));
    }
}
