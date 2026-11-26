using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #985: plain eighth-note strums of one chord under a triplet feel. Swing moves every
///     off-beat strum from 1/2 to 2/3 but the strum keeps its written length, so each downbeat
///     strum "ended" 1/6 beat before the next one began and the hand-shape planner read that hole
///     as a rest: a new shape on almost every strum. A gap that only swing made is never a rest,
///     a rest the tab wrote still splits, and a shape ends on the swung grid. Bar 1: eight eighth
///     strums. Bar 2: a strum, an eighth rest, six strums.
/// </summary>
public sealed class GpSwungStrumHandShapeTests
{
    private static readonly FractionalPosition SwungRest = new(4, new Fraction(2, 3));

    [Fact]
    public void Gpif_RepeatedSwungStrums_StayOneShapeUntilTheWrittenRest()
        => AssertShapes(ImportGpif());

    [Fact]
    public void Legacy_RepeatedSwungStrums_StayOneShapeUntilTheWrittenRest()
        => AssertShapes(ImportLegacy());

    [Fact]
    public void Gpif_SwungStrikes_KeepNoSustain()
        => AssertStrikesHaveNoSustain(ImportGpif());

    [Fact]
    public void Legacy_SwungStrikes_KeepNoSustain()
        => AssertStrikesHaveNoSustain(ImportLegacy());

    /// <summary>A plain strum is a bare strike: the hand shape carries how long it is held.</summary>
    private static void AssertStrikesHaveNoSustain(Arrangement arrangement) =>
        Assert.All(arrangement.Levels[0].Chords.SelectMany(c => c.ChordNotes),
            cn => Assert.Equal(FractionalPosition.Zero, cn.Sustain));

    [Fact]
    public void Timeline_MapsAWrittenEndOntoTheSwungBeatWrittenThere()
    {
        var bars = GpBarTimeline.FromBarStarts([new FractionalPosition(0), new FractionalPosition(4)]);
        var half = new FractionalPosition(0, new Fraction(1, 2));
        var swung = new FractionalPosition(0, new Fraction(2, 3));
        bars.RecordOnset(swung, half);

        Assert.Equal(swung, bars.PlacedEnd(FractionalPosition.Zero, half));
        Assert.Equal(new FractionalPosition(1), bars.PlacedEnd(swung, half));
        Assert.Equal(new FractionalPosition(2), GpBarTimeline.None.PlacedEnd(new FractionalPosition(1), new FractionalPosition(1)));
    }

    private static void AssertShapes(Arrangement arrangement)
    {
        var level = arrangement.Levels[0];
        Assert.Equal(15, level.Chords.Count);
        var shapes = level.HandShapes.OrderBy(h => h.Position).ToList();
        Assert.Equal(2, shapes.Count);
        Assert.Equal(FractionalPosition.Zero, shapes[0].Position);
        // The strum before the written rest ends where the swung rest begins, not 1/6 earlier.
        Assert.Equal(SwungRest, shapes[0].EndPosition);
        Assert.Equal(new FractionalPosition(5), shapes[1].Position);
        // The last (off-beat) strum ends on the barline it was written to reach.
        Assert.Equal(new FractionalPosition(8), shapes[1].EndPosition);
    }

    private static Arrangement ImportLegacy()
    {
        static FxBeat Strum() => FxBeat.Chord(1, new FxNote(0, 5), new FxNote(1, 7));
        var first = new FxMeasure { TripletFeel = 1 }.AddVoice(Enumerable.Range(0, 8).Select(_ => Strum()).ToArray());
        var second = new FxMeasure { TripletFeel = 1 }.AddVoice(
            [Strum(), FxBeat.RestBeat(1), .. Enumerable.Range(0, 6).Select(_ => Strum())]);
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", [first, second]);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Arrangement ImportGpif()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gp985-{Guid.NewGuid():N}.gp");
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            {
                writer.Write(Gpif());
            }

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Gpif()
    {
        var masterBars = string.Concat(Enumerable.Range(0, 2).Select(i =>
            $"<MasterBar><Time>4/4</Time><TripletFeel>Triplet8th</TripletFeel><Bars>{i}</Bars></MasterBar>"));
        // Beats 0-7: bar 1. Beat 8: strum, beat 9: the rest, beats 10-15: strums.
        var beatIds = Enumerable.Range(0, 16).ToList();
        var beats = string.Concat(beatIds.Select(id => id == 9
            ? $"""<Beat id="{id}"><Rhythm ref="0" /></Beat>"""
            : $"""<Beat id="{id}"><Rhythm ref="0" /><Notes>{2 * id} {2 * id + 1}</Notes></Beat>"""));
        var notes = string.Concat(beatIds.Where(id => id != 9).Select(id => Note(2 * id, 0, 5) + Note(2 * id + 1, 1, 7)));
        return $"""
            <GPIF>
              <Score><Title>Swing Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Rhythm</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>{masterBars}</MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>{string.Join(' ', beatIds.Take(8))}</Beats></Voice>
                <Voice id="1"><Beats>{string.Join(' ', beatIds.Skip(8))}</Beats></Voice>
              </Voices>
              <Beats>{beats}</Beats>
              <Notes>{notes}</Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }

    private static string Note(int id, int str, int fret) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties></Note>
        """;
}
