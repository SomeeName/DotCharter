using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     A PLAIN (non-slide) grace before a CHORD used to be dropped outright:
///     <see cref="ChordNote"/> had no grace-hint field, so the ornament had
///     nowhere to go once the importer decided it could not become a real
///     sliding note (the slide case was recovered in 0.33.2). It now degrades to
///     the per-string <see cref="ChordNote.GraceFret"/> hint, exactly as a grace
///     before a single note degrades to <see cref="GuitarNote.GraceFret"/>.
/// </summary>
public sealed class GpChordGraceHintTests
{
    // ---- gpif fixture plumbing (same shape as GpSlideRecoveryTests) --------

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpgrace-{Guid.NewGuid():N}.gp");
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

    private static string Envelope(string voiceBeats, string beats, string notes, string rhythms) => $"""
        <GPIF>
          <Score><Title>Chord Grace Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Guitar</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                <Property name="CapoFret"><Fret>0</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>{voiceBeats}</Beats></Voice></Voices>
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

    private static string Slide(int flags) => $"""<Property name="Slide"><Flags>{flags}</Flags></Property>""";

    private const string QuarterAnd32nd =
        """<Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>""" +
        """<Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>""";

    // ---- the ornament -----------------------------------------------------

    /// <summary>
    ///     A 32nd grace CHORD with NO slide, ahead of a quarter chord: nothing can
    ///     be materialized (a plain ornament is not a playable sound), so every
    ///     graced string keeps its fret as a notation hint and the target chord
    ///     stays exactly where it was authored. Before the fix the whole ornament
    ///     was silently discarded.
    /// </summary>
    [Fact]
    public void PlainGraceBeforeAChord_BecomesAPerStringHint()
    {
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="1" /><Notes>0 1</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>2 3</Notes></Beat>
            """,
            Note(0, 1, 3) + Note(1, 2, 5) + Note(2, 1, 5) + Note(3, 2, 7),
            QuarterAnd32nd);

        var level = ImportSingleTrack(gpif).Levels[0];
        var chord = Assert.Single(level.Chords);

        // No ornament note/chord was inserted, and the target did not move.
        Assert.Empty(level.Notes);
        Assert.Equal(FractionalPosition.Zero, chord.Position);

        // Each graced string carries its own hint (identified by the landing
        // fret so the test does not depend on GP's string numbering).
        Assert.Equal(3, chord.ChordNotes.Single(cn => cn.Fret == 5).GraceFret);
        Assert.Equal(5, chord.ChordNotes.Single(cn => cn.Fret == 7).GraceFret);
    }

    /// <summary>
    ///     A grace beat may mix flavours: the "sl." strings materialize into a real
    ///     origin chord that slides in, while the plain string keeps its hint. The
    ///     two paths must not cancel each other out.
    /// </summary>
    [Fact]
    public void MixedGrace_SlidesOnOneStringAndHintsOnTheOther()
    {
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><GraceNotes>OnBeat</GraceNotes><Rhythm ref="1" /><Notes>0 1</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>2 3</Notes></Beat>
            """,
            Note(0, 1, 3, Slide(2)) + Note(1, 2, 5) + Note(2, 1, 5) + Note(3, 2, 7),
            QuarterAnd32nd);

        var level = ImportSingleTrack(gpif).Levels[0];
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count);

        // The sliding string became a real origin chord...
        var origin = chords[0];
        var slid = Assert.Single(origin.ChordNotes);
        Assert.Equal(3, slid.Fret);
        Assert.Equal(5, slid.SlideTo);
        Assert.True(slid.LinkNext);
        Assert.Null(slid.GraceFret); // consumed — it is a real sound now

        // ...and the plain string kept its hint on the landing chord, which is
        // itself pushed past the ornament by the on-beat grace.
        var landing = chords[1];
        Assert.True(landing.Position.CompareTo(origin.Position) > 0);
        Assert.Null(landing.ChordNotes.Single(cn => cn.Fret == 5).GraceFret);
        Assert.Equal(5, landing.ChordNotes.Single(cn => cn.Fret == 7).GraceFret);
    }

    /// <summary>A chord with no grace beat ahead of it gets no hints — the field
    /// stays null for every chart that never used the feature.</summary>
    [Fact]
    public void ChordWithoutAGrace_HasNoHints()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>""",
            Note(0, 1, 5) + Note(1, 2, 7),
            QuarterAnd32nd);

        var level = ImportSingleTrack(gpif).Levels[0];
        Assert.All(Assert.Single(level.Chords).ChordNotes, cn => Assert.Null(cn.GraceFret));
    }
}
