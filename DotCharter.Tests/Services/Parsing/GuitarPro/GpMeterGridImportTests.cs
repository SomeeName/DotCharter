using System.IO.Compression;
using System.Text;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A bar whose time signature is not x/4. The beat map gives a 6/8 bar six
///     eighth-note beats, and the parsers count in quarter notes, so the importer must convert:
///     left in quarters, the six eighths of a 6/8 bar land on beats 0, ½, 1 … 2½ — played twice as
///     fast — and every later bar slides into the wrong measure. Synthetic scores only.
/// </summary>
public sealed class GpMeterGridImportTests
{
    /// <summary>Bar 1 in 6/8: six eighths, frets 1-6. Bar 2 in 4/4: four quarters, frets 7-10.</summary>
    private static string Gpif()
    {
        static string Note(int id, int fret) => $"""
            <Note id="{id}"><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>{fret}</Fret></Property>
            </Properties></Note>
            """;
        var beats = string.Concat(Enumerable.Range(0, 10).Select(i =>
            $"""<Beat id="{i}"><Rhythm ref="{(i < 6 ? 0 : 1)}" /><Notes>{i}</Notes></Beat>"""));
        return $"""
            <GPIF>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars>
                <MasterBar><Time>6/8</Time><Bars>0</Bars></MasterBar>
                <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
              </MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar><Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1 2 3 4 5</Beats></Voice><Voice id="1"><Beats>6 7 8 9</Beats></Voice></Voices>
              <Beats>{beats}</Beats>
              <Notes>{string.Concat(Enumerable.Range(0, 10).Select(i => Note(i, i + 1)))}</Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }

    private static string WriteGpif()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-meter-{Guid.NewGuid():N}.gp");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8);
        writer.Write(Gpif());
        return path;
    }

    private static string WriteGp5()
    {
        var sixEight = new FxMeasure { Num = 6, Den = 8 }.AddVoice(
            [.. Enumerable.Range(0, 6).Select(i => FxBeat.Note(1, 0, i + 1))]);
        var fourFour = new FxMeasure().AddVoice([.. Enumerable.Range(6, 4).Select(i => FxBeat.Note(0, 0, i + 1))]);
        return LegacyGpFixtureBuilder.WriteFile("5.10", [sixEight, fourFour]);
    }

    public static TheoryData<string> Formats => ["gpif", "gp5"];

    private static (List<GuitarNote> Notes, List<BeatMarker> Beats) Import(string format, GpImportSettings? settings = null)
    {
        var path = format == "gpif" ? WriteGpif() : WriteGp5();
        try
        {
            var importer = new GpImporter();
            var result = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)],
                settings);
            var beats = GpBeatMapBuilder.Build(result.Metadata, 120, 0, settings?.ImportTimeSignatures ?? true);
            return ([.. Assert.Single(result.Arrangements).Levels[0].Notes.OrderBy(n => n.Position)], beats);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void EighthNotesInSixEight_TakeOneBeatEach(string format)
    {
        var (notes, beats) = Import(format);

        Assert.Equal(10, beats.Count); // six eighth beats, then four quarter beats
        Assert.Equal(Enumerable.Range(1, 10), notes.Select(n => n.Fret));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], notes.Select(n => n.Position.BeatId));
        Assert.All(notes, n => Assert.Equal(Fraction.Zero, n.Position.Fraction));
        // 120 bpm: an eighth is 250 ms, so the 4/4 bar starts at 1.5 s.
        Assert.Equal(250, notes[1].GetTimeMs(beats), 3);
        Assert.Equal(1500, notes[6].GetTimeMs(beats), 3);
    }

    /// <summary>With time signatures off the beat map is four quarter beats per bar, so
    /// quarter-note positions are already right and nothing moves.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void WithTimeSignaturesOff_PositionsStayInQuarters(string format)
    {
        var (notes, beats) = Import(format, new GpImportSettings { ImportTimeSignatures = false });

        Assert.Equal(250, notes[1].GetTimeMs(beats), 3);
        Assert.Equal(new FractionalPosition(0, new Fraction(1, 2)), notes[1].Position);
        Assert.Equal(new FractionalPosition(3), notes[6].Position);
    }

    /// <summary>
    ///     Export → re-import keeps an x/8 bar as written. <c>NotationDuration</c> counts
    ///     quarter notes while the exporter's tick grid counts beats; mixing them up writes every
    ///     authored eighth of a 6/8 bar as a sixteenth plus a sixteenth rest, and the
    ///     re-import marks the note accented (a short rest after a note is the
    ///     staccato convention).
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void AnX8Bar_SurvivesTheGuitarProRoundTrip(string format)
    {
        var path = format == "gpif" ? WriteGpif() : WriteGp5();
        var again = Path.Combine(Path.GetTempPath(), $"dotcharter-meter-rt-{Guid.NewGuid():N}.gp");
        try
        {
            var importer = new GpImporter();
            var first = importer.ImportTracksWithMetadata(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            var arrangement = Assert.Single(first.Arrangements);
            var beats = GpBeatMapBuilder.Build(first.Metadata, 120, 0);
            File.WriteAllBytes(again, GpExporter.ExportToBytes(arrangement, arrangement.Levels[0], beats));
            var second = importer.ImportTracksWithMetadata(again, [.. importer.GetAvailableTracks(again).Select(t => t.Id)]);

            static string Describe(Arrangement a) => string.Join(" ", a.Levels[0].Notes.OrderBy(n => n.Position)
                .Select(n => $"{n.Position}:{n.Fret}:{n.NotationDuration}{(n.Accent ? ":ac" : "")}"));
            Assert.Equal(Describe(arrangement), Describe(Assert.Single(second.Arrangements)));
        }
        finally
        {
            File.Delete(path);
            File.Delete(again);
        }
    }
}
