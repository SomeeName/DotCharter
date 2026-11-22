using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Rendering.Data;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A lead phrase tied across three beats on one string: A bends 0 to Full,
///     B is a tied prebend/release Full to 0, C is tied with vibrato and a
///     slide-out. Guitar Pro plays it as ONE pick stroke, so the chart must keep
///     A linked into B and B into C through import, the spacing pass and a save.
/// </summary>
public sealed class GpTiedBendLinkImportTests
{
    private const int LeadString = 2;
    private const int LeadFret = 15;

    private static string Note(int id, string tie, string extra, string props) => $"""
        <Note id="{id}">{tie}{extra}<Properties>
          <Property name="String"><String>{LeadString}</String></Property>
          <Property name="Fret"><Fret>{LeadFret}</Fret></Property>
          {props}
        </Properties></Note>
        """;

    private static string Bend(int origin, int destination, int destinationOffset = 100) => $"""
        <Property name="Bended"><Enable /></Property>
        <Property name="BendOriginOffset"><Float>0</Float></Property>
        <Property name="BendOriginValue"><Float>{origin}</Float></Property>
        <Property name="BendMiddleOffset1"><Float>{destinationOffset / 2}</Float></Property>
        <Property name="BendMiddleOffset2"><Float>{destinationOffset / 2}</Float></Property>
        <Property name="BendMiddleValue"><Float>{(origin + destination) / 2}</Float></Property>
        <Property name="BendDestinationOffset"><Float>{destinationOffset}</Float></Property>
        <Property name="BendDestinationValue"><Float>{destination}</Float></Property>
        """;

