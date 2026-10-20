using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A chord where only ONE note slides (#474): 5 on the A string and 8 on the
///     D string, the 5 legato-slides ("sl.") to 6 while the 8 is held (tied), so
///     the sound goes 5+8 → 6+8.
///     <para>
///         <b>The Rocksmith convention this pins</b> (official library, read-only
///         scan of 5,680 arrangements, 2026-10-08): slides live PER STRING in
///         <c>chordNotes</c>, so only the moving string carries <c>slideTo</c>.
///         The sliding chord note carries <c>linkNext</c>, and the landing is
///         written as loose SINGLE notes, never a chord: of the 814 official chords
///         where some strings slide and others do not, none links into a chord
///         (e.g. 3 Doors Down "It's Not My Time" lead, 93.433 s: B-string 8→10
///         plus an open high E, both <c>linkNext</c>, landing as two single
///         notes). The held string either links on into its single note or just
///         rings.
///     </para>
///     <para>
///         The importer already matches that: <c>GpPostProcessor.LinkSlides</c>
///         resolves the <c>-1</c> marker per string, the GP tie gives the held 8
///         its <c>LinkNext</c>, and the export's <c>LinkLandingBreakup</c> writes
///         the landing chord as single notes. EOF does NOT: its slide is a
///         per-chord flag exported as the same fret delta on every fretted string
///         (<c>EOF_SRC/rs.c</c> <c>slideto = fret + slidediff</c>), so it would slide
///         the held 8 to 9.
///     </para>
/// </summary>
public sealed class GpPartialChordSlideTests
{
    private const int AString = 1;
    private const int DString = 2;

    // ---- gpif fixture plumbing -------------------------------------------

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

    private static string WriteGpif(string notes, string secondBeatNotes)
    {
        var gpif = $"""
            <GPIF>
              <Score><Title>Partial chord slide</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1 2</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /><Notes>{secondBeatNotes}</Notes></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>4</Notes></Beat>
              </Beats>
              <Notes>{notes}</Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-partialslide-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static string Note(int id, int str, int fret, string extra = "", string tie = "") => $"""
        <Note id="{id}">{tie}<Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
          {extra}
        </Properties></Note>
        """;

    private const string LegatoSlide = """<Property name="Slide"><Flags>2</Flags></Property>""";
    private const string TieOrigin = """<Tie origin="true" destination="false" />""";
    private const string TieDestination = """<Tie origin="false" destination="true" />""";

    /// <summary>5(sl.)+8~ | 6+~8 | low-E 3, all quarters.</summary>
    private static string TiedHoldGpif() => WriteGpif(
        Note(0, AString, 5, LegatoSlide) + Note(1, DString, 8, tie: TieOrigin) +
        Note(2, AString, 6) + Note(3, DString, 8, tie: TieDestination) + Note(4, 0, 3),
        "2 3");

    /// <summary>The same case through the GP3-5 binary path (GP5 legato slide = bit 0x02).</summary>
    private static string TiedHoldGp5() => LegacyGpFixtureBuilder.WriteFile("5.10",
    [
        new FxMeasure().AddVoice(
            FxBeat.Chord(0, new FxNote(AString, 5, Slide: 0x02), new FxNote(DString, 8)),
            FxBeat.Chord(0, new FxNote(AString, 6), new FxNote(DString, 8, Tied: true)),
            FxBeat.Note(0, 0, 3),
            FxBeat.RestBeat(0))
    ]);

    public static TheoryData<string> Formats => ["gpif", "gp5"];

    internal static Arrangement ImportTiedHold(string format) =>
        Import(format == "gpif" ? TiedHoldGpif() : TiedHoldGp5());

    /// <summary>5(sl.)+8 | 6+8 | low-E 3: the 8 is RE-PICKED on beat 2, not tied.</summary>
    internal static Arrangement ImportRepickedHold() => Import(WriteGpif(
        Note(0, AString, 5, LegatoSlide) + Note(1, DString, 8) +
        Note(2, AString, 6) + Note(3, DString, 8) + Note(4, 0, 3),
        "2 3"));

    private static ChordNote On(Chord chord, int stringIndex) =>
        Assert.Single(chord.ChordNotes, cn => cn.StringIndex == stringIndex);

    // ---- the import --------------------------------------------------------

    /// <summary>Only the moving string slides; the held string keeps no slide
    /// and links on through the tie. Neither is an unpitched slide.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void OnlyTheSlidingStringCarriesTheSlide(string format)
    {
        var level = ImportTiedHold(format).Levels[0];
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count);

        var origin = chords[0];
        Assert.Equal(FractionalPosition.Zero, origin.Position);
        var sliding = On(origin, AString);
        var held = On(origin, DString);

        Assert.Equal(5, sliding.Fret);
        Assert.Equal(6, sliding.SlideTo);
        Assert.False(sliding.UnpitchedSlide);
        Assert.True(sliding.LinkNext, "the slide sustains into its landing");

        Assert.Equal(8, held.Fret);
        Assert.Null(held.SlideTo); // never slid along with the chord (EOF would export 8 -> 9)
        Assert.False(held.UnpitchedSlide);
        Assert.True(held.LinkNext, "the tied 8 links on into the landing");

        // Both strings sound to the landing beat.
        Assert.Equal(new FractionalPosition(1), sliding.Sustain);
        Assert.Equal(new FractionalPosition(1), held.Sustain);
    }

    /// <summary>The held 8 survives into the landing shape: 6+8 on beat 2, with
    /// its own template and hand shape, and the 5+8 shape ends where it starts.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void TheHeldNoteSurvivesIntoTheLandingShape(string format)
    {
        var arrangement = ImportTiedHold(format);
        var level = arrangement.Levels[0];
        var landing = level.Chords.OrderBy(c => c.Position).Last();

        Assert.Equal(new FractionalPosition(1), landing.Position);
        Assert.Equal(6, On(landing, AString).Fret);
        Assert.Equal(8, On(landing, DString).Fret);
        Assert.All(landing.ChordNotes, cn => Assert.Null(cn.SlideTo));

        var origin = level.Chords.OrderBy(c => c.Position).First();
        var originTemplate = Assert.Single(arrangement.ChordTemplates, t => t.Id == origin.TemplateId);
        var landingTemplate = Assert.Single(arrangement.ChordTemplates, t => t.Id == landing.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [AString] = 5, [DString] = 8 }, originTemplate.Frets);
        Assert.Equal(new Dictionary<int, int> { [AString] = 6, [DString] = 8 }, landingTemplate.Frets);

        var shapes = level.HandShapes.OrderBy(h => h.Position).ToList();
        Assert.Equal(2, shapes.Count);
        Assert.Equal(origin.TemplateId, shapes[0].TemplateId);
        Assert.Equal(new FractionalPosition(1), shapes[0].EndPosition);
        Assert.Equal(landing.TemplateId, shapes[1].TemplateId);
        Assert.Equal(new FractionalPosition(1), shapes[1].Position);
    }

    /// <summary>A RE-PICKED 8 (no tie) is still not a slide and claims no link:
    /// only the sliding string links.</summary>
    [Fact]
    public void ARepickedHeldNote_StillOnlySlidesTheMovingString()
    {
        var level = ImportRepickedHold().Levels[0];
        var origin = level.Chords.OrderBy(c => c.Position).First();

        Assert.Equal(6, On(origin, AString).SlideTo);
        Assert.True(On(origin, AString).LinkNext);
        Assert.Null(On(origin, DString).SlideTo);
        Assert.False(On(origin, DString).LinkNext);
    }

    // ---- what Rocksmith is given -----------------------------------------

    /// <summary>
    ///     The export spelling official charts use: the sliding chord stays a
    ///     chord, and the shape it links into is written as loose SINGLE notes
    ///     (RS reads a chord as a strum; nothing was re-picked). Both XML and SNG
    ///     writers consume this projection.
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void TheLandingIsWrittenAsSingleNotes(string format)
    {
        var level = ImportTiedHold(format).Levels[0];

        var (notes, chords) = LinkLandingBreakup.Project(level);

        var chord = Assert.Single(chords);
        Assert.Equal(FractionalPosition.Zero, chord.Position);
        Assert.Equal(6, On(chord, AString).SlideTo);

        var landing = notes.Where(n => n.Position == new FractionalPosition(1)).OrderBy(n => n.StringIndex).ToList();
        Assert.Equal(new[] { (AString, 6), (DString, 8) }, landing.Select(n => (n.StringIndex, n.Fret)));
        Assert.All(landing, n => Assert.Null(n.SlideTo));
    }
}
