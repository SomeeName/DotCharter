using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #471, end to end through the REAL importers: a dead ("x") note inside a
///     chord that still has fretted notes is dropped before chord templates,
///     hand shapes and fingering are derived, techniques on the remaining notes
///     survive, a chord left with one note becomes a single note, and an all-x
///     chord is kept as it is. The shapes are the owner's tab (measures 45-46):
///     <c>5/x/2</c> and <c>10/x/7</c> sliding into <c>12/x/9</c>.
///     <para>
///     The rule is <see cref="GpPostProcessor.CleanDeadChordNotes" />, shared by
///     the gpif, GP3-5 binary and Songsterr paths, so the gpif path is pinned in
///     full and the binary path once.
///     </para>
/// </summary>
public sealed class GpDeadChordNoteImportTests
{
    // ── gpif fixture ────────────────────────────────────────────────────────

    private static string Score(string beats, string beatIds, string notes) => $"""
        <GPIF>
          <Score><Title>Dead chord note fixture</Title></Score>
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
            <Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm>
          </Rhythms>
          <Notes>{notes}</Notes>
        </GPIF>
        """;

    private static string Beat(int id, params int[] noteIds) =>
        $"""<Beat id="{id}"><Rhythm ref="0" /><Notes>{string.Join(' ', noteIds)}</Notes></Beat>""";

    private static string Note(int id, int stringIndex, int fret, bool dead = false, int? slideFlags = null)
    {
        var muted = dead ? """<Property name="Muted"><Enable /></Property>""" : "";
        var slide = slideFlags is { } flags ? $"""<Property name="Slide"><Flags>{flags}</Flags></Property>""" : "";
        return $"""
            <Note id="{id}"><Properties>
              <Property name="String"><String>{stringIndex}</String></Property>
              <Property name="Fret"><Fret>{fret}</Fret></Property>
              {muted}{slide}
            </Properties></Note>
            """;
    }

    private static Arrangement ImportGpif(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpdead-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Content/score.gpif");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(gpif);
        }

        try
        {
            return Assert.Single(new GpImporter().ImportTracksWithMetadata(path, [0]).Arrangements);
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    private static ChordTemplate TemplateOf(Arrangement arr, Chord chord) =>
        Assert.Single(arr.ChordTemplates, t => t.Id == chord.TemplateId);

    // ── the rules ───────────────────────────────────────────────────────────

    [Fact]
    public void FiveXTwo_ImportsAsTheDoubleStopFiveTwo()
    {
        var arr = ImportGpif(Score(
            beats: Beat(0, 0, 1, 2),
            beatIds: "0",
            notes: Note(0, 5, 5) + Note(1, 4, 0, dead: true) + Note(2, 3, 2)));
        var level = arr.Levels[^1];

        var chord = Assert.Single(level.Chords);
        Assert.Empty(level.Notes);
        Assert.Equal([(5, 5), (3, 2)], chord.ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToArray());
        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute));

        // The template and the hand shape come from the remaining notes: no x string.
        var template = TemplateOf(arr, chord);
        Assert.Equal(new Dictionary<int, int> { [5] = 5, [3] = 2 }, template.Frets);
        Assert.DoesNotContain(4, template.Fingers.Keys);
        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(chord.TemplateId, shape.TemplateId);
    }

    [Fact]
    public void SlidingChordWithAnXString_KeepsBothSlides_IntoTheTargetChord()
    {
        // 10/x/7 shift-slides into 12/x/9.
        var arr = ImportGpif(Score(
            beats: Beat(0, 0, 1, 2) + Beat(1, 3, 4, 5),
            beatIds: "0 1",
            notes: Note(0, 5, 10, slideFlags: 1) + Note(1, 4, 0, dead: true) + Note(2, 3, 7, slideFlags: 1) +
                   Note(3, 5, 12) + Note(4, 4, 0, dead: true) + Note(5, 3, 9)));
        var level = arr.Levels[^1];

        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count);
        Assert.Empty(level.Notes);

        Assert.Equal([(5, 10, 12), (3, 7, 9)],
            chords[0].ChordNotes.Select(n => (n.StringIndex, n.Fret, n.SlideTo)).ToArray());
        Assert.Equal([(5, 12), (3, 9)], chords[1].ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToArray());
        Assert.All(chords.SelectMany(c => c.ChordNotes), n => Assert.Equal(MuteType.None, n.Mute));
        Assert.All(chords, c => Assert.DoesNotContain(4, TemplateOf(arr, c).Frets.Keys));
    }

    [Fact]
    public void AllXChord_IsKeptAsItIs()
    {
        var arr = ImportGpif(Score(
            beats: Beat(0, 0, 1),
            beatIds: "0",
            notes: Note(0, 1, 5, dead: true) + Note(1, 0, 5, dead: true)));
        var level = arr.Levels[^1];

        var chord = Assert.Single(level.Chords);
        Assert.Equal([(1, 5), (0, 5)], chord.ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToArray());
        Assert.True(chord.IsFullyMuted());
    }

    [Fact]
    public void TwoNoteChordWithOneX_BecomesASingleNote()
    {
        var arr = ImportGpif(Score(
            beats: Beat(0, 0, 1),
            beatIds: "0",
            notes: Note(0, 5, 5, slideFlags: 1) + Note(1, 4, 3, dead: true)));
        var level = arr.Levels[^1];

        Assert.Empty(level.Chords);
        Assert.Empty(level.HandShapes);
        Assert.Empty(arr.ChordTemplates);
        var note = Assert.Single(level.Notes);
        Assert.Equal((5, 5, MuteType.None), (note.StringIndex, note.Fret, note.Mute));
        Assert.Equal(FractionalPosition.Zero, note.Position);
    }

    [Fact]
    public void PalmMutedNotes_AreNotDeadNotes_AndStay()
    {
        var palm = """<Property name="PalmMuted"><Enable /></Property>""";
        var arr = ImportGpif(Score(
            beats: Beat(0, 0, 1, 2),
            beatIds: "0",
            notes: Note(0, 5, 5) + Note(1, 4, 7).Replace("</Properties>", palm + "</Properties>") +
                   Note(2, 3, 2)));

        var chord = Assert.Single(arr.Levels[^1].Chords);
        Assert.Equal(3, chord.ChordNotes.Count);
    }

    // ── the binary GP3-5 path takes the same pass ───────────────────────────

    [Fact]
    public void Gp5_FiveXTwo_ImportsAsTheDoubleStop_AndAOneNoteLeftoverAsANote()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(-1, new FxNote(0, 5), new FxNote(1, 0, Dead: true), new FxNote(2, 2)),
            FxBeat.Chord(-1, new FxNote(0, 7), new FxNote(1, 3, Dead: true)));
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", [measure]);
        Arrangement arr;
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path).Select(t => t.Id).ToList();
            arr = Assert.Single(importer.ImportTracksWithMetadata(path, tracks).Arrangements);
        }
        finally
        {
            BestEffortDelete.File(path);
        }

        var level = arr.Levels[^1];
        var chord = Assert.Single(level.Chords);
        Assert.Equal([2, 5], chord.ChordNotes.Select(n => n.Fret).Order().ToArray());
        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute));
        Assert.Equal(2, TemplateOf(arr, chord).Frets.Count);

        var note = Assert.Single(level.Notes);
        Assert.Equal(7, note.Fret);
    }
}
