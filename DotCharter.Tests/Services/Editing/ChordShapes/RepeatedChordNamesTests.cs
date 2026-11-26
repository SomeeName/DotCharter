using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     "Name only the first chord of a hand shape": a later chord that repeats the same named grip inside one
///     hand shape plays an unnamed twin, on Guitar Pro import and Generate hand shapes; a chord that already
///     played a template is never repointed, and the exporters write the twin's name empty.
/// </summary>
public sealed class RepeatedChordNamesTests
{
    private static readonly (int String, int Fret)[] AMinor = [(1, 0), (2, 2), (3, 2), (4, 1), (5, 0)];

    [Fact]
    public void Plan_RepointsEveryRepeatAfterTheFirst_ToOneUnnamedTwin()
    {
        var grip = Grip(3, "Am");
        var chords = Enumerable.Range(0, 3).Select(i => Strum(i, -1)).ToList();

        var plan = RepeatedChordNames.Plan([grip], [chords.Select(c => new ShapeChord(c, grip, true)).ToList()], 7);

        var twin = Assert.Single(plan.Twins);
        Assert.Equal(7, twin.Id);
        Assert.Equal(string.Empty, twin.Name);
        Assert.True(twin.Unnamed);
        Assert.True(twin.HasSameShapeAs(grip));
        Assert.Equal(grip.Fingers, twin.Fingers);
        Assert.Equal([(chords[1], 7), (chords[2], 7)], plan.Repoints);
        Assert.Equal("Am", grip.Name);
    }

    [Fact]
    public void Plan_ReusesAnUnnamedTwinTheArrangementHolds()
    {
        var grip = Grip(0, "Am");
        var existing = grip.Clone();
        existing.Id = 4;
        existing.Name = string.Empty;
        var chords = new[] { Strum(0, -1), Strum(1, -1) };

        var plan = RepeatedChordNames.Plan([grip, existing], [chords.Select(c => new ShapeChord(c, grip, true)).ToList()], 9);

        Assert.Empty(plan.Twins);
        Assert.Equal([(chords[1], 4)], plan.Repoints);
    }

    [Fact]
    public void Plan_NeverRepointsAChordItDidNotAssign_NorAnUnnamedGrip_NorAcrossShapes()
    {
        var named = Grip(0, "Am");
        var unnamed = Grip(1, string.Empty);
        var kept = Strum(1, 0);
        var shapes = new List<IReadOnlyList<ShapeChord>>
        {
            new List<ShapeChord> { new(Strum(0, -1), named, true), new(kept, named, false) },
            new List<ShapeChord> { new(Strum(2, -1), unnamed, true), new(Strum(3, -1), unnamed, true) },
            new List<ShapeChord> { new(Strum(4, -1), named, true) }
        };

        Assert.True(RepeatedChordNames.Plan([named, unnamed], shapes, 5).IsEmpty);
    }

    [Fact]
    public void GuitarProImport_NamesOnlyTheFirstChordOfAHandShape()
    {
        var arrangement = Standard();
        var level = new ChartLevel();
        for (var beat = 0; beat < 4; beat++) level.Chords.Add(Strum(beat, -1));

        GpPostProcessor.Run(level, arrangement);

        var shape = Assert.Single(level.HandShapes);
        var names = level.Chords.Select(c => arrangement.ChordTemplates.Single(t => t.Id == c.TemplateId).Name).ToList();
        Assert.Equal([names[0], "", "", ""], names);
        Assert.Equal(level.Chords[0].TemplateId, shape.TemplateId);
        Assert.Equal(2, arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void GenerateHandShapes_NamesOnlyTheFirstChord_AndUndoRestoresEverything()
    {
        var arrangement = Standard();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        arrangement.ChordTemplates.Add(Grip(0, "Am"));
        for (var beat = 0; beat < 3; beat++) level.Chords.Add(Strum(beat, -1));

        var actions = ArrangementHandShapeGenerator.BuildActions(arrangement, ArrangementHandShapeGenerator.Plan(arrangement));
        foreach (var action in actions) action.Execute();

        var names = level.Chords.Select(c => arrangement.ChordTemplates.Single(t => t.Id == c.TemplateId).Name).ToList();
        Assert.Equal(["Am", "", ""], names);

        foreach (var action in Enumerable.Reverse(actions)) action.Undo();
        Assert.Single(arrangement.ChordTemplates);
        Assert.All(level.Chords, c => Assert.Equal(-1, c.TemplateId));
    }

    [Fact]
    public void TheSettingOff_KeepsTheNameOnEveryChord()
    {
        try
        {
            RepeatedChordNames.Configure(static () => false);
            var arrangement = Standard();
            var level = new ChartLevel();
            for (var beat = 0; beat < 3; beat++) level.Chords.Add(Strum(beat, -1));

            GpPostProcessor.Run(level, arrangement);

            Assert.Single(arrangement.ChordTemplates);
        }
        finally
        {
            RepeatedChordNames.Configure(static () => true);
        }
    }

    [Fact]
    public void RsXml_WritesTheTwinWithAnEmptyName()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        var arrangement = Standard();
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        for (var beat = 0; beat < 3; beat++) level.Chords.Add(Strum(beat, -1));
        GpPostProcessor.Run(level, arrangement);

        var song = new RsXmlSongWriter().Write(project).Root!;
        var templates = song.Element("chordTemplates")!.Elements("chordTemplate").ToList();

        Assert.Equal([arrangement.ChordTemplates[0].Name, ""], templates.Select(t => (string)t.Attribute("chordName")!));
        Assert.NotEmpty(arrangement.ChordTemplates[0].Name);
        Assert.Equal("", (string)templates[1].Attribute("displayName")!);
    }

    private static Arrangement Standard()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        return arrangement;
    }

    private static ChordTemplate Grip(int id, string name)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in AMinor) template.Frets[stringIndex] = fret;
        template.Fingers[2] = 2;
        template.Fingers[3] = 3;
        template.Fingers[4] = 1;
        return template;
    }

    private static Chord Strum(int beat, int templateId)
    {
        var chord = new Chord
        {
            Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1), TemplateId = templateId
        };
        foreach (var (stringIndex, fret) in AMinor)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret, Sustain = new FractionalPosition(1) });
        return chord;
    }
}
