using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #940: an arpeggio bar tabbed as let-ring notes plus a plain dotted quarter, repeated bar
///     after bar under a triplet feel. The import ran each shape across two bars, ended the last
///     one inside the following rest bar (swung onset + written length) and left the plain note
///     sustaining to the barline under the shape; the owner fixed all three by hand. A let-ring
///     run now stops at the barline, ends at its last note's written end, and the plain tail the
///     shape carries becomes a strike.
/// </summary>
public sealed class GpLetRingBarlineHandShapeTests
{
    private static readonly GpBarTimeline FourFour = GpBarTimeline.FromBarStarts(
        [new FractionalPosition(0), new FractionalPosition(4), new FractionalPosition(8), new FractionalPosition(12)]);

    [Fact]
    public void ABarThatRestartsTheArpeggio_GetsItsOwnShape()
    {
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        foreach (var bar in new[] { 0, 4 })
        {
            Ring(level, letRing, new FractionalPosition(bar), 1, 7);
            Ring(level, letRing, new FractionalPosition(bar + 1), 2, 9);
            Ring(level, letRing, new FractionalPosition(bar + 2), 4, 8);
        }

        GpHandShapeBuilder.BuildLetRingHandShapes(level, new Arrangement(), letRing, bars: FourFour);

        Assert.Equal(2, level.HandShapes.Count);
        Assert.True(level.HandShapes[0].EndPosition.CompareTo(new FractionalPosition(4)) <= 0);
        Assert.Equal(new FractionalPosition(4), level.HandShapes[1].Position);
    }

    [Fact]
    public void TheLastRunEndsAtTheBarline_NotInTheRestBarAfterIt()
    {
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        Ring(level, letRing, new FractionalPosition(0), 1, 12);
        Ring(level, letRing, new FractionalPosition(2), 2, 14, new Fraction(5, 2));

        GpHandShapeBuilder.BuildLetRingHandShapes(level, new Arrangement(), letRing, bars: FourFour);

        Assert.Equal(new FractionalPosition(4), Assert.Single(level.HandShapes).EndPosition);
    }

    [Fact]
    public void ASwungLastNote_EndsTheShapeAtItsWrittenEnd()
    {
        // Written at 1/2 for an eighth (ends on beat 1), swung to 2/3; a rest follows.
        var bars = GpBarTimeline.FromBarStarts([new FractionalPosition(0), new FractionalPosition(4)]);
        var swung = new FractionalPosition(0, new Fraction(2, 3));
        bars.RecordOnset(swung, new FractionalPosition(0, new Fraction(1, 2)));
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        Ring(level, letRing, FractionalPosition.Zero, 1, 12, new Fraction(1, 2));
        Ring(level, letRing, swung, 2, 14, new Fraction(1, 2));

        GpHandShapeBuilder.BuildLetRingHandShapes(level, new Arrangement(), letRing, bars: bars);

        Assert.Equal(new FractionalPosition(1), Assert.Single(level.HandShapes).EndPosition);
    }

