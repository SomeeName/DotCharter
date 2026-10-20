using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// The auto-handshape import pass (<see cref="ChordHandShapeBuilder"/>) shared by
/// every tab importer: each imported chord gets a HandShape spanning its authored
/// duration and its own sustain is trimmed to a short strike — UNLESS a
/// sustain-visible technique exempts it. The exemption lives in one named
/// predicate; all four cases from the spec are pinned here.
/// </summary>
public sealed class ChordHandShapeBuilderTests
{
    private static Arrangement Arr()
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        return arr;
    }

    private static Chord Chord(int beat, Fraction notationDuration, params ChordNote[] notes)
    {
        var chord = new Chord
        {
            Position = new FractionalPosition(beat),
            NotationDuration = notationDuration
        };
        var sustain = new FractionalPosition(0, notationDuration);
        foreach (var n in notes)
        {
            n.Sustain = sustain;
            chord.ChordNotes.Add(n);
        }

        return chord;
    }

    private static ChordNote Cn(int stringIndex, int fret) => new() { StringIndex = stringIndex, Fret = fret };

    // ---- Minted template ids (the carried next-id must equal the old rescan) ----

    [Fact]
    public void MintedTemplateIds_ContinueFromTheHIGHEST_ExistingId_NotTheCount()
    {
        // The mint used to recompute `Max(t => t.Id) + 1` for every template it
        // created. The carried counter has to give the same answer on a SPARSE
        // id list — where the count and the max disagree — or a re-import
        // silently reuses an id another chord is already bound to.
        var arr = Arr();
        arr.ChordTemplates.Add(new ChordTemplate { Id = 4, Name = "prior" });
        arr.ChordTemplates.Add(new ChordTemplate { Id = 11, Name = "prior" });

        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        level.Chords.Add(Chord(4, new Fraction(1, 1), Cn(0, 7), Cn(1, 9)));
        level.Chords.Add(Chord(8, new Fraction(1, 1), Cn(2, 2), Cn(3, 4)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var minted = arr.ChordTemplates.Where(t => t.Name != "prior").Select(t => t.Id).ToList();
        Assert.Equal([12, 13, 14], minted);
        Assert.Equal(minted.Count, minted.Distinct().Count());
    }

    [Fact]
    public void MintedTemplateIds_StartAtZero_OnAnEmptyTemplateList()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        level.Chords.Add(Chord(4, new Fraction(1, 1), Cn(0, 7), Cn(1, 9)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.Equal([0, 1], arr.ChordTemplates.Select(t => t.Id));
    }

    // ---- The exemption predicate (one named place) ----

    [Fact]
    public void HasSustainVisibleTechnique_False_ForPlainChord_AndForHammerOn()
    {
        var plain = Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        Assert.False(ChordHandShapeBuilder.HasSustainVisibleTechnique(plain));

        var hammer = Chord(0, new Fraction(1, 1),
            new ChordNote { StringIndex = 0, Fret = 3, Hopo = HopoType.HammerOn },
            new ChordNote { StringIndex = 1, Fret = 5, Hopo = HopoType.Tap });
        Assert.False(ChordHandShapeBuilder.HasSustainVisibleTechnique(hammer));
    }

    [Fact]
    public void HasSustainVisibleTechnique_True_ForBendTremoloVibratoSlide()
    {
        var bend = Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        bend.ChordNotes[0].BendValues.Add(new BendValue(new FractionalPosition(0), 1.0));
        Assert.True(ChordHandShapeBuilder.HasSustainVisibleTechnique(bend));

        var tremolo = Chord(0, new Fraction(1, 1), new ChordNote { StringIndex = 0, Fret = 3, Tremolo = true });
        Assert.True(ChordHandShapeBuilder.HasSustainVisibleTechnique(tremolo));

        var vibrato = Chord(0, new Fraction(1, 1), new ChordNote { StringIndex = 0, Fret = 3, Vibrato = true });
        Assert.True(ChordHandShapeBuilder.HasSustainVisibleTechnique(vibrato));

        var pitchedSlide = Chord(0, new Fraction(1, 1), new ChordNote { StringIndex = 0, Fret = 3, SlideTo = 5 });
        Assert.True(ChordHandShapeBuilder.HasSustainVisibleTechnique(pitchedSlide));

        var whammy = Chord(0, new Fraction(1, 1), new ChordNote { StringIndex = 0, Fret = 3, UnpitchedSlide = true });
        Assert.True(ChordHandShapeBuilder.HasSustainVisibleTechnique(whammy));
    }

    // ---- Trim + handshape behavior (the four spec cases) ----

    [Fact]
    public void PlainChord_Trimmed_AndGetsHandshapeOverAuthoredDuration()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(2, 1), Cn(0, 3), Cn(1, 5))); // half note

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain)); // strike

        var hs = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(0), hs.Position);
        Assert.Equal(new FractionalPosition(2), hs.EndPosition); // spans the half note
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(template.Id, hs.TemplateId);
        Assert.Equal(3, template.Frets[0]);
        Assert.Equal(5, template.Frets[1]);
        // New templates arrive charting-ready (v0.26): suggested fingering
        // (power-chord shape → index + ring). The NAME is deliberately blank since
        // 0.41 — this is a two-string shape, and two pitches do not determine a
        // chord, so the automatic namer leaves it to the charter.
        Assert.Equal(1, template.Fingers[0]);
        Assert.Equal(3, template.Fingers[1]);
        Assert.Equal("", template.Name);
    }

    [Fact]
    public void FindOrCreate_ReusesHandAuthoredTemplateWithSameFrets_WithoutMutatingIt()
    {
        var arr = Arr();
        // A hand-authored template for the same shape, with deliberate name and
        // fingering — the import pass must reuse it verbatim, never mutate it,
        // and never mint a duplicate.
        var authored = new ChordTemplate { Id = 0, Name = "MyChord" };
        authored.Frets[0] = 3;
        authored.Frets[1] = 5;
        authored.Fingers[0] = 2;
        authored.Fingers[1] = 4;
        arr.ChordTemplates.Add(authored);

        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var template = Assert.Single(arr.ChordTemplates);
        Assert.Same(authored, template);
        Assert.Equal("MyChord", template.Name);
        Assert.Equal(2, template.Fingers[0]);
        Assert.Equal(4, template.Fingers[1]);
        Assert.Equal(template.Id, Assert.Single(level.HandShapes).TemplateId);
    }

    [Fact]
    public void HammerOnChord_Trimmed_AndGetsHandshape()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1),
            new ChordNote { StringIndex = 0, Fret = 3, Hopo = HopoType.HammerOn },
            new ChordNote { StringIndex = 1, Fret = 5, Hopo = HopoType.HammerOn }));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.All(level.Chords[0].ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void BendChord_SustainKept_AndStillGetsHandshape()
    {
        var arr = Arr();
        var level = new ChartLevel();
        var chord = Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        chord.ChordNotes[0].BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1.0));
        level.Chords.Add(chord);

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        // Sustain kept (bend is only visible across it); handshape still created.
        Assert.All(level.Chords[0].ChordNotes, n => Assert.Equal(new FractionalPosition(1), n.Sustain));
        Assert.Single(level.HandShapes);
    }

    [Fact]
    public void TremoloChord_SustainKept_AndStillGetsHandshape()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1),
            new ChordNote { StringIndex = 0, Fret = 3, Tremolo = true },
            new ChordNote { StringIndex = 1, Fret = 5, Tremolo = true }));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.All(level.Chords[0].ChordNotes, n => Assert.Equal(new FractionalPosition(1), n.Sustain));
        Assert.Single(level.HandShapes);
    }

    // ---- Template find-or-create + idempotency ----

    [Fact]
    public void RepeatedIdenticalChords_MergeIntoOneHandShape_DifferentShapeBreaksTheRun()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        level.Chords.Add(Chord(1, new Fraction(1, 1), Cn(0, 3), Cn(1, 5))); // same shape → merges
        level.Chords.Add(Chord(2, new Fraction(1, 1), Cn(0, 5), Cn(1, 7))); // different shape → own bar

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.Equal(2, arr.ChordTemplates.Count); // find-or-create deduped the repeat
        // RS convention: the repeated strums share ONE handshape bar spanning
        // both chords; the different shape gets its own.
        Assert.Equal(2, level.HandShapes.Count);
        Assert.Equal(new FractionalPosition(0), level.HandShapes[0].Position);
        Assert.Equal(new FractionalPosition(2), level.HandShapes[0].EndPosition);
        Assert.Equal(new FractionalPosition(2), level.HandShapes[1].Position);
        Assert.Equal(new FractionalPosition(3), level.HandShapes[1].EndPosition);
        Assert.NotEqual(level.HandShapes[0].TemplateId, level.HandShapes[1].TemplateId);
    }

    [Fact]
    public void RepeatedChords_BeyondTheMergeGap_GetSeparateHandShapes()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        // Same shape but 4 beats after the first chord's end — past the 2-beat gap.
        level.Chords.Add(Chord(5, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(2, level.HandShapes.Count);
    }

    [Fact]
    public void SingleNoteBetweenRepeatedChords_BreaksTheMerge()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        level.Chords.Add(Chord(2, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));
        // A melodic fill between the strums — the run must not swallow it.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 2, Fret = 7
        });

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.Equal(2, level.HandShapes.Count);
    }

    [Fact]
    public void LongSustainBendChord_DoesNotSwallowTheFollowingDifferentChords()
    {
        // A bend chord {12,12,14} keeps its long authored sustain (technique-
        // exempt), so its handshape used to span the whole sustain and COVER
        // the plain {12,12} double-stops that strike during it. Those chords
        // were then suppressed (HandShapeCoversPosition) and inherited the 3-note
        // bend template — selecting the bar added a spurious 14 to them (owner
        // report 2026-07-19). The bend handshape must clip at the next chord so
        // each distinct shape keeps its own handshape + template.
        var arr = Arr();
        var level = new ChartLevel();
        var bend = Chord(0, new Fraction(4, 1), Cn(0, 12), Cn(1, 12), Cn(2, 14)); // whole note
        bend.ChordNotes[2].BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1.0));
        level.Chords.Add(bend);
        level.Chords.Add(Chord(1, new Fraction(1, 1), Cn(0, 12), Cn(1, 12))); // double-stop during the sustain
        level.Chords.Add(Chord(2, new Fraction(1, 1), Cn(0, 12), Cn(1, 12)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        // Each distinct shape gets its OWN handshape; the bend bar clips at the
        // first double-stop (beat 1) instead of spanning to beat 4.
        Assert.Equal(2, level.HandShapes.Count);
        var bendHs = level.HandShapes.Single(h => h.Position == new FractionalPosition(0));
        Assert.Equal(new FractionalPosition(1), bendHs.EndPosition);

        // The double-stops carry their OWN 2-note template, never the bend's 3-note one.
        var bendTemplate = arr.ChordTemplates.Single(t => t.Frets.Count == 3);
        var dyadTemplate = arr.ChordTemplates.Single(t => t.Frets.Count == 2);
        var dyadHs = level.HandShapes.Single(h => h.Position == new FractionalPosition(1));
        Assert.Equal(dyadTemplate.Id, dyadHs.TemplateId);
        Assert.NotEqual(bendTemplate.Id, dyadHs.TemplateId);
        Assert.False(dyadTemplate.Frets.ContainsKey(2)); // no fret-14 string in the double-stop shape
    }

    [Fact]
    public void LongSustainChord_DoesNotSwallowAFollowingMelodicNote()
    {
        // A bend chord with a long sustain followed by a stray single note: the
        // handshape must clip at the note's onset, not span over it (owner
        // report 2026-07-19: single notes were caught inside the bar).
        var arr = Arr();
        var level = new ChartLevel();
        var bend = Chord(0, new Fraction(4, 1), Cn(0, 12), Cn(1, 12));
        bend.ChordNotes[0].BendValues.Add(new BendValue(new FractionalPosition(0, new Fraction(1, 2)), 1.0));
        level.Chords.Add(bend);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 14 });

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var hs = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(1), hs.EndPosition); // clipped at the note, not beat 4
    }

    [Fact]
    public void SlideChord_KeepsItsOwnHandShape_AndDoesNotMerge()
    {
        var arr = Arr();
        var level = new ChartLevel();
        var slide = Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        slide.ChordNotes[0].SlideTo = 5;
        level.Chords.Add(slide);
        level.Chords.Add(Chord(1, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        // The slide changes pitch mid-shape: its bar stays its own, and its
        // sustain is kept (sustain-visible technique).
        Assert.Equal(2, level.HandShapes.Count);
        Assert.Equal(new FractionalPosition(1), slide.ChordNotes[0].Sustain);
    }

    [Fact]
    public void ChordAtSlideOutEnd_ButDifferentFret_IsTrimmedToStrike()
    {
        // A downward slide→1 that merely ENDS where a {3,5} chord begins does NOT
        // land on it (target fret 1 ≠ the chord's fret 3), so the chord collapses
        // to a strike like any plain chord (owner report on Test - Test.gp: a
        // stray sustain lingered under the hand-shape bar).
        var arr = Arr();
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3,
            Sustain = new FractionalPosition(1), SlideTo = 1
        });
        var chord = Chord(1, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        level.Chords.Add(chord);

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.All(chord.ChordNotes, cn => Assert.Equal(FractionalPosition.Zero, cn.Sustain));
    }

    [Fact]
    public void ChordAtSlideLanding_SameFret_KeepsSustain()
    {
        // A slide→3 that lands on the {3,5} chord's fret-3 string is a genuine
        // slide INTO the chord: it keeps ringing, so its sustain survives the trim.
        var arr = Arr();
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 1,
            Sustain = new FractionalPosition(1), SlideTo = 3
        });
        var chord = Chord(1, new Fraction(1, 1), Cn(0, 3), Cn(1, 5));
        level.Chords.Add(chord);

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.All(chord.ChordNotes, cn => Assert.True(cn.Sustain.CompareTo(FractionalPosition.Zero) > 0));
    }

    [Fact]
    public void RunTwice_DoesNotDoubleCreate()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(1, 1), Cn(0, 3), Cn(1, 5)));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);
        ChordHandShapeBuilder.BuildAndTrim(level, arr); // re-import / re-run

        Assert.Single(level.HandShapes);
        Assert.Single(arr.ChordTemplates);
    }

    // ---- End-to-end: through the real gpif import pipeline ----

    [Fact]
    public void GpImport_Chord_IsTrimmed_WithHandshape()
    {
        // A full 4/4 bar: a half-note dyad then a half rest — plain chord, so it
        // trims and gets a handshape spanning the half note.
        const string gpif = """
            <GPIF>
              <Score><Title>x</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0 1</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>0</String></Property><Property name="Fret"><Fret>3</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>5</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gphs-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            w.Write(gpif);

        try
        {
            var importer = new GpImporter();
            var ids = importer.GetAvailableTracks(path).Select(t => t.Id).ToList();
            var arr = Assert.Single(importer.ImportTracks(path, ids));
            var level = Assert.Single(arr.Levels);

            var chord = Assert.Single(level.Chords);
            Assert.All(chord.ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));
            var hs = Assert.Single(level.HandShapes);
            Assert.Equal(chord.Position, hs.Position);
            Assert.Equal(new FractionalPosition(2), hs.EndPosition); // half note = 2 beats
            Assert.Contains(arr.ChordTemplates, t => t.Id == hs.TemplateId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GpImport_DropD_TuningNamesFollowThePitches_AndChordNameUsesThem()
    {
        // Drop-D pitches (38 45 50 55 59 64): Arrangement.Tuning must reflect
        // the file's tuning (it used to stay at the E-standard default), so the
        // open D+A dyad below names as D5 — under the stale default it would
        // have been analyzed as E+A instead.
        const string gpif = """
            <GPIF>
              <Score><Title>x</Title></Score>
              <MasterTrack><Tracks>0</Tracks></MasterTrack>
              <Tracks><Track id="0"><Name>Lead</Name><Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>38 45 50 55 59 64</Pitches></Property>
              </Properties></Staff></Staves></Track></Tracks>
              <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
              <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
              <Voices><Voice id="0"><Beats>0 1</Beats></Voice></Voices>
              <Beats>
                <Beat id="0"><Rhythm ref="0" /><Notes>0 1 2</Notes></Beat>
                <Beat id="1"><Rhythm ref="0" /></Beat>
              </Beats>
              <Notes>
                <Note id="0"><Properties><Property name="String"><String>0</String></Property><Property name="Fret"><Fret>0</Fret></Property></Properties></Note>
                <Note id="1"><Properties><Property name="String"><String>1</String></Property><Property name="Fret"><Fret>0</Fret></Property></Properties></Note>
                <Note id="2"><Properties><Property name="String"><String>2</String></Property><Property name="Fret"><Fret>0</Fret></Property></Properties></Note>
              </Notes>
              <Rhythms><Rhythm id="0"><NoteValue>Half</NoteValue></Rhythm></Rhythms>
            </GPIF>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpdropd-{Guid.NewGuid():N}.gp");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("Content/score.gpif").Open(), Encoding.UTF8))
            w.Write(gpif);

        try
        {
            var importer = new GpImporter();
            var ids = importer.GetAvailableTracks(path).Select(t => t.Id).ToList();
            var arr = Assert.Single(importer.ImportTracks(path, ids));

            Assert.Equal(TuningUtilities.GetTuningNames(6, [-2, 0, 0, 0, 0, 0]), arr.Tuning);

            // Open D + open A + open D in drop D = a D5 shape; the auto-name must
            // be computed from the DROP-D tuning (E-standard would call it E5-ish).
            // THREE strings on purpose: a two-string dyad ships unnamed since 0.41,
            // which would leave this test asserting nothing about the tuning.
            var template = Assert.Single(arr.ChordTemplates);
            Assert.Equal("D5", template.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- rscp save/load round-trip ----

    [Fact]
    public void ImportedHandshape_SurvivesRscpRoundTrip()
    {
        var arr = Arr();
        var level = new ChartLevel();
        level.Chords.Add(Chord(0, new Fraction(2, 1), Cn(0, 3), Cn(1, 5)));
        ChordHandShapeBuilder.BuildAndTrim(level, arr);
        arr.Levels.Add(level);

        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i < 4; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
        project.Arrangements.Add(arr);

        var dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "project.rscp");
        try
        {
            var ser = new RscpChartSerializer();
            ser.Save(path, project);
            var loaded = ser.Load(path);

            var loadedLevel = loaded.Arrangements[0].Levels[0];
            var hs = Assert.Single(loadedLevel.HandShapes);
            Assert.Equal(new FractionalPosition(0), hs.Position);
            Assert.Equal(new FractionalPosition(2), hs.EndPosition);
            Assert.Contains(loaded.Arrangements[0].ChordTemplates, t => t.Id == hs.TemplateId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
