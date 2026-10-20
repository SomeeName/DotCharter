using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// Triplet-feel (swing) placement from the GP7/GP8 XML container. GP encodes
/// swing as bar-level metadata (<c>&lt;TripletFeel&gt;Triplet8th&lt;/TripletFeel&gt;</c>)
/// over straight-notated rhythms; the importer applies it to positions so the
/// chart matches the swung recording. Three invariants, all owner-reported
/// regressions from a real GP8 export (TABS.gp, 2026-07-17):
/// <list type="bullet">
/// <item>A beat that IS a tuplet never swings — the explicit tuplet already
/// spells the swung subdivision. Swinging a 16th-triplet's first note (notated
/// at the off-beat eighth slot) shoved it onto its own group's second note,
/// producing coincident notes (L15) and a degenerate same-fret slide (E01).</item>
/// <item>A swung beat's EFFECTIVE duration is the gap to the next beat's swung
/// onset (on-beat eighth 2/3, off-beat 1/3) — notated 1/2s left link-next
/// sustains 1/6 short of their targets (I08) and chord handshapes overlapping
/// (L17).</item>
/// <item>The authored rhythm survives as NotationDuration (the sheet hint) —
/// swing must not leak into notation.</item>
/// </list>
/// </summary>
public sealed class GpTripletFeelImportTests
{
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpfeel-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static Arrangement ImportSingleTrack(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(arrangements);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Envelope(string masterBar, string voices, string beats, string notes, string rhythms) => $"""
        <GPIF>
          <Score><Title>Triplet Feel Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars>{masterBar}</MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>{voices}</Beats></Voice></Voices>
          <Beats>{beats}</Beats>
          <Notes>{notes}</Notes>
          <Rhythms>{rhythms}</Rhythms>
        </GPIF>
        """;

    private static string Note(int id, int str, int fret, string extra = "") => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{str}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
          {extra}
        </Properties></Note>
        """;

    private static void AssertAt(GuitarNote note, int beatId, Fraction fraction)
    {
        var expected = new FractionalPosition(beatId, fraction);
        Assert.True(note.Position.CompareTo(expected) == 0,
            $"expected position {beatId}+{fraction.Numerator}/{fraction.Denominator}, " +
            $"got {note.Position.BeatId}+{note.Position.Fraction.Numerator}/{note.Position.Fraction.Denominator}");
    }

    /// <summary>Straight swung eighths: off-beats land at 2/3 and a legato slide
    /// on the ON-beat eighth sustains the full 2/3 gap onto its swung target
    /// (link-next lands exactly; notated eighth stays in NotationDuration).</summary>
    [Fact]
    public void Triplet8thFeel_SwingsOffbeatsAndSizesSustainsToTheGap()
    {
        // 4/4: E5 slide-legato→E7 | E5 E7 | quarter 5 | quarter 5, all string 2.
        var gpif = Envelope(
            "<MasterBar><Time>4/4</Time><TripletFeel>Triplet8th</TripletFeel><Bars>0</Bars></MasterBar>",
            "0 1 2 3 4 5",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
            <Beat id="3"><Rhythm ref="0" /><Notes>3</Notes></Beat>
            <Beat id="4"><Rhythm ref="1" /><Notes>4</Notes></Beat>
            <Beat id="5"><Rhythm ref="1" /><Notes>5</Notes></Beat>
            """,
            Note(0, 2, 5, """<Property name="Slide"><Flags>2</Flags></Property>""") +
            Note(1, 2, 7) + Note(2, 2, 5) + Note(3, 2, 7) + Note(4, 2, 5) + Note(5, 2, 5),
            """
            <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
            """);

        var arr = ImportSingleTrack(gpif);
        var notes = arr.Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(6, notes.Count);

        AssertAt(notes[0], 0, Fraction.Zero);
        AssertAt(notes[1], 0, new Fraction(2, 3)); // off-beat eighth swings
        AssertAt(notes[2], 1, Fraction.Zero);
        AssertAt(notes[3], 1, new Fraction(2, 3));

        // The legato slide resolves to the swung target and its sustain lands
        // exactly on it (regression: notated 1/2 fell 1/6 short → I08).
        var slide = notes[0];
        Assert.Equal(7, slide.SlideTo);
        Assert.True(slide.LinkNext);
        Assert.True((slide.Position + slide.Sustain).CompareTo(notes[1].Position) == 0,
            "slide sustain must land exactly on the swung target onset");