    /// <summary>The fixture variants: the rhythm A, B and C are written in, and
    /// whether the tie crosses a barline (A is the last beat of bar 1).</summary>
    private static (string Rhythm, string Feel, bool AcrossBar) Variant(string name) => name switch
    {
        "Quarter" => ("<NoteValue>Quarter</NoteValue>", "", false),
        "Eighth" => ("<NoteValue>Eighth</NoteValue>", "", false),
        "16th" => ("<NoteValue>16th</NoteValue>", "", false),
        "SwungEighth" => ("<NoteValue>Eighth</NoteValue>", "<TripletFeel>Triplet8th</TripletFeel>", false),
        "TripletEighth" => ("""<NoteValue>Eighth</NoteValue><PrimaryTuplet num="3" den="2" />""", "", false),
        "DottedEighth" => ("""<NoteValue>Eighth</NoteValue><AugmentationDot count="1" />""", "", false),
        "QuarterAcrossBar" => ("<NoteValue>Quarter</NoteValue>", "", true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Score(string variant)
    {
        var (rhythm, feel, acrossBar) = Variant(variant);
        var masterBars = acrossBar
            ? $"<MasterBar><Time>4/4</Time>{feel}<Bars>0</Bars></MasterBar><MasterBar><Time>4/4</Time>{feel}<Bars>1</Bars></MasterBar>"
            : $"<MasterBar><Time>4/4</Time>{feel}<Bars>0</Bars></MasterBar>";
        var bars = acrossBar
            ? """<Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar><Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>"""
            : """<Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>""";
        var voices = acrossBar
            ? """<Voice id="0"><Beats>4 0</Beats></Voice><Voice id="1"><Beats>1 2 3</Beats></Voice>"""
            : """<Voice id="0"><Beats>0 1 2 3</Beats></Voice>""";
        return $"""
            <GPIF>
              <Score><Title>Tied bend fixture</Title></Score>
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
              <Bars>{bars}</Bars>
              <Voices>{voices}</Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="1" /></Beat>
                <Beat id="4"><Rhythm ref="2" /></Beat>
              </Beats>
              <Rhythms>
                <Rhythm id="0">{rhythm}</Rhythm>
                <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="2"><NoteValue>Half</NoteValue><AugmentationDot count="1" /></Rhythm>
              </Rhythms>
              <Notes>
              {Note(0, """<Tie origin="true" destination="false" />""", "", Bend(0, 100))}
              {Note(1, """<Tie origin="true" destination="true" />""", "", Bend(100, 0))}
              {Note(2, """<Tie origin="false" destination="true" />""", "<Vibrato>Slight</Vibrato>",
                  """<Property name="Slide"><Flags>4</Flags></Property>""")}
              </Notes>
            </GPIF>
            """;
    }

    /// <summary>
    ///     The same phrase as Guitar Pro 8 itself writes it: A bends and is tied to a
    ///     held beat, the release B is NOT tied (GP marks it as a bend continuation
    ///     only by starting its bend at the held value), and C is a ghost note (the
    ///     parenthesised fret) with vibrato, tied to a tail that slides out. Each
    ///     sound gets its own beat; <paramref name="releaseOrigin" /> is where B's bend
    ///     starts, <paramref name="ghost" /> whether C is written as a ghost note and
    ///     <paramref name="bent" /> whether A and B bend at all.
    /// </summary>
    private static string WrittenReleaseScore(int releaseOrigin = 100, bool ghost = true, bool bent = true)
    {
        return $"""
            <GPIF>
              <Score><Title>Written release fixture</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks>
                <Track id="0">
                  <Name>Lead</Name>
                  <Staves><Staff><Properties>
                    <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                  </Properties></Staff></Staves>
                </Track>
              </Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar><MasterBar><Time>4/4</Time><Bars>1</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar><Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1 2 3 4 5</Beats></Voice><Voice id="1"><Beats>6</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
                <Beat id="3"><Rhythm ref="1" /><Notes>3</Notes></Beat>
                <Beat id="4"><Rhythm ref="0" /><Notes>4</Notes></Beat>
                <Beat id="5"><Rhythm ref="0" /></Beat>
                <Beat id="6"><Rhythm ref="2" /></Beat>
              </Beats>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
                <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
                <Rhythm id="2"><NoteValue>Whole</NoteValue></Rhythm>
              </Rhythms>
              <Notes>
              {Note(0, """<Tie origin="true" destination="false" />""", "", bent ? Bend(0, 100, 25) : "")}
              {Note(1, """<Tie origin="false" destination="true" />""", "", "")}
              {Note(2, "", "", bent ? Bend(releaseOrigin, 0, 25) : "")}
              {Note(3, """<Tie origin="true" destination="false" />""",
                  (ghost ? "<AntiAccent>Normal</AntiAccent>" : "") + "<Vibrato>Slight</Vibrato>", "")}
              {Note(4, """<Tie origin="false" destination="true" />""", "<Vibrato>Slight</Vibrato>",
                  """<Property name="Slide"><Flags>4</Flags></Property>""")}
              </Notes>
            </GPIF>
            """;
    }

    private static GpImportResult ImportGpif(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            return new GpImporter().ImportTracksWithMetadata(path, [0]);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gptiedbend-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static GpImportResult Import(string noteValue) => ImportGpif(Score(noteValue));

    private static string Describe(ChartLevel level) => string.Join(" | ", level.Notes
        .Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position)
        .Select(n => $"@{n.Position} f{n.Fret} sus{n.Sustain} link={n.LinkNext} vib={n.Vibrato} slide={n.SlideTo} bends={n.BendValues.Count}"));

    /// <summary>Every sound on the lead string but the last links into the next, and
    /// each link ends exactly where its continuation starts.</summary>
    private static void AssertChainLinked(ChartLevel level, string stage)
    {
        var notes = level.Notes.Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position).ToList();
        Assert.True(notes.Count >= 2, $"{stage}: the tie chain collapsed to {notes.Count} note(s): {Describe(level)}");
        Assert.Empty(level.Chords);
        for (var i = 0; i < notes.Count - 1; i++)
        {
            Assert.True(notes[i].LinkNext, $"{stage}: note {i} lost LinkNext: {Describe(level)}");
            Assert.True((notes[i].Position + notes[i].Sustain).CompareTo(notes[i + 1].Position) == 0,
                $"{stage}: note {i} does not ring into its continuation: {Describe(level)}");
        }

        Assert.False(notes[^1].LinkNext, $"{stage}: the last note links past the phrase: {Describe(level)}");
        Assert.True(notes[^1].Vibrato, $"{stage}: C lost its vibrato: {Describe(level)}");
    }

    [Theory]
    [InlineData("Quarter")]
    [InlineData("Eighth")]
    [InlineData("16th")]
    [InlineData("SwungEighth")]
    [InlineData("TripletEighth")]
    [InlineData("DottedEighth")]
    [InlineData("QuarterAcrossBar")]
    public void TiedBendReleaseVibratoChain_StaysLinked_AfterImport(string noteValue)
    {
        var imported = Import(noteValue);
        AssertChainLinked(Assert.Single(imported.Arrangements).Levels[^1], "import");
    }

    [Theory]
    [InlineData("Quarter")]
    [InlineData("Eighth")]
    [InlineData("16th")]
    [InlineData("SwungEighth")]
    [InlineData("TripletEighth")]
    [InlineData("DottedEighth")]
    [InlineData("QuarterAcrossBar")]
    public void TiedBendReleaseVibratoChain_StaysLinked_AfterSpacingAndSave(string noteValue) =>
        AssertLinkedThroughSpacingSaveAndHighway(Import(noteValue));

    [Fact]
    public void WrittenRelease_UntiedReleaseAndGhostTail_ImportAsOneLinkedSound()
    {
        var imported = ImportGpif(WrittenReleaseScore());
        var level = Assert.Single(imported.Arrangements).Levels[^1];
        AssertChainLinked(level, "import");
        var notes = level.Notes.Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position).ToList();
        Assert.True(notes[0].BendValues.Any(b => b.Step > 0) && notes[0].BendValues.OrderBy(b => b.Position).Last().Step == 0,
            $"the release was not folded into the bend: {Describe(level)}");
        AssertLinkedThroughSpacingSaveAndHighway(imported);
    }

