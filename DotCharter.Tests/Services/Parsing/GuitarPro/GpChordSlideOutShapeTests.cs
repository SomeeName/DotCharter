using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #851: the notes of a chord that slide out share one distance, so a 3/5 grip
///     sliding down lands on 1/3 instead of collapsing onto fret 1. Single notes keep
///     the per-note rule.
/// </summary>
public sealed class GpChordSlideOutShapeTests
{
    private const int Down = 4;
    private const int Up = 8;

    private static string Gpif(int capo, string notes, string beatNotes) => $"""
        <GPIF>
          <Score><Title>Chord slide out</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks><Track id="0"><Name>Guitar</Name><Staves><Staff><Properties>
            <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
            <Property name="CapoFret"><Fret>{capo}</Fret></Property>
          </Properties></Staff></Staves></Track></Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
          <Beats><Beat id="0"><Rhythm ref="0" /><Notes>{beatNotes}</Notes></Beat></Beats>
          <Notes>{notes}</Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;

    private static string Note(int id, int str, int fret, int flags) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
          <Property name="Slide"><Flags>{flags}</Flags></Property>
        </Properties></Note>
        """;

    private static Arrangement ImportGpif(int capo, int flags, params int[] frets)
    {
        var notes = string.Concat(frets.Select((f, i) => Note(i, i + 1, f, flags)));
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-chordslide-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
        {
            writer.Write(Gpif(capo, notes, string.Join(' ', frets.Select((_, i) => i))));
        }

        return Import(path);
    }

    private static Arrangement ImportGp5(int slide, params int[] frets)
        => Import(LegacyGpFixtureBuilder.WriteFile("5.10",
        [
            new FxMeasure().AddVoice(FxBeat.Chord(0,
                frets.Select((f, i) => new FxNote(i + 1, f, Slide: slide)).ToArray()))
        ]));

    private static Arrangement Import(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static int[] Landings(Arrangement arrangement)
    {
        var chord = Assert.Single(arrangement.Levels[0].Chords);
        return chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.SlideTo ?? -99).ToArray();
    }

    [Fact]
    public void Gpif_ChordDownAtTheFloor_KeepsTheShape() => Assert.Equal([1, 3], Landings(ImportGpif(0, Down, 3, 5)));

    [Fact]
    public void Gpif_ChordDownWithRoom_MovesFiveFrets() => Assert.Equal([5, 7], Landings(ImportGpif(0, Down, 10, 12)));

    [Fact]
    public void Gpif_ChordDown7And9_Lands2And4() => Assert.Equal([2, 4], Landings(ImportGpif(0, Down, 7, 9)));

    [Fact]
    public void Gpif_ChordUpAtTheCeiling_KeepsTheShape() => Assert.Equal([24, 22], Landings(ImportGpif(0, Up, 22, 20)));

    // Capo 2: GP 3/5 arrive as absolute 5/7; the floor is the capo, so the landing is 2/4.
    [Fact]
    public void Gpif_ChordDownWithCapo_FloorsAtTheCapo() => Assert.Equal([2, 4], Landings(ImportGpif(2, Down, 3, 5)));

    [Fact]
    public void Gpif_SingleNote_KeepsThePerNoteRule()
    {
        var note = Assert.Single(ImportGpif(0, Down, 3).Levels[0].Notes);
        Assert.Equal(1, note.SlideTo);
    }

    [Theory]
    [InlineData(4, 3, 5, 1, 3)]
    [InlineData(8, 22, 20, 24, 22)]
    public void Gp5_ChordKeepsTheShape(int slide, int a, int b, int landA, int landB)
        => Assert.Equal([landA, landB], Landings(ImportGp5(slide, a, b)));
}