        // Swing is a placement concern only — notation keeps the authored eighth.
        Assert.Equal(new Fraction(1, 2), slide.NotationDuration);
    }

    /// <summary>The TABS.gp m103 shape: a 16th-triplet group starting at the
    /// off-beat eighth slot. The group must keep its notated placement (1/2,
    /// 2/3, 5/6) — swinging its first note onto the second produced coincident
    /// notes and a 14→14 self-slide.</summary>
    [Fact]
    public void Triplet8thFeel_TupletBeatsAreExemptFromSwing()
    {
        // 4/4: E14 | T16 14 slide-legato | T16 15 | T16 14 | E12 | E12 | quarter | quarter (str 5)
        var gpif = Envelope(
            "<MasterBar><Time>4/4</Time><TripletFeel>Triplet8th</TripletFeel><Bars>0</Bars></MasterBar>",
            "0 1 2 3 4 5 6 7",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="2" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="2" /><Notes>2</Notes></Beat>
            <Beat id="3"><Rhythm ref="2" /><Notes>3</Notes></Beat>
            <Beat id="4"><Rhythm ref="0" /><Notes>4</Notes></Beat>
            <Beat id="5"><Rhythm ref="0" /><Notes>5</Notes></Beat>
            <Beat id="6"><Rhythm ref="1" /><Notes>6</Notes></Beat>
            <Beat id="7"><Rhythm ref="1" /><Notes>7</Notes></Beat>
            """,
            Note(0, 5, 14) +
            Note(1, 5, 14, """<Property name="Slide"><Flags>2</Flags></Property>""") +
            Note(2, 5, 15) + Note(3, 5, 14) + Note(4, 5, 12) + Note(5, 5, 12) +
            Note(6, 5, 12) + Note(7, 5, 12),
            """
            <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="2"><NoteValue>16th</NoteValue><PrimaryTuplet num="3" den="2" /></Rhythm>
            """);

        var arr = ImportSingleTrack(gpif);
        var notes = arr.Levels[0].Notes.OrderBy(n => n.Position).ToList();

        // No two notes may share a position on the same string.
        var distinct = notes.Select(n => (n.StringIndex, Q: n.Position.BeatId + n.Position.Fraction.DoubleValue))
            .Distinct().Count();
        Assert.Equal(notes.Count, distinct);

        // The triplet group keeps its notated placement.
        AssertAt(notes[1], 0, new Fraction(1, 2));
        AssertAt(notes[2], 0, new Fraction(2, 3));
        AssertAt(notes[3], 0, new Fraction(5, 6));

        // ...and the legato slide resolves to the REAL next note (15), not to a
        // colliding same-fret duplicate (the E01 "slide ends on its own start
        // fret" regression).
        Assert.Equal(15, notes[1].SlideTo);

        // The off-beat eighth AFTER the tuplet still swings (1.5 → 1+2/3).
        AssertAt(notes[5], 1, new Fraction(2, 3));
    }

    /// <summary>A slide that resolves to its own fret is no slide at all — the
    /// resolver drops it (keeping any legato link) instead of emitting E01.</summary>
    [Fact]
    public void LinkSlides_SameFretResolutionDropsTheSlide()
    {
        var level = new ChartLevel();
        var first = new GuitarNote
        {
            Position = new FractionalPosition(0, Fraction.Zero),
            Sustain = new FractionalPosition(0, new Fraction(1, 2)),
            StringIndex = 5, Fret = 14, SlideTo = -1, LinkNext = true
        };
        var second = new GuitarNote
        {
            Position = new FractionalPosition(0, new Fraction(1, 2)),
            StringIndex = 5, Fret = 14
        };
        level.Notes.Add(first);
        level.Notes.Add(second);

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(first.SlideTo);
        Assert.True(first.LinkNext, "the legato link degrades to a plain linked note");
    }

    /// <summary>Consecutive hand shapes never overlap after the clamp pass.</summary>
    [Fact]
    public void ClampHandShapes_ClipsOverlapIntoTheNextShape()
    {
        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0, Fraction.Zero),
            EndPosition = new FractionalPosition(1, new Fraction(1, 6)),
            TemplateId = 0
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1, Fraction.Zero),
            EndPosition = new FractionalPosition(2, Fraction.Zero),
            TemplateId = 1
        });

        GpPostProcessor.ClampHandShapes(level);

        Assert.True(level.HandShapes[0].EndPosition.CompareTo(level.HandShapes[1].Position) == 0,
            "first shape must be clipped to the second's start");
    }

    /// <summary>An authored slide-out on an OPEN string (scrape/fall effect) has
    /// no fretted start — the note survives, the ornament is dropped (L09).</summary>
    [Fact]
    public void OpenStringSlideOut_DropsTheSlideKeepsTheNote()
    {
        var gpif = Envelope(
            "<MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar>",
            "0 1",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            """,
            Note(0, 5, 0,
                """<Property name="Slide"><Flags>8</Flags></Property><Property name="Muted" />""") +
            Note(1, 5, 12),
            """<Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm>""");

        var arr = ImportSingleTrack(gpif);
        var notes = arr.Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        var open = notes[0];
        Assert.Equal(0, open.Fret);
        Assert.Equal(MuteType.Full, open.Mute);
        Assert.False(open.UnpitchedSlide);
        Assert.Null(open.SlideTo);
    }
}