    [Theory]
    [InlineData(3, 7)] // a free string inside the grip
    [InlineData(2, 9)] // a string the run holds, struck again at its fret
    public void APlainTailSustainingToTheShapesEnd_BecomesAStrike(int stringIndex, int fret)
    {
        var (level, tail) = ArpeggioWithTail(stringIndex, fret, sustainTo: 4);

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(4), shape.EndPosition);
        Assert.Equal(FractionalPosition.Zero, tail.Sustain);
    }

    [Fact]
    public void APlainTailHeldPastTheShape_KeepsItsSustain()
    {
        var (level, tail) = ArpeggioWithTail(3, 7, sustainTo: 6);

        Assert.Equal(new FractionalPosition(4), Assert.Single(level.HandShapes).EndPosition);
        Assert.Equal(new FractionalPosition(3), tail.Sustain);
    }

    [Fact]
    public void GpifImport_SplitsTheBarsEndsAtTheBarlineAndStrikesTheTail()
    {
        // Bar 1: q s1f7 LR, e s2f9 LR, e s4f8 LR tied into a half. Bar 2 restarts it: q s1f7 LR,
        // e s2f9 LR, q s4f8 LR, dotted q s3f7. Bar 3: bar 1's rhythm at frets 12/14/14, its tied
        // half written to end on the barline. Bar 4: rest.
        var level = ImportSingleTrack(Gpif()).Levels[0];

        var shapes = level.HandShapes.OrderBy(h => h.Position).ToList();
        Assert.Equal(3, shapes.Count);
        Assert.Equal([0, 4, 8], shapes.Select(h => h.Position.BeatId));
        Assert.All(shapes, h => Assert.True(h.EndPosition.CompareTo(new FractionalPosition(h.Position.BeatId + 4)) <= 0));
        Assert.Equal(new FractionalPosition(12), shapes[2].EndPosition);
        var tail = Assert.Single(level.Notes, n => n.Position.BeatId == 6 && n.Fret == 7);
        Assert.Equal(FractionalPosition.Zero, tail.Sustain);
    }

    [Fact]
    public void LegacyImport_ASwungLastNoteFollowedByARest_EndsTheShapeAtItsWrittenEnd()
    {
        // GP3-5, triplet feel. The second eighth is written at 1/2 and swung to 2/3; a rest
        // follows inside the bar. The shape ends where the note was written to end (beat 1), not
        // 1/6 beat later.
        var bar = new FxMeasure { TripletFeel = 1 }.AddVoice(
            new FxBeat { Duration = 1, Notes = { new FxNote(1, 12, LetRing: true) } },
            new FxBeat { Duration = 1, Notes = { new FxNote(2, 14, LetRing: true) } },
            FxBeat.RestBeat(0), FxBeat.RestBeat(-1));
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", [bar], globalShuffle: true);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var level = Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList())).Levels[0];

            Assert.Equal(new FractionalPosition(1), Assert.Single(level.HandShapes).EndPosition);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (ChartLevel Level, GuitarNote Tail) ArpeggioWithTail(int stringIndex, int fret, int sustainTo)
    {
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();
        Ring(level, letRing, new FractionalPosition(0), 1, 7);
        Ring(level, letRing, new FractionalPosition(1), 2, 9);
        Ring(level, letRing, new FractionalPosition(2), 4, 8);
        var tail = new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = stringIndex, Fret = fret,
            Sustain = new FractionalPosition(sustainTo - 3), NotationDuration = new Fraction(sustainTo - 3, 1)
        };
        level.Notes.Add(tail);

        GpHandShapeBuilder.BuildLetRingHandShapes(level, new Arrangement(), letRing, bars: FourFour);
        return (level, tail);
    }

    private static void Ring(ChartLevel level, HashSet<FractionalPosition> letRing, FractionalPosition at,
        int stringIndex, int fret, Fraction? length = null)
    {
        level.Notes.Add(new GuitarNote
        {
            Position = at, StringIndex = stringIndex, Fret = fret, NotationDuration = length ?? new Fraction(1, 1)
        });
        letRing.Add(at);
    }

    private static Arrangement ImportSingleTrack(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gp940-{Guid.NewGuid():N}.gp");
        try
        {
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            {
                writer.Write(gpif);
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

    private static string Note(int id, int str, int fret, bool letRing, string tie = "") => $"""
        <Note id="{id}">{tie}<Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties>{(letRing ? "<LetRing />" : "")}</Note>
        """;

    private static string Gpif()
    {
        const string tieOut = """<Tie origin="true" destination="false" />""";
        const string tieIn = """<Tie origin="false" destination="true" />""";
        var masterBars = string.Concat(Enumerable.Range(0, 4).Select(i =>
            $"<MasterBar><Time>4/4</Time><TripletFeel>Triplet8th</TripletFeel><Bars>{i}</Bars></MasterBar>"));
        var notes = Note(0, 1, 7, true) + Note(1, 2, 9, true) + Note(2, 4, 8, true, tieOut) + Note(3, 4, 8, true, tieIn) +
                    Note(4, 1, 7, true) + Note(5, 2, 9, true) + Note(6, 4, 8, true) + Note(7, 3, 7, false) +
                    Note(8, 1, 12, true) + Note(9, 2, 14, true) + Note(10, 4, 14, true, tieOut) +
                    Note(11, 4, 14, true, tieIn);
        return $"""
            <GPIF>
              <Score><Title>Barline Fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars>{masterBars}</MasterBars>
              <Bars>
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
                <Bar id="2"><Voices>2 -1 -1 -1</Voices></Bar>
                <Bar id="3"><Voices>3 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0 1 2 3</Beats></Voice>
                <Voice id="1"><Beats>4 5 6 7</Beats></Voice>
                <Voice id="2"><Beats>8 9 10 11</Beats></Voice>
                <Voice id="3"><Beats>12</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="1" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="1" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="3" /><Notes>3</Notes></Beat>
                <Beat id="4"><Rhythm ref="0" /><Notes>4</Notes></Beat>
                <Beat id="5"><Rhythm ref="1" /><Notes>5</Notes></Beat>
                <Beat id="6"><Rhythm ref="0" /><Notes>6</Notes></Beat>
                <Beat id="7"><Rhythm ref="2" /><Notes>7</Notes></Beat>
                <Beat id="8"><Rhythm ref="0" /><Notes>8</Notes></Beat>
                <Beat id="9"><Rhythm ref="1" /><Notes>9</Notes></Beat>
                <Beat id="10"><Rhythm ref="1" /><Notes>10</Notes></Beat>
                <Beat id="11"><Rhythm ref="3" /><Notes>11</Notes></Beat>
                <Beat id="12"><Rhythm ref="4" /></Beat>
              </Beats>
              <Notes>{notes}</Notes>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>Eighth</NoteValue></Rhythm>
                <Rhythm id="2"><NoteValue>Quarter</NoteValue><AugmentationDot count="1" /></Rhythm>
                <Rhythm id="3"><NoteValue>Half</NoteValue></Rhythm>
                <Rhythm id="4"><NoteValue>Whole</NoteValue></Rhythm>
              </Rhythms>
            </GPIF>
            """;
    }
}
