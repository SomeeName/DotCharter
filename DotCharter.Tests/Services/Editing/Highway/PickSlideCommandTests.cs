using System.IO.Compression;
using System.Xml.Linq;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class PickSlideCommandTests
{
    [Fact]
    public void ImportAndCommandReuseTheSameNamedTemplateWithoutFingers()
    {
        using var h = new Harness();
        var chord = h.Chord(4, 2);
        var template = h.Arrangement.ChordTemplates[0];
        template.Name = "Shape";
        template.Frets[0] = 12;
        template.Frets[1] = 12;
        var expected = chord.Clone();
        foreach (var sound in expected.ChordNotes) sound.Fret = 12;
        Assert.Same(template, GpPickScrape.FingerlessTemplate(h.Arrangement, expected));
        h.Selection.SelectedChords.Add(chord);

        h.Run();

        Assert.Equal(template.Id, chord.TemplateId);
        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.Equal("Shape", template.Name);
        Assert.Empty(template.Fingers);
    }

    [Fact]
    public void ConvertMatchesTheImportAndLeavesSharedTemplatesAndAnchorsAlone()
    {
        var imported = PickScrapeScenes.Import(PickScrapeScenes.Gpif(
        [
            [new("Whole", [new(0, 21), new(1, 23)])],
            [new("Half", [new(0, 0, GpPickScrape.DownFlag), new(1, 0, GpPickScrape.DownFlag)]), new("Half", [])]
        ]));
        var expected = Assert.Single(imported.Levels[0].Chords, GpPickScrape.IsScrapeChord);
        using var h = new Harness();
        var anchor = FretAnchorLookup.InEffectAt(imported.Levels[0].Anchors, expected.Position)!;
        h.Level.Anchors.Add(new FretAnchor(anchor.Position, anchor.Fret));
        var chord = h.Chord(4, 2);
        var shared = h.Arrangement.ChordTemplates[0];
        shared.Name = "Grip";
        shared.Fingers[0] = 1;
        var beforeFrets = shared.Frets.ToArray();
        var shape = new HandShape { Position = chord.Position, EndPosition = new FractionalPosition(6), TemplateId = shared.Id };
        h.Level.HandShapes.Add(shape);
        h.Selection.SelectedChords.Add(chord);

        h.Run();

        AssertScrape(chord);
        Assert.Equal(expected.ChordNotes.Select(Fields), chord.ChordNotes.Select(Fields));
        Assert.Equal(expected.PassOtherNotes, chord.PassOtherNotes);
        var template = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        var expectedTemplate = imported.ChordTemplates.Single(t => t.Id == expected.TemplateId);
        Assert.Equal(expectedTemplate.Frets, template.Frets);
        Assert.Equal(expectedTemplate.Name, template.Name);
        Assert.Empty(template.Fingers);
        Assert.False(template.Fingerless);
        Assert.Empty(h.Level.HandShapes);
        Assert.Equal(beforeFrets, shared.Frets.ToArray());
        Assert.Equal(1, shared.Fingers[0]);
        Assert.Equal((anchor.Position, anchor.Fret), (h.Level.Anchors[0].Position, h.Level.Anchors[0].Fret));
    }

    [Theory]
    [InlineData(21, 2, 1, 24, 12)]
    [InlineData(21, 1, 2, 24, 18)]
    [InlineData(12, 2, 1, 12, 5)]
    public void PlacementUsesTheLatestUnsortedAnchorAndTheImportDurationRule(int anchor, int numerator,
        int denominator, int start, int landing)
    {
        using var h = new Harness();
        h.Level.Anchors.Add(new FretAnchor(new FractionalPosition(3), anchor));
        h.Level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 1));
        h.Level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 5));
        var note = h.Note(4, 0, 7, new FractionalPosition(new Fraction(numerator, denominator)));
        h.Selection.SelectedNotes.Add(note);

        h.Run();

        Assert.True(GpPickScrape.IsScrape(note));
        Assert.Equal((start, landing), (note.Fret, note.SlideTo!.Value));
        Assert.Single(h.Level.Notes);
        Assert.Empty(h.Level.Chords);
        Assert.Equal(new[] { 3, 0, 8 }, h.Level.Anchors.Select(a => a.Position.BeatId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlacementBeforeAnyAnchorMatchesTheImport(bool hasFutureAnchor)
    {
        using var h = new Harness();
        h.Arrangement.Capo = 2;
        if (hasFutureAnchor) h.Level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 21));
        var note = h.Note(4, 0, hasFutureAnchor ? 7 : 21, new FractionalPosition(2));
        var expected = note.Clone();
        GpPickScrape.Apply(expected, GpPickScrape.DownFlag, h.Arrangement.Capo);
        GpPickScrape.PlaceOnAnchor(h.Arrangement, h.Level, expected, note.Position, new Fraction(2, 1), GpPickScrape.LastFret(null));
        h.Selection.SelectedNotes.Add(note);

        h.Run();

        Assert.Equal(Fields(expected), Fields(note));
        Assert.Equal(expected.PassOtherNotes, note.PassOtherNotes);
    }

    [Fact]
    public void MixedSelectionConvertsOnlyTheOrdinarySounds()
    {
        using var h = new Harness();
        var scrape = h.Note(2, 0, 21, new FractionalPosition(1));
        GpPickScrape.Apply(scrape, GpPickScrape.UpFlag, 0);
        var original = Fields(scrape);
        var chord = h.Chord(4, 2);
        h.Selection.SelectedNotes.Add(scrape);
        h.Selection.SelectedChords.Add(chord);

        h.Run();

        Assert.Equal(original, Fields(scrape));
        AssertScrape(chord);
    }

    [Fact]
    public void FlipTwiceReturnsTheConvertedSoundAndReusesItsTemplate()
    {
        using var h = new Harness();
        h.Level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 21));
        var chord = h.Chord(4, 2);
        var note = h.Note(8, 2, 21, new FractionalPosition(new Fraction(1, 2)));
        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedNotes.Add(note);
        h.Run();
        var first = chord.ChordNotes.Select(Fields).ToArray();
        var firstNote = Fields(note);
        var templateId = chord.TemplateId;

        h.Run();

        Assert.All(chord.ChordNotes, n => Assert.Equal((12, 24), (n.Fret, n.SlideTo!.Value)));
        Assert.Equal((12, 18), (note.Fret, note.SlideTo!.Value));
        h.Run();
        Assert.Equal(first, chord.ChordNotes.Select(Fields));
        Assert.Equal(firstNote, Fields(note));
        Assert.Equal(templateId, chord.TemplateId);
    }

    [Fact]
    public void EmptySelectionPlacesOneSelectedSplitChordAtThePenPosition()
    {
        using var h = new Harness();
        h.PenPosition = new FractionalPosition(4, new Fraction(1, 2));
        h.State.CurrentTimeMs = 0;

        h.Run();

        var chord = Assert.Single(h.Level.Chords);
        Assert.Equal(h.PenPosition, chord.Position);
        Assert.Equal(new[] { 0, 1 }, chord.ChordNotes.Select(n => n.StringIndex));
        Assert.All(chord.ChordNotes, n => Assert.Equal(new FractionalPosition(1), n.Sustain));
        AssertScrape(chord);
        Assert.Same(chord, Assert.Single(h.Selection.SelectedChords));
        Assert.Empty(h.Level.Notes);
    }

    [Fact]
    public void ShapeChangeHonoursTheMaskWithoutAskingTheTechniqueLadder()
    {
        using var h = new Harness();
        var chord = h.Chord(4, 2);
        var excluded = h.Note(8, 3, 7, new FractionalPosition(1));
        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedNotes.Add(excluded);
        h.State.StringEditMask = StringEditMask.Empty.Toggle(0);
        h.Service.TechniqueReach = new ForbiddenReach();

        h.Run();

        AssertScrape(chord);
        Assert.False(GpPickScrape.IsScrape(excluded));
        Assert.Equal(7, excluded.Fret);
    }

    [Fact]
    public void FocusedLevelSuppliesTheAnchorAndOwnsTheNewTemplate()
    {
        using var h = new Harness();
        var focused = new Arrangement { Capo = 2 };
        var level = new ChartLevel();
        focused.Levels.Add(level);
        h.State.ActiveProject!.Arrangements.Add(focused);
        h.Scope.SetResolver(() => new EditingTarget(focused, level));
        level.Anchors.Add(new FretAnchor(FractionalPosition.Zero, 21));
        h.Run();

        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, n => Assert.Equal((24, 12), (n.Fret, n.SlideTo!.Value)));
        Assert.Single(focused.ChordTemplates);
        Assert.Empty(h.Level.Chords);
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    [Theory]
    [InlineData("convert")]
    [InlineData("flip")]
    [InlineData("place")]
    public void UndoRedoRestoresTheWholeGestureIncludingHandShapeAndSelection(string operation)
    {
        using var h = new Harness();
        if (operation != "place")
        {
            var chord = h.Chord(4, 2);
            var note = h.Note(8, 2, 7, new FractionalPosition(1));
            h.Selection.SelectedChords.Add(chord);
            h.Selection.SelectedNotes.Add(note);
            if (operation == "flip") h.Run();
            var shape = new HandShape { Position = chord.Position, EndPosition = new FractionalPosition(6), TemplateId = chord.TemplateId };
            h.Level.HandShapes.Add(shape);
            h.Selection.SelectedHandShapes.Add(shape);
        }

        h.History.Clear();
        var before = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        var selectedNotes = h.Selection.SelectedNotes.ToArray();
        var selectedChords = h.Selection.SelectedChords.ToArray();
        var selectedShapes = h.Selection.SelectedHandShapes.ToArray();
        h.Run();
        var after = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        var afterChords = h.Selection.SelectedChords.ToArray();
        Assert.NotEqual(before, after);
        if (operation == "flip") Assert.Equal(selectedShapes, h.Level.HandShapes);
        else Assert.Empty(h.Level.HandShapes);

        h.History.Undo();

        Assert.False(h.History.CanUndo);
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        Assert.Equal(selectedNotes, h.Selection.SelectedNotes);
        Assert.Equal(selectedChords, h.Selection.SelectedChords);
        Assert.Equal(selectedShapes, h.Selection.SelectedHandShapes);
        h.History.Redo();
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        Assert.Equal(afterChords, h.Selection.SelectedChords);
        if (operation == "flip") Assert.Equal(selectedShapes, h.Selection.SelectedHandShapes);
        else Assert.Empty(h.Selection.SelectedHandShapes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroSustainGetsOneBeatThenUsesImportCompatibleSpacing(bool chordTarget)
    {
        using var h = new Harness();
        IGuitarSound sound;
        if (chordTarget)
        {
            var chord = h.Chord(4, 0);
            h.Selection.SelectedChords.Add(chord);
            sound = chord.ChordNotes[0];
        }
        else
        {
            var note = h.Note(4, 0, 7, FractionalPosition.Zero);
            h.Selection.SelectedNotes.Add(note);
            sound = note;
        }

        var next = h.Note(4, 0, 5, FractionalPosition.Zero);
        next.Position += new FractionalPosition(new Fraction(1, 2));
        var unrelated = h.Note(4, 5, 5, FractionalPosition.Zero);
        unrelated.Position += new FractionalPosition(new Fraction(1, 4));

        h.Run();

        Assert.True(sound.Sustain > FractionalPosition.Zero);
        Assert.True(sound.Sustain < new FractionalPosition(new Fraction(1, 4)));
        Assert.True(sound.Sustain < new FractionalPosition(new Fraction(1, 2)));
        Assert.True(GpPickScrape.IsScrape(sound));
        if (chordTarget)
        {
            Assert.Equal(sound.Sustain, h.Level.Chords[0].ChordNotes[1].Sustain);
            Assert.False(h.Level.Chords[0].PassOtherNotes);
        }
        else
        {
            Assert.False(h.Level.Notes[0].PassOtherNotes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImpliedTargetsArePromotedAndUndoRestoresTheirSelection(bool handShapeTarget)
    {
        using var h = new Harness();
        var chord = h.Chord(4, 2);
        h.PenPosition = chord.Position;
        HandShape? shape = null;
        if (handShapeTarget)
        {
            shape = new HandShape { Position = chord.Position, EndPosition = new FractionalPosition(6), TemplateId = chord.TemplateId };
            h.Level.HandShapes.Add(shape);
            h.Selection.SelectedHandShapes.Add(shape);
            h.PenPosition = new FractionalPosition(12);
        }

        h.Run();

        AssertScrape(chord);
        Assert.Same(chord, Assert.Single(h.Selection.SelectedChords));
        Assert.Single(h.Level.Chords);
        h.History.Undo();
        Assert.Empty(h.Selection.SelectedChords);
        if (shape != null) Assert.Same(shape, Assert.Single(h.Selection.SelectedHandShapes));
    }

    [Fact]
    public void ExportAndProjectReopenKeepTheChartedPickSlide()
    {
        using var h = new Harness();
        var chord = h.Chord(4, 2);
        h.Selection.SelectedChords.Add(chord);
        h.Run();
        var xml = new RsXmlSongWriter().Write(h.State.ActiveProject!);
        Assert.Empty(xml.Descendants("chord"));
        Assert.Empty(xml.Descendants("handShape"));
        var notes = xml.Descendants("note").ToArray();
        Assert.Equal(2, notes.Length);
        Assert.All(notes, n =>
        {
            foreach (var key in new[] { "tap", "tremolo", "ignore" }) Assert.Equal("1", n.Attribute(key)?.Value);
            Assert.Null(n.Attribute("mute"));
            Assert.Equal("5", n.Attribute("slideUnpitchTo")?.Value);
        });
        var bytes = GpExporter.ExportToBytes(h.Arrangement, h.Level, h.State.ActiveProject!.Beats, "Scrape", "DotCharter");
        using var archive = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(archive.Entries.Single(e => e.FullName.EndsWith("score.gpif", StringComparison.Ordinal)).Open());
        var gpif = XDocument.Parse(reader.ReadToEnd());
        Assert.Equal(new[] { "64", "64" }, gpif.Descendants("Property").Where(p => (string?)p.Attribute("name") == "Slide").Select(p => p.Element("Flags")!.Value));
        Assert.Equal(2, gpif.Descendants("Property").Count(p => (string?)p.Attribute("name") == "Muted"));
        Assert.Empty(gpif.Descendants("Tremolo"));
        var path = Path.Combine(Path.GetTempPath(), $"pick-slide-{Guid.NewGuid():N}.dcproj");
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, h.State.ActiveProject!);
            var reopened = serializer.Load(path);
            var actual = Assert.Single(Assert.Single(reopened.Arrangements).Levels).Chords.Single();
            AssertScrape(actual);
            Assert.Equal(chord.TemplateId, actual.TemplateId);
            Assert.Equal(chord.ChordNotes.Select(Fields), actual.ChordNotes.Select(Fields));
            Assert.Equal(FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!), FocusedHighwayEditingTests.ChartBytes(reopened));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static object Fields(IGuitarSound n)
        => (n.StringIndex, n.Fret, n.SlideTo, n.Hopo, n.Tremolo, n.Mute, n.UnpitchedSlide, n.LinkNext, n.Sustain, GpPickScrape.IsScrape(n), n is GuitarNote note && note.PassOtherNotes);

    private static void AssertScrape(Chord chord)
    {
        Assert.True(chord.SplitIntoNotes);
        Assert.True(chord.Ignore);
        Assert.All(chord.ChordNotes, n =>
        {
            Assert.True(GpPickScrape.IsScrape(n));
            Assert.Equal(MuteType.None, n.Mute);
        });
    }

    internal sealed class Harness : IDisposable
    {
        public Harness()
        {
            var project = new ChartProject();
            for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            Arrangement.Levels.Add(Level);
            project.Arrangements.Add(Arrangement);
            State.ActiveProject = project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Level;
            var config = new FakeConfigurationService();
            config.AppConfig.UseFastChartingInput = true;
            Scope = new EditingScope(State);
            var targets = new EditTargetService(Selection, Scope, config);
            targets.SetPenPositionProvider(() => PenPosition);
            Service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(), config, Scope, targets, new FakeAppLogger());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; } = new(new FakeAppLogger());
        public Arrangement Arrangement { get; } = new();
        public ChartLevel Level { get; } = new();
        public HighwayEditingService Service { get; }
        public EditingScope Scope { get; }
        public FractionalPosition PenPosition { get; set; } = new(4);

        public GuitarNote Note(int beat, int str, int fret, FractionalPosition sustain)
        {
            var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = str, Fret = fret, Sustain = sustain };
            Level.Notes.Add(note);
            return note;
        }

        public Chord Chord(int beat, int sustain)
        {
            var template = new ChordTemplate { Id = Arrangement.ChordTemplates.Count };
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = template.Id };
            for (var i = 0; i < 2; i++)
            {
                template.Frets[i] = 7 + i;
                chord.ChordNotes.Add(new ChordNote { StringIndex = i, Fret = 7 + i, Sustain = new FractionalPosition(sustain) });
            }

            Arrangement.ChordTemplates.Add(template);
            Level.Chords.Add(chord);
            return chord;
        }

        public void Run() => Service.PickSlide();

        public void Dispose() => Service.Dispose();
    }

    private sealed class ForbiddenReach : IChordTechniqueReach
    {
        public IReadOnlyList<Chord>? ChordsFor(IReadOnlyList<Chord> selectedChords)
            => throw new InvalidOperationException("A shape command must stay on the resolved target.");

        public void Reached(IReadOnlyList<Chord> chords)
            => throw new InvalidOperationException("A shape command must stay on the resolved target.");
    }
}
