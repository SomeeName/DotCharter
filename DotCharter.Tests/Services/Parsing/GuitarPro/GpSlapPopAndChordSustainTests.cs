using System.IO.Compression;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Two findings from the 2026-07-25 parsing audit, on opposite sides of the
///     bug/decision line.
///     <para>
///     §5.6 — gpif <c>Slapped</c>/<c>Popped</c> note properties were never read.
///     The GP3-5 binary reader started reading the equivalent string-effect byte
///     in 0.33.2, so the two importers DISAGREED, which the legacy-vs-gpif parity
///     rule forbids outright: the binary reader is measured against this one.
///     </para>
///     <para>
///     §5.7 — <c>AdjustSustains</c> assigning every chord note the chord's
///     MAXIMUM sustain. Real, and deliberate: see the pin at the bottom.
///     </para>
/// </summary>
public sealed class GpSlapPopAndChordSustainTests
{
    // ---- §5.6 gpif slap / pop -----------------------------------------------

    [Fact]
    public void Gpif_SlappedNoteProperty_BecomesBassPickingSlap()
    {
        var note = Assert.Single(ImportGpif(Score("""<Property name="Slapped"><Enable /></Property>""")).Notes);
        Assert.Equal(BassPickingType.Slap, note.BassPicking);
    }

    [Fact]
    public void Gpif_PoppedNoteProperty_BecomesBassPickingPop()
    {
        var note = Assert.Single(ImportGpif(Score("""<Property name="Popped"><Enable /></Property>""")).Notes);
        Assert.Equal(BassPickingType.Pop, note.BassPicking);
    }

    /// <summary>The control: without the property nothing is claimed.</summary>
    [Fact]
    public void Gpif_WithoutTheProperty_BassPickingStaysNone()
    {
        var note = Assert.Single(ImportGpif(Score("")).Notes);
        Assert.Equal(BassPickingType.None, note.BassPicking);
    }

    /// <summary>Guitar Pro 7+ (and alphaTab, which our own export goes through) writes slap and
    /// pop on the BEAT, not the note: read that too, on every sound of the beat.</summary>
    [Theory]
    [InlineData("Slapped", BassPickingType.Slap)]
    [InlineData("Popped", BassPickingType.Pop)]
    public void Gpif_BeatLevelSlapOrPop_ReachesEverySoundOfTheBeat(string property, BassPickingType expected)
    {
        var beat = $"""<Property name="{property}"><Enable /></Property>""";
        var single = Assert.Single(ImportGpif(Gpif(NoteXml("0", "0"), "0", beat)).Notes);
        var chord = Assert.Single(ImportGpif(Gpif(NoteXml("0", "0") + NoteXml("1", "1"), "0 1", beat)).Chords);

        Assert.Equal(expected, single.BassPicking);
        Assert.All(chord.ChordNotes, cn => Assert.Equal(expected, cn.BassPicking));
    }

