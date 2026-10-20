using System.Xml.Linq;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A chord marked "Split notes" (<see cref="Chord.SplitIntoNotes" />) ships as single
///     notes, so every export decision counts it as those notes (#621), as EOF does:
///     the chord template list (a template only split chords play is not written, and
///     every later <c>chordId</c> moves down with it — XML and SNG alike), the repeat
///     decision (the chord after a split chord is judged against notes) and the
///     arrangement properties (its strings' techniques count, its shape does not).
/// </summary>
public sealed class SplitChordsAsNotesTests
{
    // Three-string shapes: a two-string shape may ship unnamed (the double-stop
    // name strip), and the names are how these tests tell the templates apart.
    private static readonly (int String, int Fret)[] A5 = [(0, 5), (1, 7), (2, 7)];
    private static readonly (int String, int Fret)[] Scrape = [(1, 9), (3, 11)];
    private static readonly (int String, int Fret)[] E5 = [(1, 7), (2, 9), (3, 9)];

    private static FractionalPosition At(int beat) => new(beat);

    /// <summary>A Lead arrangement on a 120 BPM grid declaring A5 (id 0), a nameless
    /// scrape shape (id 1) and E5 (id 2), with one level for <paramref name="build" />.</summary>
    private static ChartProject Project(Action<ChartLevel> build)
    {
        var project = new ChartProject();
        foreach (var i in Enumerable.Range(0, 64))
            project.Beats.Add(new BeatMarker { Time = 1000 + i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.ChordTemplates.Add(Template(0, "A5", A5));
        arrangement.ChordTemplates.Add(Template(1, "", Scrape));
        arrangement.ChordTemplates.Add(Template(2, "E5", E5));

        var level = new ChartLevel();
        build(level);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static ChordTemplate Template(int id, string name, (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        return template;
    }

    private static Chord Strum(int beat, int templateId, (int String, int Fret)[] frets, bool split = false)
    {
        var chord = new Chord { Position = At(beat), TemplateId = templateId, SplitIntoNotes = split };
        foreach (var (s, f) in frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    private static XDocument Xml(ChartProject project) => new RsXmlSongWriter().Write(project);

    private static RocksmithSngLevel Sng(ChartProject project)
    {
        var arrangement = project.Arrangements[0];
        PhraseSectionGenerator.GenerateIfAbsent(arrangement, project.Beats, 0.0);
        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 0.0).Sng;
        return new RocksmithSngLevel(sng.Chords.Select(t => t.Name).ToList(),
            sng.Levels[^1].Notes.Where(n => n.ChordId >= 0).Select(n => n.ChordId).ToList(),
            sng.Levels[^1].HandShapes.Select(h => h.ChordId).ToList());
    }

    private sealed record RocksmithSngLevel(List<string> Templates, List<int> ChordIds, List<int> HandShapeIds);

    // ── the chord template list ─────────────────────────────────────────────

    /// <summary>
    ///     A5 strum, a split scrape on template 1, an E5 strum under an E5 hand shape:
    ///     template 1 is written nowhere, and E5 — template 2 in the project — is index
    ///     1 in the file, for the chord AND the hand shape, in the XML AND the SNG.
    /// </summary>
    [Fact]
    public void ATemplateOnlySplitChordsPlay_IsNotWritten_AndTheLaterIdsMoveDown()
    {
        var project = Project(level =>
        {
            level.Chords.Add(Strum(1, 0, A5));
            level.Chords.Add(Strum(2, 1, Scrape, split: true));
            level.Chords.Add(Strum(3, 2, E5));
            level.HandShapes.Add(new HandShape { Position = At(3), EndPosition = At(4), TemplateId = 2 });
        });

        var xml = Xml(project);
        Assert.Equal(["A5", "E5"], xml.Descendants("chordTemplate").Select(t => (string?)t.Attribute("chordName")));
        Assert.Equal(["0", "1"], xml.Descendants("chord").Select(c => (string?)c.Attribute("chordId")));
        Assert.Equal("1", (string?)Assert.Single(xml.Descendants("handShape")).Attribute("chordId"));
        Assert.Equal(2, xml.Descendants("note").Count()); // the scrape, one per string

        var sng = Sng(project);
        Assert.Equal(["A5", "E5"], sng.Templates);
        Assert.Equal([0, 1], sng.ChordIds);
        Assert.Equal([1], sng.HandShapeIds);
    }

    /// <summary>A template a split chord SHARES — with a strum, or with a hand shape —
    /// is still written; so is one nothing plays, as before.</summary>
    [Fact]
    public void ATemplateSomethingElseNames_IsStillWritten()
    {
        var project = Project(level =>
        {
            level.Chords.Add(Strum(1, 0, A5));
            level.Chords.Add(Strum(2, 0, A5, split: true));
            level.Chords.Add(Strum(4, 2, E5, split: true));
            level.HandShapes.Add(new HandShape { Position = At(4), EndPosition = At(5), TemplateId = 2 });
        });

        Assert.Equal(["A5", "", "E5"],
            Xml(project).Descendants("chordTemplate").Select(t => (string?)t.Attribute("chordName")));
        Assert.Equal(["A5", "", "E5"], Sng(project).Templates);
    }

    /// <summary>A split chord whose template is gone is not given a synthesized one —
    /// it ships as notes and names no template.</summary>
    [Fact]
    public void ASplitChordWithNoLiveTemplate_GetsNoSynthesizedTemplate()
    {
        var project = Project(level =>
        {
            level.Chords.Add(Strum(1, 0, A5));
            level.Chords.Add(Strum(2, 99, [(3, 4), (4, 4)], split: true));
        });

        Assert.Equal(3, Xml(project).Descendants("chordTemplate").Count());
        Assert.Equal(3, Sng(project).Templates.Count);
    }

    // ── the repeat decision ─────────────────────────────────────────────────

    /// <summary>
    ///     A5, a SPLIT A5, A5: the game sees a strum, two notes, a strum — so the third
    ///     strum follows a note and keeps its full panel. Judged as a chord, the split
    ///     A5 made it a repeat box.
    /// </summary>
    [Fact]
    public void AStrumAfterASplitChord_IsJudgedAgainstItsNotes()
    {
        var project = Project(level =>
        {
            level.Chords.Add(Strum(1, 0, A5));
            level.Chords.Add(Strum(2, 0, A5, split: true));
            level.Chords.Add(Strum(3, 0, A5));
        });
        var level = project.Arrangements[0].Levels[0];

        var verdicts = RepeatChords.Explain(project.Arrangements[0].ChordTemplates, level.Notes, level.Chords,
            level.HandShapes, project.Beats);

        Assert.Equal(RepeatChordReason.SplitIntoNotes, verdicts[level.Chords[1]].Reason);
        Assert.Equal(RepeatChordReason.PreviousSoundIsNote, verdicts[level.Chords[2]].Reason);
        Assert.All(Xml(project).Descendants("chord"), c => Assert.NotEmpty(c.Elements("chordNote")));
    }

    /// <summary>
    ///     Inside one A5 hand shape: A5, a split scrape, A5. The nearest earlier CHORD
    ///     in the shape is the first A5 — the scrape is notes — so the second A5 ships
    ///     as the repeat box. Judged as a chord, the scrape gave it a different
    ///     template and a full panel.
    /// </summary>
    [Fact]
    public void InsideAHandShape_ASplitChordIsNotTheChordBefore()
    {
        var project = Project(level =>
        {
            level.HandShapes.Add(new HandShape { Position = At(1), EndPosition = At(5), TemplateId = 0 });
            level.Chords.Add(Strum(1, 0, A5));
            level.Chords.Add(Strum(2, 1, Scrape, split: true));
            level.Chords.Add(Strum(3, 0, A5));
        });
        var level = project.Arrangements[0].Levels[0];

        var verdicts = RepeatChords.Explain(project.Arrangements[0].ChordTemplates, level.Notes, level.Chords,
            level.HandShapes, project.Beats);

        Assert.Equal(RepeatChordReason.Repeat, verdicts[level.Chords[2]].Reason);
        var written = Xml(project).Descendants("chord").ToList();
        Assert.Equal(2, written.Count);
        Assert.Empty(written[1].Elements("chordNote"));
    }

    // ── the arrangement properties ──────────────────────────────────────────

    /// <summary>
    ///     A split A5 power chord on adjacent strings, with tremolo: tremolo counts
    ///     (the notes carry it), the shape does not — no <c>doubleStops</c>,
    ///     <c>powerChords</c> or <c>nonStandardChords</c>. The same chord unsplit
    ///     counts all of them.
    /// </summary>
    [Fact]
    public void ASplitChord_CountsItsTechniques_NotItsShape()
    {
        Chord chord = null!;
        var project = Project(level =>
        {
            chord = Strum(1, 1, [(0, 5), (1, 7)], split: true);
            foreach (var note in chord.ChordNotes) note.Tremolo = true;
            level.Chords.Add(chord);
        });
        var arrangement = project.Arrangements[0];

        var split = ArrangementPropertiesDeriver.Derive(arrangement);
        chord.SplitIntoNotes = false;
        var strummed = ArrangementPropertiesDeriver.Derive(arrangement);

        Assert.Equal((1, 0, 0, 0), (split.Tremolo, split.DoubleStops, split.PowerChords, split.NonStandardChords));
        Assert.Equal((1, 1, 1, 1), (strummed.Tremolo, strummed.DoubleStops, strummed.PowerChords, strummed.NonStandardChords));
    }
}
