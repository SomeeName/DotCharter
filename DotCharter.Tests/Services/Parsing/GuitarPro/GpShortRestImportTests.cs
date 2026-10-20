using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     <see cref="GpImportSettings.PreserveShortRests" /> — the two halves of
///     honouring a rest the chart model cannot hold.
///     <para>
///     Rocksmith drops a sub-quarter sustain and has no staccato mark, so an
///     eighth-note / eighth-rest / eighth-note riff imports as a run of bare
///     strikes with the articulation gone (owner report 2026-08-21, measures
///     40-41 of a real GP8 file at 220 bpm). The charting convention spells
///     staccato as an ACCENT, so that is what a qualifying note gets. CHORDS need
///     no accent: a hand-shape bar already carries duration, so a rest between
///     two identical strums BREAKS the shared bar instead of being covered by it
///     (measures 74+ of the same file, where one bar spanned the whole measure).
///     </para>
/// </summary>
public sealed class GpShortRestImportTests
{
    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gprest-{Guid.NewGuid():N}.gp");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Content/score.gpif");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(gpif);
        return path;
    }

    /// <summary>Rhythm refs: 0 = eighth, 1 = quarter, 2 = half.</summary>
    private static string Score(string beats, string beatIds, string notes) => $"""
        <GPIF>
          <Score><Title>Short rest fixture</Title></Score>
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
            <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="2"><NoteValue>Half</NoteValue></Rhythm>
          </Rhythms>
          <Notes>{notes}</Notes>
        </GPIF>
        """;

    private static string Note(int id, int stringIndex, int fret) => $"""
        <Note id="{id}"><Properties>
          <Property name="String"><String>{stringIndex}</String></Property>
          <Property name="Fret"><Fret>{fret}</Fret></Property>
        </Properties></Note>
        """;

    private static ChartLevel Import(string gpif, bool preserveShortRests)
    {
        var path = WriteGpFile(gpif);
        try
        {
            var settings = GpImportSettings.Default with { PreserveShortRests = preserveShortRests };
            var result = new GpImporter().ImportTracksWithMetadata(path, [0], settings);
            return Assert.Single(result.Arrangements).Levels[^1];
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>The reported riff shape: eighth, eighth REST, eighth, eighth.</summary>
    private static string StaccatoRiff() => Score(
        beats: """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            <Beat id="3"><Rhythm ref="0" /><Notes>2</Notes></Beat>
            """,
        beatIds: "0 1 2 3",
        notes: Note(0, 3, 11) + Note(1, 3, 9) + Note(2, 3, 10));

    [Fact]
    public void NoteFollowedByAShortRest_ImportsAccented()
    {
        var level = Import(StaccatoRiff(), preserveShortRests: true);
        var ordered = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.True(ordered[0].Accent, "the note before the rest is the staccato one");
    }

    [Fact]
    public void NotesWithNoRestAfterThem_AreNotAccented()
    {
        var level = Import(StaccatoRiff(), preserveShortRests: true);
        var ordered = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.False(ordered[1].Accent);
        Assert.False(ordered[2].Accent);
    }

    [Fact]
    public void TheRule_IsOff_WhenTheSettingIsOff()
    {
        var level = Import(StaccatoRiff(), preserveShortRests: false);
        Assert.All(level.Notes, n => Assert.False(n.Accent));
    }

    [Fact]
    public void ARestLongerThanTheNote_IsNotStaccato()
    {
        // An eighth followed by a HALF rest is a phrase ending, not an
        // articulation — the length rule is what tells them apart.
        var level = Import(Score(
            beats: """
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="2" /></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>1</Notes></Beat>
                """,
            beatIds: "0 1 2",
            notes: Note(0, 3, 11) + Note(1, 3, 9)), preserveShortRests: true);

        Assert.All(level.Notes, n => Assert.False(n.Accent));
    }

    [Fact]
    public void ANoteWithNothingSoundingAfterTheRest_IsNotStaccato()
    {
        // Trailing silence is the end of a phrase. Nothing follows to make the
        // gap read as an articulation.
        var level = Import(Score(
            beats: """
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /></Beat>
                """,
            beatIds: "0 1",
            notes: Note(0, 3, 11)), preserveShortRests: true);

        Assert.All(level.Notes, n => Assert.False(n.Accent));
    }

    [Fact]
    public void ANoteAtTheEndOfABar_IsStaccato_WhenItsRestFallsInTheNextBar()
    {
        // The owner's own example needs this: the last eighth of measure 40 is
        // articulated by a rest that lives in measure 41. A per-bar pass cannot
        // see it, which is why the rule defers its decision to the next SOUND.
        var gpif = """
            <GPIF>
              <Score><Title>Cross-bar staccato</Title></Score>
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
                <Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar>
                <Bar id="1"><Voices>1 -1 -1 -1</Voices></Bar>
              </Bars>
              <Voices>
                <Voice id="0"><Beats>0</Beats></Voice>
                <Voice id="1"><Beats>1 2</Beats></Voice>
              </Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /></Beat>
                <Beat id="2"><Rhythm ref="0" /><Notes>1</Notes></Beat>
              </Beats>
              <Rhythms>
                <Rhythm id="0"><NoteValue>Eighth</NoteValue></Rhythm>
              </Rhythms>
              <Notes>
            """ + Note(0, 3, 8) + Note(1, 3, 10) + """
              </Notes>
            </GPIF>
            """;

        var level = Import(gpif, preserveShortRests: true);
        var first = level.Notes.OrderBy(n => n.Position).First();
        Assert.True(first.Accent, "the rest in the NEXT bar still articulates it");
    }

    // ---- the chord half: a rest breaks the shared hand-shape bar -------------

    private static (ChartLevel Level, Arrangement Arr) TwoStrumsWithARestBetween()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();

        static Chord Strum(int beat, Fraction notated)
        {
            var chord = new Chord
            {
                Position = new FractionalPosition(beat), NotationDuration = notated
            };
            chord.ChordNotes.Add(new ChordNote
            { StringIndex = 4, Fret = 5, Sustain = new FractionalPosition(0, notated) });
            chord.ChordNotes.Add(new ChordNote
            { StringIndex = 3, Fret = 6, Sustain = new FractionalPosition(0, notated) });
            return chord;
        }

        // Quarter strum at beat 0 (ends at 1), an eighth of REST, then a strum
        // at 1 1/2 — the measure-74 figure.
        level.Chords.Add(Strum(0, new Fraction(1, 1)));
        var second = Strum(0, new Fraction(1, 2));
        second.Position = new FractionalPosition(1, new Fraction(1, 2));
        level.Chords.Add(second);
        return (level, arr);
    }

    [Fact]
    public void ARestBetweenIdenticalStrums_BreaksTheSharedHandShapeBar()
    {
        var (level, arr) = TwoStrumsWithARestBetween();
        ChordHandShapeBuilder.BuildAndTrim(level, arr, splitRunsOnRests: true);

        Assert.Equal(2, level.HandShapes.Count);
        var first = level.HandShapes.OrderBy(h => h.Position).First();
        Assert.Equal(new FractionalPosition(1), first.EndPosition);
    }

    [Fact]
    public void WithoutTheSetting_TheStrumsStillShareOneBarAcrossTheRest()
    {
        // The historical behaviour, pinned: the 2-beat merge gap spans the rest.
        var (level, arr) = TwoStrumsWithARestBetween();
        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var shape = Assert.Single(level.HandShapes);
        Assert.True(shape.EndPosition.CompareTo(new FractionalPosition(1)) > 0,
            "the single bar covers the rest and both strums");
    }

    [Fact]
    public void AbuttingStrums_StillShareOneBar_WhenTheSettingIsOn()
    {
        // No rest between them, so nothing to break: over-splitting is the
        // failure on the other side of this rule.
        var arr = new Arrangement();
        var level = new ChartLevel();
        foreach (var beat in new[] { 0, 1 })
        {
            var chord = new Chord
            { Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1) };
            chord.ChordNotes.Add(new ChordNote
            { StringIndex = 4, Fret = 5, Sustain = new FractionalPosition(1) });
            chord.ChordNotes.Add(new ChordNote
            { StringIndex = 3, Fret = 6, Sustain = new FractionalPosition(1) });
            level.Chords.Add(chord);
        }

        ChordHandShapeBuilder.BuildAndTrim(level, arr, splitRunsOnRests: true);
        Assert.Single(level.HandShapes);
    }
}
