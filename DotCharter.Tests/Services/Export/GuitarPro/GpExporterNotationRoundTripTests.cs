using System.IO.Compression;
using System.Text;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
/// The acceptance bar for the notation-hint export leg: authored data must WIN
/// on export and survive a full gpif round trip — import a .gp → export a .gp →
/// re-import — preserving <c>NotationDuration</c>, the <c>GraceFret</c> ornament
/// and the notation-sidecar voice content (fret / string / timing).
/// <para>The source .gp is a real Content/score.gpif zip built inline, so the
/// whole <see cref="GpImporter"/> → <see cref="GpExporter"/> → GpImporter
/// pipeline runs (the exporter's Gp7Exporter writes the same gpif shape back).</para>
/// </summary>
public sealed class GpExporterNotationRoundTripTests
{
    private static List<BeatMarker> Beats44(int count, double bpm = 120.0)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 60_000.0 / bpm,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        return beats;
    }

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpnote-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static GpImportResult Import(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static GpImportResult ExportAndReimport(Arrangement arrangement, List<BeatMarker> beats)
    {
        var level = arrangement.Levels[0];
        var bytes = GpExporter.ExportToBytes(arrangement, level, beats, "Round Trip", "DotCharter");
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpnoteexp-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, bytes);
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A single-track gpif with two FULL 4/4 bars (Guitar Pro renormalizes a bar
    // whose beats do not fill it, so fixtures must be complete). Voice 0 (chart)
    // uses quarter / eighth / dotted-quarter / eighth-triplet rhythms — the last
    // as a COMPLETE 3-note group (an isolated triplet has no bar-valid spelling).
    // A before-beat grace precedes the fret-5 eighth. Voice 1 (a notation
    // sidecar) carries two half notes. Rhythm ids MUST be integers — the parser
    // int.TryParses them.
    private const string RhythmAndVoicesGpif = """
        <GPIF>
          <Score><Title>Notation Round Trip</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>
            <MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>
            <MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar>
          </MasterBars>
          <Bars>
            <Bar id="0"><Voices>0 1 -1 -1</Voices></Bar>
            <Bar id="1"><Voices>2 -1 -1 -1</Voices></Bar>
          </Bars>
          <Voices>
            <Voice id="0"><Beats>0 1 2 3 4 5</Beats></Voice>
            <Voice id="1"><Beats>10 11</Beats></Voice>
            <Voice id="2"><Beats>20 21 22 23 24</Beats></Voice>
          </Voices>
          <Beats>
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="1" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="2" /><Notes>2</Notes></Beat>
            <Beat id="3"><Rhythm ref="3" /><Notes>3</Notes></Beat>
            <Beat id="4"><Rhythm ref="2" /><Notes>4</Notes></Beat>
            <Beat id="5"><Rhythm ref="2" /><Notes>5</Notes></Beat>
            <Beat id="10"><Rhythm ref="5" /><Notes>10</Notes></Beat>
            <Beat id="11"><Rhythm ref="5" /><Notes>11</Notes></Beat>
            <Beat id="20"><Rhythm ref="4" /><Notes>20</Notes></Beat>
            <Beat id="21"><Rhythm ref="4" /><Notes>21</Notes></Beat>
            <Beat id="22"><Rhythm ref="4" /><Notes>22</Notes></Beat>
            <Beat id="23"><Rhythm ref="5" /><Notes>23</Notes></Beat>
            <Beat id="24"><Rhythm ref="0" /><Notes>24</Notes></Beat>
          </Beats>
          <Notes>
            <Note id="0"><Properties>
              <Property name="String"><String>0</String></Property><Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="1"><Properties>
              <Property name="String"><String>2</String></Property><Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="2"><Properties>
              <Property name="String"><String>2</String></Property><Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="3"><Properties>
              <Property name="String"><String>2</String></Property><Property name="Fret"><Fret>7</Fret></Property>
            </Properties></Note>
            <Note id="4"><Properties>
              <Property name="String"><String>3</String></Property><Property name="Fret"><Fret>9</Fret></Property>
            </Properties></Note>
            <Note id="5"><Properties>
              <Property name="String"><String>3</String></Property><Property name="Fret"><Fret>10</Fret></Property>
            </Properties></Note>
            <Note id="10"><Properties>
              <Property name="String"><String>5</String></Property><Property name="Fret"><Fret>3</Fret></Property>
            </Properties></Note>
            <Note id="11"><Properties>
              <Property name="String"><String>5</String></Property><Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="20"><Properties>
              <Property name="String"><String>3</String></Property><Property name="Fret"><Fret>2</Fret></Property>
            </Properties></Note>
            <Note id="21"><Properties>
              <Property name="String"><String>3</String></Property><Property name="Fret"><Fret>4</Fret></Property>
            </Properties></Note>
            <Note id="22"><Properties>
              <Property name="String"><String>3</String></Property><Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            <Note id="23"><Properties>
              <Property name="String"><String>4</String></Property><Property name="Fret"><Fret>0</Fret></Property>
            </Properties></Note>
            <Note id="24"><Properties>
              <Property name="String"><String>4</String></Property><Property name="Fret"><Fret>2</Fret></Property>
            </Properties></Note>
          </Notes>
          <Rhythms>
            <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>16th</NoteValue></Rhythm>
            <Rhythm id="2"><NoteValue>Eighth</NoteValue></Rhythm>
            <Rhythm id="3"><NoteValue>Quarter</NoteValue><AugmentationDot count="1" /></Rhythm>
            <Rhythm id="4"><NoteValue>Eighth</NoteValue><PrimaryTuplet num="3" den="2" /></Rhythm>
            <Rhythm id="5"><NoteValue>Half</NoteValue></Rhythm>
          </Rhythms>
        </GPIF>
        """;

    [Fact]
    public void NotationDuration_WinsOnExport_AndSurvivesRoundTrip()
    {
        var source = Assert.Single(Import(RhythmAndVoicesGpif).Arrangements);
        var srcLevel = source.Levels[0];

        // Sanity: the importer captured the authored rhythms on voice 0.
        var srcByString = srcLevel.Notes.ToDictionary(n => (n.StringIndex, n.Fret), n => n.NotationDuration);
        Assert.Equal(new Fraction(3, 2), srcByString[(2, 7)]);  // dotted quarter
        Assert.Equal(new Fraction(1, 3), srcByString[(3, 2)]);  // eighth triplet (group of 3)

        var result = ExportAndReimport(source, Beats44(8));
        var level = Assert.Single(result.Arrangements).Levels[0];

        // The authored durations round-trip exactly (they drove the exported GP
        // beat rhythm, so re-import re-derives the same NotationDuration).
        var byString = level.Notes.ToDictionary(n => (n.StringIndex, n.Fret), n => n.NotationDuration);
        Assert.Equal(new Fraction(1, 1), byString[(0, 3)]);  // quarter
        Assert.Equal(new Fraction(1, 2), byString[(2, 5)]);  // eighth
        Assert.Equal(new Fraction(3, 2), byString[(2, 7)]);  // dotted quarter
        Assert.Equal(new Fraction(1, 3), byString[(3, 2)]);  // eighth triplet (complete group)
        Assert.Equal(new Fraction(2, 1), byString[(4, 0)]);  // half
    }

    [Fact]
    public void GraceHint_SurvivesRoundTrip()
    {
        var source = Assert.Single(Import(RhythmAndVoicesGpif).Arrangements);

        var result = ExportAndReimport(source, Beats44(8));
        var level = Assert.Single(result.Arrangements).Levels[0];

        // The grace ornament (fret 3 on string 2, ahead of the fret-5 eighth)
        // re-imports as the target note's GraceFret hint on the same string.
        var graced = Assert.Single(level.Notes, n => n.GraceFret != null);
        Assert.Equal(2, graced.StringIndex);
        Assert.Equal(5, graced.Fret);
        Assert.Equal(3, graced.GraceFret);
    }

    [Fact]
    public void SidecarVoice_SurvivesRoundTrip()
    {
        var source = Assert.Single(Import(RhythmAndVoicesGpif).Arrangements);
        Assert.Single(source.NotationVoices); // voice 1 imported as a sidecar

        var result = ExportAndReimport(source, Beats44(8));
        var arr = Assert.Single(result.Arrangements);

        // The second voice was exported as GP voice 2 and re-imported as a
        // notation sidecar with its notes' fret / string / timing intact.
        var sidecar = Assert.Single(arr.NotationVoices);
        var sideNotes = sidecar.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, sideNotes.Count);
        Assert.Equal((5, 3), (sideNotes[0].StringIndex, sideNotes[0].Fret));
        Assert.Equal((5, 5), (sideNotes[1].StringIndex, sideNotes[1].Fret));
        Assert.Equal(new FractionalPosition(0), sideNotes[0].Position);
        Assert.Equal(new FractionalPosition(2), sideNotes[1].Position);
    }

    /// <summary>A single full 4/4 bar: a chord on beat 1 plus three quarters, so
    /// Guitar Pro does not renormalize the bar. Graces are opt-in per string.</summary>
    private static Arrangement ChordBarArrangement(int? graceOnString2, int? graceOnString3)
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 7, Sustain = new FractionalPosition(1), GraceFret = graceOnString2
        });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 3, Fret = 9, Sustain = new FractionalPosition(1), GraceFret = graceOnString3
        });
        level.Chords.Add(chord);
        for (var b = 1; b < 4; b++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(b), StringIndex = 0, Fret = 3,
                Sustain = new FractionalPosition(1)
            });
        }

        arr.Levels.Add(level);
        return arr;
    }

    private static string GpifOf(byte[] gp)
    {
        using var ms = new MemoryStream(gp);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("Content/score.gpif");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ChordGraceHints_SurviveRoundTrip_OnEveryGracedString()
    {
        // Guitar Pro graces a strum by putting one grace note per string on the
        // grace beat; the exporter now writes that shape, so a chord ornament
        // round-trips instead of vanishing at the .gp boundary.
        var source = ChordBarArrangement(graceOnString2: 5, graceOnString3: 7);

        var result = ExportAndReimport(source, Beats44(4));
        var level = Assert.Single(result.Arrangements).Levels[0];

        var chord = Assert.Single(level.Chords, c => c.ChordNotes.Any(cn => cn.GraceFret != null));
        var byString = chord.ChordNotes.ToDictionary(cn => cn.StringIndex, cn => cn.GraceFret);
        Assert.Equal(5, byString[2]);
        Assert.Equal(7, byString[3]);
    }

    [Fact]
    public void ChordGraceHint_OnOneStringOnly_LeavesTheOtherStringsUngraced()
    {
        var source = ChordBarArrangement(graceOnString2: 5, graceOnString3: null);

        var result = ExportAndReimport(source, Beats44(4));
        var level = Assert.Single(result.Arrangements).Levels[0];

        var chord = Assert.Single(level.Chords, c => c.ChordNotes.Any(cn => cn.GraceFret != null));
        var byString = chord.ChordNotes.ToDictionary(cn => cn.StringIndex, cn => cn.GraceFret);
        Assert.Equal(5, byString[2]);
        Assert.Null(byString[3]);
    }

    [Fact]
    public void Export_WithoutChordGraceHints_WritesNoGraceBeat()
    {
        // The additive-change proof at the BYTE level: with no hint the exporter
        // emits no grace beat at all, so the gpif carries no <GraceNotes> element
        // and the .gp is exactly what it always was.
        var plain = ChordBarArrangement(graceOnString2: null, graceOnString3: null);
        var plainBytes = GpExporter.ExportToBytes(plain, plain.Levels[0], Beats44(4), "Round Trip", "DotCharter");
        Assert.DoesNotContain("GraceNotes", GpifOf(plainBytes));

        // And the same chart WITH hints does write one — the assertion above is
        // therefore about the hint, not about the marker being unreachable.
        var graced = ChordBarArrangement(graceOnString2: 5, graceOnString3: 7);
        var gracedBytes = GpExporter.ExportToBytes(graced, graced.Levels[0], Beats44(4), "Round Trip", "DotCharter");
        Assert.Contains("<GraceNotes>BeforeBeat</GraceNotes>", GpifOf(gracedBytes));

        var level = Assert.Single(ExportAndReimport(plain, Beats44(4)).Arrangements).Levels[0];
        Assert.All(level.Chords, c => Assert.All(c.ChordNotes, cn => Assert.Null(cn.GraceFret)));
        Assert.All(level.Notes, n => Assert.Null(n.GraceFret));
    }

    [Fact]
    public void Export_WithoutNotationHints_IsUnchanged_FallsBackToTimingDerivation()
    {
        // A chart with NO NotationDuration (e.g. an RS import) must still export
        // via the sustain/onset-gap derivation — the authored path is opt-in.
        var arr = new Arrangement { Name = "Plain", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1) });
        arr.Levels.Add(level);

        var result = ExportAndReimport(arr, Beats44(4));
        var imported = Assert.Single(result.Arrangements).Levels[0];
        var note = Assert.Single(imported.Notes);
        Assert.Equal(0, note.StringIndex);
        Assert.Equal(3, note.Fret);
    }
}
