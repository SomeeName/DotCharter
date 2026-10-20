using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="NoteFingerPickStrumTests" /> that need no package reader; its fixtures are in <c>NoteFingerPickStrumTests.Fixtures.cs</c>.</summary>
public sealed partial class NoteFingerPickStrumTests
{
    [Fact]
    public void RsXml_RoundTripsFingerPickDirectionAndStrum()
    {
        var document = new RsXmlSongWriter().Write(Fingered(), null);

        AssertFingered(new RsXmlSongReader().ReadDocument(document, "x.xml").Arrangements[0].Levels[0]);
    }

    [Fact]
    public void RsXml_SpellsThemTheRocksmithWay()
    {
        var level = new RsXmlSongWriter().Write(Fingered(), null).Root!.Element("levels")!.Element("level")!;
        var notes = level.Element("notes")!.Elements("note").ToList();
        var chords = level.Element("chords")!.Elements("chord").ToList();

        Assert.Equal("0", notes[0].Attribute("leftHand")!.Value);
        Assert.Equal("1", notes[0].Attribute("pickDirection")!.Value);
        Assert.Equal("0", notes[1].Attribute("pickDirection")!.Value);
        Assert.Null(notes[2].Attribute("leftHand"));
        Assert.Null(notes[2].Attribute("pickDirection"));
        Assert.Equal("up", chords[0].Attribute("strum")!.Value);
        Assert.Equal("down", chords[1].Attribute("strum")!.Value);
        Assert.Null(chords[2].Attribute("strum"));
    }

    [Fact]
    public void Rscp_RoundTripsFingerPickDirectionAndStrum()
    {
        var path = Path.Combine(_dir, "f.rscp");
        new RscpChartSerializer().Save(path, Fingered());

        AssertFingered(new RscpChartSerializer().Load(path).Arrangements[0].Levels[0]);
    }

    [Fact]
    public void Sng_WritesFingerPickDirectionAndStrum()
    {
        var project = Fingered();
        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 8000).Sng;
        var all = sng.Levels[0].Notes.OrderBy(n => n.Time).ToList();
        var notes = all.Where(n => (n.Mask & RsNoteMask.Chord) == 0).ToList();
        var chords = all.Where(n => (n.Mask & RsNoteMask.Chord) != 0).ToList();

        Assert.Equal(new sbyte[] { 0, 3, -1 }, notes.Select(n => n.LeftHand).ToArray());
        Assert.Equal(new sbyte[] { 1, 0, 0 }, notes.Select(n => n.PickDirection).ToArray());
        Assert.Equal(new sbyte[] { 1, 0, -1 }, chords.Select(n => n.PickDirection).ToArray());
    }

    [Fact]
    public void AChartThatStatesNone_ExportsExactlyAsBefore()
    {
        var project = Project(level =>
        {
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 5 });
            level.Chords.Add(Strum(PickDirectionType.None, 4));
        });

        var level = new RsXmlSongWriter().Write(project, null).Root!.Element("levels")!.Element("level")!;
        var note = level.Element("notes")!.Element("note")!;
        Assert.Null(note.Attribute("leftHand"));
        Assert.Null(note.Attribute("pickDirection"));
        Assert.Null(level.Element("chords")!.Element("chord")!.Attribute("strum"));

        var sng = SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 8000).Sng;
        var all = sng.Levels[0].Notes.OrderBy(n => n.Time).ToList();
        Assert.Equal((sbyte)-1, all[0].LeftHand);
        Assert.Equal((sbyte)0, all[0].PickDirection);
        Assert.Equal((sbyte)-1, all[1].PickDirection);
        Assert.Equal(0u, all[0].Mask & RsNoteMask.LeftHand);
    }

    [Fact]
    public void TheClonerCopiesAllThree()
    {
        var clone = ChartProjectCloner.Clone(Fingered());

        AssertFingered(clone.Arrangements[0].Levels[0]);
    }

    [Fact]
    public void CopyFrom_CopiesAllThree()
    {
        var note = new GuitarNote { LeftHandFinger = 2, PickDirection = PickDirectionType.Up };
        var chord = new Chord { Strum = PickDirectionType.Down };

        var noteCopy = note.Clone();
        var chordCopy = chord.Clone();

        Assert.Equal(2, noteCopy.LeftHandFinger);
        Assert.Equal(PickDirectionType.Up, noteCopy.PickDirection);
        Assert.Equal(PickDirectionType.Down, chordCopy.Strum);
    }

    [Fact]
    public void TheActionComparerSeesEachField()
    {
        var a = new GuitarNote();
        Assert.False(ChartActionStateComparer.GuitarNotesEqual(a, new GuitarNote { LeftHandFinger = 1 }));
        Assert.False(ChartActionStateComparer.GuitarNotesEqual(a, new GuitarNote { PickDirection = PickDirectionType.Up }));
        Assert.False(ChartActionStateComparer.ChordsEqual(new Chord(), new Chord { Strum = PickDirectionType.Up }));
    }
}