    [Fact]
    public void WrittenRelease_PrebendNotAtTheHeldPitch_IsAFreshPick()
    {
        var level = Assert.Single(ImportGpif(WrittenReleaseScore(releaseOrigin: 50, ghost: false)).Arrangements).Levels[^1];
        var notes = level.Notes.Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position).ToList();
        Assert.True(notes.Count == 3, Describe(level));
        Assert.False(notes[0].LinkNext, $"a prebend from another pitch is a new pick: {Describe(level)}");
        Assert.False(notes[1].LinkNext, $"a plain note after a release is a new pick: {Describe(level)}");
    }

    [Fact]
    public void GhostNoteAfterAnUnbentNote_IsASoftRePick()
    {
        var level = Assert.Single(ImportGpif(WrittenReleaseScore(bent: false)).Arrangements).Levels[^1];
        var notes = level.Notes.Where(n => n.StringIndex == LeadString).OrderBy(n => n.Position).ToList();
        Assert.True(notes.Count == 3, Describe(level));
        Assert.All(notes, n => Assert.False(n.LinkNext, $"a ghost note only continues a bend: {Describe(level)}"));
    }

    private static void AssertLinkedThroughSpacingSaveAndHighway(GpImportResult imported)
    {
        var arrangement = Assert.Single(imported.Arrangements);
        var beats = GpBeatMapBuilder.Build(imported.Metadata, GpBeatMapBuilder.InitialBpm(imported.Metadata, 120), 0);
        ChartSpacingEnforcer.EnforceArrangement(arrangement, beats, new AppConfig());
        AssertChainLinked(arrangement.Levels[^1], "spacing");

        // Save: the writer enforces a snapshot, then writes it; load parses it and enforces again.
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        ChartSpacingEnforcer.EnforceProject(project, new AppConfig());
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, project);
        var loaded = new ChartProject();
        foreach (var beat in beats) loaded.Beats.Add(beat);
        new RscpArrangementParser().ParseArrangements(doc, loaded);
        ChartSpacingEnforcer.EnforceProject(loaded, new AppConfig());
        var saved = Assert.Single(loaded.Arrangements).Levels[^1];
        AssertChainLinked(saved, "save round trip");

        // The highway draws every continuation as a landing, never as a fresh note head.
        var notes = saved.Notes.OrderBy(n => n.Position).ToList();
        var targets = new HashSet<(long timeMs, int stringIdx)>();
        FrameSnapshot.CollectLinkTargets(notes, saved.Chords, beats, 0, notes.Count - 1, 0, saved.Chords.Count - 1, targets);
        foreach (var follower in notes.Where(n => n.StringIndex == LeadString).Skip(1))
            Assert.Contains(((long)Math.Round(follower.Position.GetPositionMs(beats)), LeadString), targets);
    }
}
