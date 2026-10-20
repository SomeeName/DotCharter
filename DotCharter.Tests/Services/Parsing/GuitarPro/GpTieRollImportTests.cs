using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro spells a ROLLED chord as growing TIED chords: every beat
///     repeats each already-ringing string as a tie DESTINATION and adds one new
///     one. Imported literally that is a stack of ever-bigger chords for a
///     gesture the player performs as single strikes that ring together.
///     <para>
///     Provenance: owner report 2026-08-21, measure 2 of a real GP8 file
///     ("Yorushika - Hachigatsu Bou Tsukiakari.gp"). Four 32nds tied into a
///     final chord produced a note, a 2-note chord, a 3-note chord and a 4-note
///     chord, three junk chord templates and two zero-width hand shapes — two
///     beats before a properly-tabbed let-ring run of the same shape that
///     imported correctly as one bar. The fixture below is that bar's shape.
///     </para>
/// </summary>
public sealed class GpTieRollImportTests
{
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gproll-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    private static string Score(string notes, string beats, string beatIds) => $"""
        <GPIF>
          <Score><Title>Tie roll fixture</Title></Score>
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
          </MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>{beatIds}</Beats></Voice></Voices>
          <Beats>{beats}</Beats>
          <Rhythms>
            <Rhythm id="0"><NoteValue>32nd</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
          </Rhythms>
          <Notes>{notes}</Notes>
        </GPIF>
        """;

    private static string Note(int id, int stringIndex, int fret, bool origin, bool destination)
    {
        var tie = origin || destination
            ? $"""<Tie origin="{(origin ? "true" : "false")}" destination="{(destination ? "true" : "false")}" />"""
            : "";
        return $"""
            <Note id="{id}">{tie}<Properties>
              <Property name="String"><String>{stringIndex}</String></Property>
              <Property name="Fret"><Fret>{fret}</Fret></Property>
            </Properties></Note>
            """;
    }

    private static Arrangement Import(string gpif)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var result = new GpImporter().ImportTracksWithMetadata(path, [0]);
            return Assert.Single(result.Arrangements);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>The reported bar: three 32nds each adding a string, all tied,
    /// resolving into an all-destination quarter.</summary>
    private static string RollScore() => Score(
        notes:
        Note(0, 1, 2, origin: true, destination: false) +
        Note(1, 2, 4, origin: true, destination: false) +
        Note(2, 1, 2, origin: true, destination: true) +
        Note(3, 3, 2, origin: true, destination: false) +
        Note(4, 2, 4, origin: true, destination: true) +
        Note(5, 1, 2, origin: true, destination: true) +
        Note(6, 1, 2, origin: false, destination: true) +
        Note(7, 2, 4, origin: false, destination: true) +
        Note(8, 3, 2, origin: false, destination: true),
        beats: """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1 2</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>3 4 5</Notes></Beat>
            <Beat id="3"><Rhythm ref="1" /><Notes>6 7 8</Notes></Beat>
            """,
        beatIds: "0 1 2 3");

    [Fact]
    public void RolledChord_ImportsAsSingleStrikes_NotGrowingChords()
    {
        var level = Import(RollScore()).Levels[^1];

        Assert.Empty(level.Chords);
        Assert.Equal(3, level.Notes.Count);
        var ordered = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, ordered.Select(n => n.StringIndex).ToArray());
        Assert.Equal(new[] { 2, 4, 2 }, ordered.Select(n => n.Fret).ToArray());
    }

    [Fact]
    public void RolledChord_LeavesNoTails_AndNoDanglingTieLinks()
    {
        var level = Import(RollScore()).Levels[^1];

        Assert.All(level.Notes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));
        // Every tie destination is gone, so no origin may still claim one.
        Assert.All(level.Notes, n => Assert.False(n.LinkNext));
    }

    [Fact]
    public void RolledChord_BecomesOneHandShapeOverTheWholeRun()
    {
        var arr = Import(RollScore());
        var level = arr.Levels[^1];

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(level.Notes.Min(n => n.Position), shape.Position);
        var template = arr.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [1] = 2, [2] = 4, [3] = 2 }, template.Frets);
    }

    [Fact]
    public void RolledChord_DoesNotLeaveAJunkTemplatePerPartialStack()
    {
        // The growing stacks used to create one find-or-create template each.
        var arr = Import(RollScore());
        Assert.Single(arr.ChordTemplates);
    }

    [Fact]
    public void RolledChord_RingsOutForItsAuthoredTiedLength()
    {
        // The all-destination beat is consumed, but its rhythm is added to the
        // notation duration of every string still ringing — which is what gives
        // the bar its true end. Last strike at 1/4 + (1/8 own + 1 tied) = 1 3/8.
        var level = Import(RollScore()).Levels[^1];
        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(1, new Fraction(3, 8)), shape.EndPosition);
    }

    [Fact]
    public void AnAllDestinationChord_WithNoRollBeforeIt_IsStillAnOrdinaryTiedChord()
    {
        // The guard the roll rule must not cost us: a plain chord tied to an
        // identical chord is MergeTiedNotes' business, and must stay one CHORD
        // rather than being collapsed to strikes. (Real shape: measures 74+ of
        // the same file, where a strummed chord is tied across a rest.)
        var level = Import(Score(
            notes:
            Note(0, 1, 2, origin: true, destination: false) +
            Note(1, 2, 4, origin: true, destination: false) +
            Note(2, 1, 2, origin: false, destination: true) +
            Note(3, 2, 4, origin: false, destination: true),
            beats: """
                <Beat id="0"><Rhythm ref="1" /><Notes>0 1</Notes></Beat>
                <Beat id="1"><Rhythm ref="1" /><Notes>2 3</Notes></Beat>
                """,
            beatIds: "0 1")).Levels[^1];

        Assert.Empty(level.Notes);
        var chord = Assert.Single(level.Chords);
        Assert.Equal(2, chord.ChordNotes.Count);
    }
}