    private static string NoteXml(string id, string stringIndex) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{stringIndex}</String></Property>
          <Property name="Fret"><Fret>5</Fret></Property>
        </Properties></Note>
        """;

    /// <summary>It has to reach CHORD notes too — a slapped strum is exactly the
    /// bass figure this loses otherwise.</summary>
    [Fact]
    public void Gpif_SlappedOnAChord_ReachesEveryChordNote()
    {
        var level = ImportGpif(ChordScore("""<Property name="Slapped"><Enable /></Property>"""));
        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, cn => Assert.Equal(BassPickingType.Slap, cn.BassPicking));
    }

    /// <summary>The parity claim itself: the same technique authored in the GP3-5
    /// binary format already lands on <c>BassPicking</c>, so the two readers must
    /// now agree.</summary>
    [Fact]
    public void GpNoteParser_ReadsSlappedAndPoppedDirectly()
    {
        var parser = new GpNoteParser();

        var slapped = System.Xml.Linq.XElement.Parse("""
            <Note><Properties>
              <Property name="String"><String>0</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
              <Property name="Slapped"><Enable /></Property>
            </Properties></Note>
            """);
        Assert.Equal(BassPickingType.Slap,
            parser.ParseNote(slapped, FractionalPosition.Zero, new FractionalPosition(1), 6, false).BassPicking);

        // ...and it must not leak onto the NEXT note.
        var bare = System.Xml.Linq.XElement.Parse("""
            <Note><Properties>
              <Property name="String"><String>1</String></Property>
              <Property name="Fret"><Fret>5</Fret></Property>
            </Properties></Note>
            """);
        Assert.Equal(BassPickingType.None,
            parser.ParseNote(bare, FractionalPosition.Zero, new FractionalPosition(1), 6, false).BassPicking);
    }

    // ---- §5.7 chord sustains ------------------------------------------------

    /// <summary>
    ///     A chord is ONE gesture: <c>AdjustSustains</c> levels its notes to the
    ///     longest. This is deliberate, and it is a no-op on every real import —
    ///     every site in the pipeline that writes a chord-note sustain writes the
    ///     same value to all of them (the beat's rhythm at parse,
    ///     <c>ChordHandShapeBuilder</c>'s strike trim, the grace shift), and
    ///     <c>MergeTiedNotes</c> only merges chords whose notes match string for
    ///     string, so it adds the same amount to each. The pin exists so a future
    ///     per-string writer makes this visible instead of silently flattening.
    /// </summary>
    [Fact]
    public void AdjustSustains_LevelsAChordsNotesToItsLongest_Deliberately()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = FractionalPosition.Zero };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(4) });
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);

        GpPostProcessor.AdjustSustains(level, 6);

        Assert.All(chord.ChordNotes,
            cn => Assert.Equal(0, cn.Sustain.CompareTo(new FractionalPosition(4))));
    }

    /// <summary>The levelling must not resurrect a sustain the short-chord rules
    /// zeroed.</summary>
    [Fact]
    public void AdjustSustains_AMutedChordStillLosesItsSustain()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = FractionalPosition.Zero };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(4), Mute = MuteType.Palm });
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);

        GpPostProcessor.AdjustSustains(level, 6);

        Assert.All(chord.ChordNotes,
            cn => Assert.Equal(0, cn.Sustain.CompareTo(FractionalPosition.Zero)));
    }

    // ---- helpers ------------------------------------------------------------

    private static ChartLevel ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpslap-{Guid.NewGuid():N}.gp");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Content/score.gpif");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(gpif);
            }

            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arr = Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
            return Assert.Single(arr.Levels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Score(string extraNoteProperty) => Gpif($"""
        <Note id="0"><Properties>
          <Property name="String"><String>0</String></Property>
          <Property name="Fret"><Fret>5</Fret></Property>
          {extraNoteProperty}
        </Properties></Note>
        """, "0");

    private static string ChordScore(string extraNoteProperty) => Gpif($"""
        <Note id="0"><Properties>
          <Property name="String"><String>0</String></Property>
          <Property name="Fret"><Fret>5</Fret></Property>
          {extraNoteProperty}
        </Properties></Note>
        <Note id="1"><Properties>
          <Property name="String"><String>1</String></Property>
          <Property name="Fret"><Fret>5</Fret></Property>
          {extraNoteProperty}
        </Properties></Note>
        """, "0 1");

    private static string Gpif(string notes, string noteIds, string beatProperties = "") => $"""
        <GPIF>
          <Score><Title>Slap Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Bass</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>28 33 38 43</Pitches></Property>
                <Property name="CapoFret"><Fret>0</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>0</Beats></Voice></Voices>
          <Beats><Beat id="0"><Rhythm ref="0" /><Notes>{noteIds}</Notes><Properties>{beatProperties}</Properties></Beat></Beats>
          <Notes>{notes}</Notes>
          <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
        </GPIF>
        """;
}
