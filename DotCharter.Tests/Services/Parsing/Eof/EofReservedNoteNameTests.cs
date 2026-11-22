using System.Collections;
using System.Reflection;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofReservedNoteNameTests
{
    private const string Marker = "[DotCharter grace]";

    [Fact]
    public void Imported_note_identity_never_becomes_chart_data()
    {
        Assert.Multiple(
            () => AssertImported("folded pair"),
            () => AssertImported("unfolded single"),
            () => AssertImported("edited chord"),
            () => AssertImported("ghost template"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_reserved_chart_template_name_is_empty_in_the_final_wire_record(bool nop)
    {
        var project = Project();
        var arrangement = project.Arrangements[0];
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = Marker, NopInRs = nop, Frets = { [0] = 5, [1] = 7 }
        });
        arrangement.Levels[0].Chords.Add(new Chord
        {
            TemplateId = 0, Position = new FractionalPosition(1),
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 5 }, new ChordNote { StringIndex = 1, Fret = 7 } }
        });
        var built = Assert.Single(EofNoteBuilder.Build(arrangement, project.Beats));
        var writer = new EofPackWriter();
        EofProGuitarNoteWriter.Write(writer, built.Raw);
        var raw = EofProGuitarNoteReader.Read(new EofPackReader(writer.ToArray()));
        Assert.Empty(raw.Name);
        Assert.Equal(Marker, arrangement.ChordTemplates[0].Name);
        Assert.Equal((byte)3, raw.NoteBitmask);
    }

    private static void AssertImported(string fixture)
    {
        var source = Project();
        source.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), Sustain = new FractionalPosition(2),
            StringIndex = 0, Fret = 7, GraceFret = 5
        });
        var notes = EofNoteBuilder.Build(source.Arrangements[0], source.Beats).Select(n => n.Raw).ToList();
        Assert.Equal(Marker, notes[0].Name);
        Assert.Equal(600u, notes[0].PositionMs);
        if (fixture == "unfolded single") notes[0].Flags |= EofConstants.NoteFlagCrazy;
        if (fixture is "edited chord" or "ghost template")
        {
            notes[0].NoteBitmask = 3;
            notes[0].Frets[1] = 9;
            if (fixture == "ghost template") notes[0].GhostBitmask = 2;
        }
        var project = Import(notes, source.Beats);
        var arrangement = Assert.Single(project.Arrangements);
        var level = Assert.Single(arrangement.Levels);
        if (fixture == "folded pair") Assert.Equal(5, Assert.Single(level.Notes).GraceFret);
        else if (fixture == "edited chord") Assert.Single(level.Chords);
        else Assert.Equal(2, level.Notes.Count);
        if (fixture == "ghost template") Assert.Contains(arrangement.ChordTemplates, t => t.GhostMask != 0);
        Assert.DoesNotContain(Marker, StringsIn(project, new HashSet<object>(ReferenceEqualityComparer.Instance)));
    }

    private static ChartProject Import(List<EofRawProGuitarNote> notes, IList<BeatMarker> beats)
    {
        var writer = new EofPackWriter();
        writer.WriteU8(24);
        writer.WriteU8(6);
        for (var s = 0; s < 6; s++) writer.WriteU8(0);
        writer.WriteU32((uint)notes.Count);
        foreach (var note in notes) EofProGuitarNoteWriter.Write(writer, note);
        writer.WriteU16(0);
        writer.WriteU32(0);
        var reader = new EofPackReader(writer.ToArray());
        var arrangement = EofProGuitarTrackParser.Parse(reader, new EofTrackInfo { Id = 9, Name = "Lead" }, beats);
        Assert.True(reader.Eof);
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static IEnumerable<string> StringsIn(object? value, HashSet<object> visited)
    {
        if (value is string text)
        {
            yield return text;
            yield break;
        }
        if (value == null || !visited.Add(value)) yield break;
        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                foreach (var found in StringsIn(item, visited)) yield return found;
            }
            yield break;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) yield break;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            foreach (var found in StringsIn(property.GetValue(value), visited)) yield return found;
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var found in StringsIn(field.GetValue(value), visited)) yield return found;
        }
    }

    private static ChartProject Project()
    {
        var project = new ChartProject();
        for (var i = 0; i < 6; i++) project.Beats.Add(new BeatMarker { Time = i * 600 });
        project.Arrangements.Add(new Arrangement { Name = "Lead", Levels = { new ChartLevel() } });
        return project;
    }
}
