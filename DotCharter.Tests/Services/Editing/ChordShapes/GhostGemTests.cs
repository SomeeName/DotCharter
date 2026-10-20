using System.Xml.Linq;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Services.Editing;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     #785, ghost gems: a fret the hand HOLDS but does not play lives in the hand shape's
///     grip (<see cref="ChordTemplate.GhostMask" />), never as a note — so nothing plays,
///     scores, detects or exports it as one, and the Rocksmith export writes it only into the
///     template. These pin the toggle (and its undo), the tapped-harmonic shortcut, the live
///     detection timeline, the RS-XML and .rscp round trips, the clone, and the import reading.
/// </summary>
public sealed class GhostGemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-ghost-" + Guid.NewGuid().ToString("N"));

    public GhostGemTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static List<BeatMarker> Beats(int count = 16) =>
    [
        .. Enumerable.Range(0, count).Select(i => new BeatMarker
        {
            Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4
        })
    ];

    private static Arrangement Guitar(int capo = 0)
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6, Capo = capo,
            Tuning = ["E", "A", "D", "G", "B", "E"]
        };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    /// <summary>A G5 + octave (3-5-5) on its own template at beat 2, one beat long.</summary>
    private static (Arrangement Arrangement, Chord Chord, ChordTemplate Template) PowerChordWithOctave()
    {
        var arrangement = Guitar();
        var template = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 }, Fingers = { [0] = 1, [1] = 3, [2] = 4 } };
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        foreach (var (s, f) in template.Frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f, Sustain = new FractionalPosition(1) });
        arrangement.Levels[0].Chords.Add(chord);
        return (arrangement, chord, template);
    }

    [Fact]
    public void GhostingAChordsString_TakesItOutOfPlay_AndPutsItInTheHandShapesGrip_AndUndoPutsItBack()
    {
        var (arrangement, chord, template) = PowerChordWithOctave();
        var level = arrangement.Levels[0];

        var plan = GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2);
        Assert.Equal(1, plan.Gems);
        plan.Action!.Execute();

        // The chord plays two strings, on a template of exactly those.
        Assert.Equal([0, 1], chord.ChordNotes.Select(n => n.StringIndex).Order());
        var played = arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [0] = 3, [1] = 5 }, played.Frets);
        Assert.Equal(0, played.GhostMask);

        // A hand shape over it HOLDS the whole grip, string 2 a ghost, pinned, fingered.
        var shape = Assert.Single(level.HandShapes);
        Assert.True(shape.Covers(chord.Position));
        var grip = arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(template.Frets, grip.Frets);
        Assert.True(grip.IsGhost(2));
        Assert.False(grip.IsGhost(0));
        Assert.True(grip.IsPinned);
        Assert.Equal(4, grip.Fingers[2]);
        Assert.Equal(0, template.GhostMask); // the shared original is never touched

        plan.Action.Undo();
        Assert.Equal([0, 1, 2], chord.ChordNotes.Select(n => n.StringIndex).Order());
        Assert.Equal(0, chord.TemplateId);
        Assert.Empty(level.HandShapes);
        Assert.Single(arrangement.ChordTemplates);
    }

    [Fact]
    public void GhostingAllButOneString_LeavesASingleNote_AndASingleNoteGhostsWhole()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        GhostGems.Ghost(arrangement, level, [], [chord], s => s != 0).Action!.Execute();

        Assert.Empty(level.Chords);
        var note = Assert.Single(level.Notes);
        Assert.Equal((0, 3), (note.StringIndex, note.Fret));
        var grip = arrangement.ChordTemplates.Single(t => t.Id == Assert.Single(level.HandShapes).TemplateId);
        Assert.Equal(ChordTemplate.GhostBit(1) | ChordTemplate.GhostBit(2), grip.GhostMask);

        // The last note too: nothing is played, the bar holds all three.
        GhostGems.Ghost(arrangement, level, [note], [], _ => true).Action!.Execute();
        Assert.Empty(level.Notes);
        grip = arrangement.ChordTemplates.Single(t => t.Id == Assert.Single(level.HandShapes).TemplateId);
        Assert.Equal(7, grip.GhostMask);
    }

    [Fact]
    public void AGhostGem_IsNeverAnExpectedSound_ForLiveDetectionOrScoring()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        var beats = Beats();
        var expected = new List<ExpectedSound>();
        var timeline = new LiveExpectedTimeline();

        timeline.Build(expected, arrangement, level, beats, 0);
        var before = Assert.Single(expected).MidiNotes.Length;

        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        timeline.Build(expected, arrangement, level, beats, 0);

        Assert.Equal(3, before);
        // 40 (low E) + 3 and 45 (A) + 5: the ghost D-string 5 (55) is held, not heard.
        Assert.Equal([43, 50], Assert.Single(expected).MidiNotes.Order());
    }

    [Fact]
    public void TheTappedHarmonicShortcut_TapsTheHarmonic_AndHoldsTheFretTwelveBelowAsAGhost()
    {
        var arrangement = Guitar();
        var level = arrangement.Levels[0];
        var note = new GuitarNote { Position = new FractionalPosition(4), StringIndex = 2, Fret = 17, Sustain = new FractionalPosition(2) };
        level.Notes.Add(note);

        var plan = GhostGems.TappedHarmonic(arrangement, level, [note], GhostGems.DefaultTappedHarmonicOffset);
        plan.Action!.Execute();

        Assert.Equal(HarmonicType.Normal, note.Harmonic);
        Assert.Equal(HopoType.Tap, note.Hopo);
        Assert.Equal(17, note.Fret); // the tapped note itself is what is played
        var shape = Assert.Single(level.HandShapes);
        Assert.True(shape.Covers(note.Position));
        var grip = arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [2] = 5 }, grip.Frets);
        Assert.True(grip.IsGhost(2));
        Assert.Equal(1, grip.Fingers[2]);
        Assert.True(grip.IsPinned);

        plan.Action.Undo();
        Assert.Equal((HarmonicType.None, HopoType.None), (note.Harmonic, note.Hopo));
        Assert.Empty(level.HandShapes);
    }

    [Fact]
    public void ASoundOnAGhostString_IsNotAContradictionOfTheHeldFret()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        // Under the bar, string 2 is played at 7 (a tap over the held 5).
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2, new Fraction(1, 2)), StringIndex = 2, Fret = 7 });
        var grip = arrangement.ChordTemplates.Single(t => t.GhostMask != 0);

        Assert.Empty(ContentFindings(arrangement));

        // The same bar without the ghost mark: the rule reports it (the guard is not vacuous).
        grip.GhostMask = 0;
        Assert.Single(ContentFindings(arrangement));
    }

    private static List<ArrangementIssue> ContentFindings(Arrangement arrangement)
    {
        var issues = new List<ArrangementIssue>();
        new HandShapeContentRule().Validate(ArrangementValidationContext.Build(arrangement, Beats(), null), issues);
        return issues;
    }

    [Theory]
    [InlineData(17, 12, 0, 5)]
    [InlineData(7, 12, 0, 0)] // never below the nut
    [InlineData(19, 12, 0, 7)]
    [InlineData(14, 12, 3, 3)] // a held 2 behind a capo at 3 is the capo
    [InlineData(17, 5, 0, 12)] // the offset is the setting's
    public void TheHeldFret_IsTheOffsetBelow_ClampedToTheNeck(int tapped, int offset, int capo, int held) =>
        Assert.Equal(held, GhostGems.HeldFret(tapped, offset, capo));

    [Fact]
    public void ToggleBackOnTheHandShape_ReturnsItsGhostsToTheGrip_AndSetGhostFlipsOneString()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        var shape = Assert.Single(level.HandShapes);

        GhostGems.Unghost(arrangement, level, [shape]).Action!.Execute();
        var grip = arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(0, grip.GhostMask);
        Assert.True(grip.IsPinned); // still the charter's grip

        GhostGems.SetGhost(arrangement, level, shape, 0, true).Action!.Execute();
        grip = arrangement.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.True(grip.IsGhost(0));
        Assert.True(GhostGems.SetGhost(arrangement, level, shape, 0, true).IsEmpty);
        Assert.True(GhostGems.SetGhost(arrangement, level, shape, 4, true).IsEmpty); // no fret there
    }

    [Fact]
    public void RsXml_WritesTheGhostOnlyIntoTheHandShapesTemplate_AndReadsItBackAsAGhost()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        var document = new RsXmlSongWriter().Write(project, null);

        var root = document.Root!;
        var templates = root.Element("chordTemplates")!.Elements("chordTemplate").ToList();
        var xmlLevel = root.Element("levels")!.Element("level")!;
        var handShapeChordId = int.Parse(xmlLevel.Element("handShapes")!.Element("handShape")!.Attribute("chordId")!.Value);
        Assert.Equal("5", templates[handShapeChordId].Attribute("fret2")!.Value);
        var xmlChord = xmlLevel.Element("chords")!.Element("chord")!;
        Assert.Null(templates[int.Parse(xmlChord.Attribute("chordId")!.Value)].Attribute("fret2"));
        Assert.DoesNotContain(xmlChord.Elements("chordNote"), n => n.Attribute("string")?.Value == "2");
        Assert.DoesNotContain(xmlLevel.Descendants("note"), n => n.Attribute("string")?.Value == "2");

        var back = new RsXmlSongReader().ReadDocument(document, "ghost.xml").Arrangements[0];
        var backShape = Assert.Single(back.Levels[0].HandShapes);
        var grip = back.ChordTemplates.Single(t => t.Id == backShape.TemplateId);
        Assert.Equal(ChordTemplate.GhostBit(2), grip.GhostMask);
        Assert.Equal(new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 }, grip.Frets);
        Assert.Equal([0, 1], Assert.Single(back.Levels[0].Chords).ChordNotes.Select(n => n.StringIndex).Order());
    }

    [Fact]
    public void Eof_WritesTheGhostAsAGhostGemOfTheOpeningChord_AndReadsItBackIntoTheHandShape()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        GhostGems.Ghost(arrangement, level, [], [chord], s => s == 2).Action!.Execute();
        var project = new ChartProject { AudioFileName = "song.wav" };
        foreach (var beat in Beats()) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        var path = Path.Combine(_dir, "p.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        var importer = new EofImporter();
        var back = importer.ImportTracks(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList()).Single();

        // The strum still plays two strings...
        Assert.Equal([0, 1], Assert.Single(back.Levels[0].Chords).ChordNotes.Select(n => n.StringIndex).Order());
        // ...and the hand shape holds the octave as a ghost again.
        var shape = Assert.Single(back.Levels[0].HandShapes);
        var grip = back.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 }, grip.Frets);
        Assert.Equal(ChordTemplate.GhostBit(2), grip.GhostMask);
        Assert.Equal(4, grip.Fingers[2]);
    }

    [Fact]
    public void Import_NeverReadsAGhostIntoATemplateAChordPlays_NorUnderAShapeThatCoversNothing()
    {
        var (arrangement, chord, template) = PowerChordWithOctave();
        var level = arrangement.Levels[0];
        // A chord plays template 0 with only two of its strings, and a bar names it too.
        chord.ChordNotes.RemoveAt(2);
        level.HandShapes.Add(new HandShape { Position = chord.Position, EndPosition = new FractionalPosition(4), TemplateId = 0 });
        // A bar over nothing names a template no chord plays.
        var lonely = new ChordTemplate { Id = 1, Frets = { [3] = 7 } };
        arrangement.ChordTemplates.Add(lonely);
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), TemplateId = 1 });

        Assert.Equal(0, GhostGems.MarkUnplayedHandShapeFrets(arrangement));
        Assert.Equal(0, template.GhostMask);
        Assert.Equal(0, lonely.GhostMask);
    }

    [Fact]
    public void Rscp_KeepsTheGhostsAndThePin_AndAnOlderProjectReadsAsNeither()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        GhostGems.Ghost(arrangement, arrangement.Levels[0], [], [chord], s => s == 2).Action!.Execute();
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        var path = Path.Combine(_dir, "p.rscp");

        new RscpChartSerializer().Save(path, project);

        var written = XDocument.Load(path).Descendants("chordTemplate").ToList();
        Assert.Contains(written, t => t.Attribute("ghost")?.Value == "2" && t.Attribute("pinned")?.Value == "T");
        Assert.Equal(written.Count - 1, written.Count(t => t.Attribute("ghost") == null));
        var reloaded = new RscpChartSerializer().Load(path).Arrangements[0];
        var grip = reloaded.ChordTemplates.Single(t => t.Id == reloaded.Levels[0].HandShapes[0].TemplateId);
        Assert.Equal(ChordTemplate.GhostBit(2), grip.GhostMask);
        Assert.True(grip.Pinned);

        // A project saved before #785 has neither attribute.
        var doc = XDocument.Load(path);
        doc.Descendants("chordTemplate").Attributes("ghost").Remove();
        doc.Descendants("chordTemplate").Attributes("pinned").Remove();
        doc.Save(path);
        Assert.All(new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates,
            t => Assert.False(t.IsPinned));
    }

    [Fact]
    public void TheProjectClone_CarriesTheGhostsAndThePin()
    {
        var (arrangement, chord, _) = PowerChordWithOctave();
        GhostGems.Ghost(arrangement, arrangement.Levels[0], [], [chord], s => s == 2).Action!.Execute();
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);

        var clone = ChartProjectCloner.Clone(project).Arrangements[0];

        var grip = clone.ChordTemplates.Single(t => t.Id == clone.Levels[0].HandShapes[0].TemplateId);
        Assert.Equal(ChordTemplate.GhostBit(2), grip.GhostMask);
        Assert.True(grip.Pinned);
        Assert.NotSame(arrangement.ChordTemplates.Single(t => t.Id == grip.Id), grip);
    }

    [Fact]
    public void AGhostGrip_IsNeverAFindOrCreateMatch_ForAChordThatPlaysAllItsFrets()
    {
        var (arrangement, chord, template) = PowerChordWithOctave();
        GhostGems.Ghost(arrangement, arrangement.Levels[0], [], [chord], s => s == 2).Action!.Execute();
        var grip = arrangement.ChordTemplates.Single(t => t.GhostMask != 0);

        Assert.False(grip.HasFrets(template.Frets));
        Assert.Same(template, ChordTemplateResolver.FindByShape(arrangement, template.Frets, ArpeggioFlavour.Plain));
        Assert.False(grip.DescribesChordNotes(chord.ChordNotes));
    }
}
