using System.IO.Compression;
using System.Text;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The GP-import slide/grace losses found by diffing DotCharter's RS2 export
///     of the cross-tool reference score against EOF's and Log Charter's
///     (2026-07-20). The source <c>score.gpif</c> holds 9 pitched (<c>Slide=2</c>),
///     6 unpitched (<c>Slide=4</c>) and 2 slide-in (<c>Slide=16</c>) notes; EOF and
///     Charter agree EXACTLY on 12 <c>slideTo</c> and 6 <c>slideUnpitchTo</c>,
///     while DotCharter emitted 5 and 6. Four independent defects account for the
///     gap, one test group each:
///     <list type="bullet">
///     <item>a grace before a CHORD was dropped outright — the hint model was
///     per-note/per-string, so the 3-note 3/5/5 → 5/7/7 shift vanished and only
///     the landing chord survived (at the origin's timestamp);</item>
///     <item><c>Slide=16</c> / <c>32</c> (slide in from below/above) was not
///     implemented and rendered a static note;</item>
///     <item>a resolved pitched slide got no <c>linkNext</c>, so it did not
///     sustain into its landing note (EOF sets it on all twelve);</item>
///     <item>every grace was placed BEFORE the beat even though the source says
///     <c>&lt;GraceNotes&gt;OnBeat&lt;/GraceNotes&gt;</c> for all five of them —
///     which also silently DROPPED the two graces whose target abuts the previous
///     note, because there was no room ahead of the beat.</item>
///     </list>
/// </summary>
public sealed class GpSlideRecoveryTests
{
    // ---- gpif fixture plumbing -------------------------------------------

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpslide-{Guid.NewGuid():N}.gp");
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

    private static string Envelope(string voiceBeats, string beats, string notes, string rhythms,
        string tuning = "40 45 50 55 59 64") => $"""
        <GPIF>
          <Score><Title>Slide Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Lead</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>{tuning}</Pitches></Property>
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

    private const string QuarterRhythm = """<Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>""";

    private const string QuarterAnd32nd = QuarterRhythm +
                                          """<Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>""";

    // ---- Defect 3a: a grace before a CHORD -------------------------------

    /// <summary>
    ///     The reference file's bar 12: a 32nd grace CHORD (1,3)(2,5)(3,5) with
    ///     <c>Slide=2</c> on every string, sliding into the quarter chord
    ///     (1,5)(2,7)(3,7). EOF and Charter both emit the origin chord with
    ///     <c>slideTo</c>+<c>linkNext</c> on all three strings; DotCharter used to
    ///     emit the landing chord alone, sitting at the origin's timestamp.
    /// </summary>
    [Fact]
    public void GraceBeforeAChord_MaterializesAnOriginChordThatSlidesIn()
    {
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><GraceNotes>OnBeat</GraceNotes><Rhythm ref="1" /><Notes>0 1 2</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>3 4 5</Notes></Beat>
            """,
            Note(0, 1, 3, Slide(2)) + Note(1, 2, 5, Slide(2)) + Note(2, 3, 5, Slide(2)) +
            Note(3, 1, 5) + Note(4, 2, 7) + Note(5, 3, 7),
            QuarterAnd32nd);

        var level = ImportSingleTrack(gpif).Levels[0];
        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count);

        var origin = chords[0];
        Assert.Equal(FractionalPosition.Zero, origin.Position);
        Assert.Equal(new[] { 3, 5, 5 }, origin.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.Fret));
        Assert.Equal(new[] { 5, 7, 7 },
            origin.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.SlideTo!.Value));
        Assert.All(origin.ChordNotes, cn => Assert.True(cn.LinkNext));

        // ...and the landing chord is a REAL chord, pushed past the ornament.
        Assert.True(chords[1].Position.CompareTo(origin.Position) > 0);
        Assert.Equal(new[] { 5, 7, 7 }, chords[1].ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.Fret));
    }

    /// <summary>A grace on a string the target chord does not use has nothing to
    /// slide into; it is dropped without disturbing the chord.</summary>
    [Fact]
    public void GraceOnAStringTheChordDoesNotUse_IsDroppedNotMaterialized()
    {
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><GraceNotes>OnBeat</GraceNotes><Rhythm ref="1" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1 2</Notes></Beat>
            """,
            Note(0, 5, 3, Slide(2)) + Note(1, 1, 5) + Note(2, 2, 7),
            QuarterAnd32nd);

        var level = ImportSingleTrack(gpif).Levels[0];
        var chord = Assert.Single(level.Chords);
        Assert.Equal(FractionalPosition.Zero, chord.Position); // untouched
        Assert.Equal(2, chord.ChordNotes.Count);
    }

    // ---- Defect 3b: slide IN (Slide=16 / 32) ------------------------------

    /// <summary>
    ///     <c>Slide=16</c> ("slide in from below") must become a REAL pitched
    ///     slide starting below the target — both references materialize one.
    ///     GP names no origin fret, so ours is fabricated at the default distance
    ///     of TWO frets below (owner default; EOF uses 1, Charter 2). The exact
    ///     distance and its configurability are pinned on the materializer in
    ///     <see cref="MaterializeSlideIns_RespectsTheConfiguredFretDistance"/>.
    /// </summary>
    [Fact]
    public void SlideInFromBelow_MaterializesAPitchedLeadInBelowTheTarget()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>""",
            Note(0, 1, 3, Slide(16)),
            QuarterRhythm);

        var level = ImportSingleTrack(gpif).Levels[0];
        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        var lead = notes[0];
        Assert.Equal(1, lead.Fret);                       // target 3 minus the default two frets
        Assert.Equal(3, lead.SlideTo);
        Assert.True(lead.LinkNext);
        Assert.Equal(FractionalPosition.Zero, lead.Position);
        Assert.Equal(new FractionalPosition(0, GpPostProcessor.SlideInLeadIn), lead.Sustain);

        // The target moved to the end of the lead-in and kept the rest of the beat.
        Assert.Equal(lead.Position + lead.Sustain, notes[1].Position);
        Assert.Equal(3, notes[1].Fret);
        Assert.Null(notes[1].SlideTo);                    // the sentinel never survives
    }

    /// <summary><c>Slide=32</c> is the mirror image — the lead-in comes from
    /// ABOVE the target (default two frets).</summary>
    [Fact]
    public void SlideInFromAbove_MaterializesAPitchedLeadInAboveTheTarget()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>""",
            Note(0, 1, 7, Slide(32)),
            QuarterRhythm);

        var notes = ImportSingleTrack(gpif).Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(9, notes[0].Fret);
        Assert.Equal(7, notes[0].SlideTo);
        Assert.True(notes[0].LinkNext);
    }

    /// <summary>
    ///     The slide-in origin distance is configurable (owner setting, default 2).
    ///     Pinned directly on the materializer: EOF's 1 fret, the default 2, and
    ///     the max 3 all fabricate the origin the requested distance below the
    ///     target; out-of-range values clamp into 1–3.
    /// </summary>
    [Theory]
    [InlineData(1, 6)] // EOF: target 7 minus 1
    [InlineData(2, 5)] // default: minus 2
    [InlineData(3, 4)] // max: minus 3
    [InlineData(9, 4)] // clamped to 3
    public void MaterializeSlideIns_RespectsTheConfiguredFretDistance(int distance, int expectedOriginFret)
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, StringIndex = 0, Fret = 7,
            SlideTo = GpNoteParser.SlideInFromBelow, Sustain = new FractionalPosition(1)
        });

        GpPostProcessor.MaterializeSlideIns(level, distance);

        var lead = level.Notes.OrderBy(n => n.Position).First();
        Assert.Equal(expectedOriginFret, lead.Fret);
        Assert.Equal(7, lead.SlideTo);
    }

    /// <summary>A slide-in into an OPEN string from below would need fret −1:
    /// there is no origin, so the ornament degrades to the plain note (and never
    /// leaves a negative sentinel behind).</summary>
    [Fact]
    public void SlideInFromBelow_IntoAnOpenString_LeavesAPlainNote()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>""",
            Note(0, 1, 0, Slide(16)),
            QuarterRhythm);

        var note = Assert.Single(ImportSingleTrack(gpif).Levels[0].Notes);
        Assert.Equal(0, note.Fret);
        Assert.Null(note.SlideTo);
        Assert.False(note.UnpitchedSlide);
    }

    /// <summary>A slide-in inside a CHORD cannot be split into an origin note;
    /// the sentinel is cleared so no negative fret reaches the chart.</summary>
    [Fact]
    public void SlideInOnAChordNote_ClearsTheSentinel()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = FractionalPosition.Zero };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 1, Fret = 5, SlideTo = GpNoteParser.SlideInFromBelow,
            Sustain = new FractionalPosition(1)
        });
        level.Chords.Add(chord);

        GpPostProcessor.MaterializeSlideIns(level);

        Assert.Null(chord.ChordNotes[0].SlideTo);
    }

    /// <summary>An authored slide OUT wins the note's single RS slide slot — a
    /// note flagged both ways must not lose the slide-out to the ornament.</summary>
    [Fact]
    public void SlideOutAndSlideIn_OnOneNote_KeepsTheSlideOut()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>""",
            Note(0, 1, 7, Slide(4 | 16)),
            QuarterRhythm);

        var note = Assert.Single(ImportSingleTrack(gpif).Levels[0].Notes);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(2, note.SlideTo); // 7 - 5
    }

    // ---- Defect 3c: linkNext on a resolved pitched slide ------------------

    /// <summary>
    ///     RS2014 needs <c>linkNext</c> for a pitched slide to sustain into its
    ///     landing note; EOF sets it on all twelve slides of the reference file
    ///     and DotCharter set it on none of its five.
    /// </summary>
    [Fact]
    public void ResolvedPitchedSlide_GetsLinkNext()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, Sustain = new FractionalPosition(1),
            StringIndex = 2, Fret = 5, SlideTo = -1
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 7 });

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Equal(7, level.Notes[0].SlideTo);
        Assert.True(level.Notes[0].LinkNext);
    }

    /// <summary>A slide whose sustain stops short of the next sound (a rest in
    /// between) links to nothing — the slide resolves, the link does not. The
    /// landing is one beat after the slide's end, the most a landing may be
    /// (#284; further away is no landing, see GpSlideLandingGapTests).</summary>
    [Fact]
    public void SlideThatDoesNotReachItsLanding_IsNotLinked()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, Sustain = new FractionalPosition(1),
            StringIndex = 2, Fret = 5, SlideTo = -1
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 });

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Equal(7, level.Notes[0].SlideTo);
        Assert.False(level.Notes[0].LinkNext);
    }

    /// <summary>A slide-in sentinel that reaches <see cref="GpPostProcessor.LinkSlides"/>
    /// unmaterialized (the notation-only sidecar voices run that pass without
    /// <see cref="GpPostProcessor.MaterializeSlideIns"/>) is scrubbed — a negative
    /// fret must never reach the chart.</summary>
    [Fact]
    public void LinkSlides_ScrubsAnUnmaterializedSlideInSentinel()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, StringIndex = 0, Fret = 5,
            SlideTo = GpNoteParser.SlideInFromAbove
        });

        GpPostProcessor.LinkSlides(level, 6);

        Assert.Null(level.Notes[0].SlideTo);
    }

    // ---- Defect 3d: OnBeat vs BeforeBeat grace placement ------------------

    /// <summary>
    ///     Every grace beat in the reference score is
    ///     <c>&lt;GraceNotes&gt;OnBeat&lt;/GraceNotes&gt;</c>, and both references
    ///     honour it: the ornament sits ON the beat and the main note is pushed
    ///     later (EOF: grace at 22.540, target moved to 22.603). DotCharter placed
    ///     every grace a 32nd EARLY instead.
    /// </summary>
    [Fact]
    public void OnBeatGrace_SitsOnTheBeatAndPushesItsTarget()
    {
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><GraceNotes>OnBeat</GraceNotes><Rhythm ref="1" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            """,
            Note(0, 3, 3, Slide(2)) + Note(1, 3, 5),
            QuarterAnd32nd);

        var notes = ImportSingleTrack(gpif).Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        Assert.Equal(FractionalPosition.Zero, notes[0].Position); // ON the beat, not before it
        Assert.Equal(3, notes[0].Fret);
        Assert.Equal(5, notes[0].SlideTo);
        Assert.True(notes[0].LinkNext);
        Assert.Equal(new FractionalPosition(0, GpPostProcessor.GraceLeadIn), notes[0].Sustain);
        Assert.Equal(notes[0].Position + notes[0].Sustain, notes[1].Position);
    }

    /// <summary>A BEFORE-beat grace keeps the traditional placement: the ornament
    /// is squeezed in ahead of the beat and the target does NOT move.</summary>
    [Fact]
    public void BeforeBeatGrace_StealsTimeAheadOfTheBeatAndLeavesItsTargetPut()
    {
        var gpif = Envelope(
            "0 1 2",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="1" /><Notes>1</Notes></Beat>
            <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
            """,
            // Beat 0 is on ANOTHER string, so the graced string has room to spare.
            Note(0, 0, 5) + Note(1, 3, 3, Slide(2)) + Note(2, 3, 5),
            QuarterAnd32nd);

        var onString3 = ImportSingleTrack(gpif).Levels[0].Notes
            .Where(n => n.StringIndex == 3).OrderBy(n => n.Position).ToList();
        Assert.Equal(2, onString3.Count);

        // The target stayed on beat 1; the ornament sits a 32nd ahead of it.
        Assert.Equal(new FractionalPosition(1), onString3[1].Position);
        Assert.Equal(new FractionalPosition(1) - new FractionalPosition(0, GpPostProcessor.GraceLeadIn),
            onString3[0].Position);
        Assert.Equal(5, onString3[0].SlideTo);
    }

    /// <summary>
    ///     The silent-drop regression: a before-beat grace whose target abuts the
    ///     previous note on the same string has NO room ahead of the beat. The old
    ///     rule gave up and dropped the ornament entirely — that is how two of the
    ///     reference file's grace slides disappeared. It must fall back to on-beat
    ///     placement instead.
    /// </summary>
    [Fact]
    public void BeforeBeatGrace_WithNoRoom_FallsBackToOnBeatInsteadOfVanishing()
    {
        var level = new ChartLevel();
        // The previous note on string 1 ends exactly where the target starts.
        level.Notes.Add(new GuitarNote
        {
            Position = FractionalPosition.Zero, Sustain = new FractionalPosition(1),
            StringIndex = 1, Fret = 2
        });
        var target = new GuitarNote
        {
            Position = new FractionalPosition(1), Sustain = new FractionalPosition(1),
            StringIndex = 1, Fret = 7, GraceFret = 5, GraceSlide = true
        };
        level.Notes.Add(target);

        GpPostProcessor.MaterializeGraceSlides(level);

        var grace = Assert.Single(level.Notes, n => n.Fret == 5);
        Assert.Equal(new FractionalPosition(1), grace.Position); // on the beat
        Assert.Equal(7, grace.SlideTo);
        Assert.True(grace.LinkNext);
        Assert.Equal(grace.Position + grace.Sustain, target.Position); // the target moved past it
        Assert.Null(target.GraceFret);
    }

    // ---- Latent 1: the gpif string-count guard ---------------------------

    /// <summary>
    ///     A gpif note on a string the track does not have is INERT: every
    ///     post-import pass bounds-checks it away, so it survived import only as a
    ///     phantom sound nothing could see or edit. The GP3-5 binary reader has
    ///     always dropped these; gpif now matches.
    /// </summary>
    [Fact]
    public void NoteOnAStringTheTrackDoesNotHave_IsDropped()
    {
        // A 4-string bass track with a note authored on string 5.
        var gpif = Envelope(
            "0 1",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            """,
            Note(0, 5, 7) + Note(1, 2, 5),
            QuarterRhythm,
            tuning: "28 33 38 43");

        var level = ImportSingleTrack(gpif).Levels[0];
        var note = Assert.Single(level.Notes);
        Assert.Equal(2, note.StringIndex);
    }

    /// <summary>Same guard inside a chord: the out-of-range string is dropped and
    /// the playable strings still form the chord.</summary>
    [Fact]
    public void ChordNoteOnAStringTheTrackDoesNotHave_IsDropped()
    {
        var gpif = Envelope(
            "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0 1 2</Notes></Beat>""",
            Note(0, 0, 3) + Note(1, 1, 5) + Note(2, 6, 9),
            QuarterRhythm,
            tuning: "28 33 38 43");

        var chord = Assert.Single(ImportSingleTrack(gpif).Levels[0].Chords);
        Assert.Equal(new[] { 0, 1 }, chord.ChordNotes.Select(cn => cn.StringIndex).OrderBy(s => s));
    }

    // ---- Latent 2: duplicate-time dedup ----------------------------------

    /// <summary>
    ///     Two sounds on ONE string at ONE instant are physically impossible, and
    ///     nothing downstream removed them (<c>ChartSpacingEnforcer</c> only
    ///     shortens sustains). The richer note wins.
    /// </summary>
    [Fact]
    public void DuplicateNotesOnAString_KeepTheOneCarryingMoreInformation()
    {
        var level = new ChartLevel();
        var plain = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 2, Fret = 5 };
        var rich = new GuitarNote
        {
            Position = FractionalPosition.Zero, Sustain = new FractionalPosition(1),
            StringIndex = 2, Fret = 5, SlideTo = 7, LinkNext = true
        };
        level.Notes.Add(plain);
        level.Notes.Add(rich);

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Same(rich, Assert.Single(level.Notes));
    }

    /// <summary>An exact tie keeps the FIRST note, so the pass is order-stable and
    /// idempotent.</summary>
    [Fact]
    public void DuplicateNotesOfEqualWeight_KeepTheFirst()
    {
        var level = new ChartLevel();
        var first = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 0, Fret = 5 };
        var second = new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 0, Fret = 9 };
        level.Notes.Add(first);
        level.Notes.Add(second);

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Same(first, Assert.Single(level.Notes));
    }

    /// <summary>When a chord already claims the string at that instant the
    /// standalone NOTE is the redundant one — removing a chord note would rewrite
    /// the strum's shape, template and hand shape.</summary>
    [Fact]
    public void NoteCollidingWithAChordNote_LosesToTheChord()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = FractionalPosition.Zero };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 5 });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 2, Fret = 12 });

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Empty(level.Notes);
        Assert.Equal(2, Assert.Single(level.Chords).ChordNotes.Count);
    }

    /// <summary>Legitimately stacked events must survive: different strings, and
    /// the same string at different instants.</summary>
    [Fact]
    public void DistinctSounds_AreNeverRemoved()
    {
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 0, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 1, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 5 });

        GpPostProcessor.RemoveDuplicateSounds(level);

        Assert.Equal(3, level.Notes.Count);
    }

    // ---- Latent 3: the legacy path's second hand-shape clamp -------------

    /// <summary>
    ///     The gpif path re-clamps hand shapes AFTER the let-ring pass adds its
    ///     own; the GP3-5 binary path did not, so a let-ring shape could overlap
    ///     the following chord's shape until the save-time spacing enforcer
    ///     clipped it. The two importers must stay symmetric.
    /// </summary>
    [Theory]
    [InlineData("5.10")]
    [InlineData("4.06")]
    public void LegacyImport_LetRingShapesNeverOverlapChordShapes(string version)
    {
        // Three let-ring quarters, the middle one a chord: the chord gets its own
        // hand shape [1,2] from the shared builder, and the let-ring run then adds
        // a shape spanning [0,3] straight over it.
        var measure = new FxMeasure().AddVoice(
            new FxBeat { Duration = 0, Notes = { new FxNote(0, 3, LetRing: true) } },
            new FxBeat { Duration = 0, Notes = { new FxNote(3, 7, LetRing: true), new FxNote(4, 7, LetRing: true) } },
            new FxBeat { Duration = 0, Notes = { new FxNote(2, 9, LetRing: true) } });

        var path = LegacyGpFixtureBuilder.WriteFile(version, new[] { measure });
        Arrangement arr;
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            arr = Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        var shapes = arr.Levels[0].HandShapes.OrderBy(h => h.Position).ToList();
        for (var i = 0; i < shapes.Count - 1; i++)
        {
            Assert.True(shapes[i].EndPosition.CompareTo(shapes[i + 1].Position) <= 0,
                "a let-ring hand shape must not overlap the next shape after import");
        }
    }

    /// <summary>
    ///     A let-ring arpeggio's hand shape carries the run's duration, so the
    ///     individual notes must collapse to short strikes — the same convention
    ///     <see cref="ChordHandShapeBuilder"/> applies to chords (owner report: the
    ///     arpeggio notes kept their long let-ring sustains under the bar). A note
    ///     that needs its sustain to render a technique keeps it.
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_TrimsPlainNoteSustains_ButKeepsTechniqueSustains()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        var plain = new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1)
        };
        var slid = new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 1, Fret = 5, SlideTo = 7,
            Sustain = new FractionalPosition(1)
        };
        var plain2 = new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 5, Sustain = new FractionalPosition(1)
        };
        level.Notes.Add(plain);
        level.Notes.Add(slid);
        level.Notes.Add(plain2);
        letRing.Add(plain.Position);
        letRing.Add(slid.Position);
        letRing.Add(plain2.Position);

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        Assert.Single(level.HandShapes); // the run became one hand shape
        Assert.Equal(FractionalPosition.Zero, plain.Sustain);
        Assert.Equal(FractionalPosition.Zero, plain2.Sustain);
        Assert.True(slid.Sustain.CompareTo(FractionalPosition.Zero) > 0, "a slide note keeps its sustain");
    }

    /// <summary>
    ///     A let-ring run is a PLAIN hand shape, never an arpeggio (owner decision
    ///     0.34.1). <see cref="ChordTemplate.Arpeggio" /> drives ChordMask.Arpeggio
    ///     in the SNG export, the .eof arpeggio section type and the purple
    ///     arpeggio brackets in the 3D highway — none of which "let ring" means.
    ///     The shape still arrives charting-ready (suggested fingering + name).
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_MakesAPlainHandShape_NotAnArpeggio()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        // G major across the three lowest strings: 3-2-0.
        foreach (var (beat, str, fret) in new[] { (0, 0, 3), (1, 1, 2), (2, 2, 0) })
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = str, Fret = fret,
                Sustain = new FractionalPosition(1)
            };
            level.Notes.Add(note);
            letRing.Add(note.Position);
        }

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        var shape = Assert.Single(level.HandShapes);
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(template.Id, shape.TemplateId);
        Assert.False(template.Arpeggio, "let ring is a sustain instruction, not an arpeggio phrase");
        Assert.Equal(new Dictionary<int, int> { [0] = 3, [1] = 2, [2] = 0 }, template.Frets);
        // Charting-ready like every other imported shape: fingering for the
        // fretted strings and a suggested name (GP carries neither for a run).
        Assert.NotEmpty(template.Fingers);
        Assert.False(string.IsNullOrEmpty(template.Name), "the run's shape gets a suggested chord name");
    }

    /// <summary>
    ///     Templates are SHARED and deduped by shape. Now that a let-ring run
    ///     produces an ordinary (non-arpeggio) template it joins that pool, so a
    ///     run whose aggregate shape a chord already plays must REUSE that
    ///     chord's template rather than adding a duplicate.
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_ReusesAnExistingTemplateWithTheSameShape()
    {
        var arr = new Arrangement();
        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 7, Name = "Em", Frets = new Dictionary<int, int> { [0] = 0, [1] = 2 }
        });
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        foreach (var (beat, str, fret) in new[] { (0, 0, 0), (1, 1, 2) })
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = str, Fret = fret,
                Sustain = new FractionalPosition(1)
            };
            level.Notes.Add(note);
            letRing.Add(note.Position);
        }

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        var shape = Assert.Single(level.HandShapes);
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(7, template.Id);
        Assert.Equal(7, shape.TemplateId);
        Assert.Equal("Em", template.Name); // the hand-authored name is never overwritten
    }

    /// <summary>
    ///     The bar must span the run's AUTHORED ring-out, not collapse onto the
    ///     last note's own onset (owner report, image #12: "the handshape ... cuts
    ///     off at the start of the note"). This pass runs after AdjustSustains has
    ///     zeroed a sub-quarter let-ring note's playable Sustain, so the bar end
    ///     has to come from the surviving NotationDuration.
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_BarSpansAuthoredRingOut_NotJustTheLastOnset()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        // A 16th-note run 7-7-7-7 whose sustains are ALREADY trimmed to zero (as
        // they are by the time this pass runs on a real import), but whose
        // authored NotationDuration survives. The last note's authored 8th-note
        // duration rings to 3/4 + 1/2 = 5/4, exactly where the next chord strikes.
        for (var i = 0; i < 4; i++)
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(0, new Fraction(i, 4)),
                StringIndex = i, Fret = 7,
                Sustain = FractionalPosition.Zero,
                NotationDuration = new Fraction(i == 3 ? 1 : 1, i == 3 ? 2 : 4) // last = 1/2, rest = 1/4
            };
            level.Notes.Add(note);
            letRing.Add(note.Position);
        }

        var nextChord = new Chord { Position = new FractionalPosition(0, new Fraction(5, 4)) };
        nextChord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 10, Sustain = FractionalPosition.Zero });
        nextChord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 12, Sustain = FractionalPosition.Zero });
        level.Chords.Add(nextChord);

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(FractionalPosition.Zero, shape.Position);
        // Not the collapsed 3/4 (the last onset) — the bar covers the ring-out.
        Assert.Equal(new FractionalPosition(0, new Fraction(5, 4)), shape.EndPosition);
    }

    /// <summary>
    ///     A let-ring run rings until the next played sound and no further — the
    ///     bar is clipped to that sound even when the authored ring-out overshoots
    ///     it (owner report, image #11/#13: let-ring bars overlapped the following
    ///     note).
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_ClipsBarToTheNextSound_NoOverlap()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        // A two-note run whose last note claims a whole-beat ring-out (1/1), but
        // a fresh note strikes half a beat later — the bar must stop there.
        for (var i = 0; i < 2; i++)
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(0, new Fraction(i, 2)),
                StringIndex = i, Fret = 5,
                Sustain = FractionalPosition.Zero,
                NotationDuration = new Fraction(1, 1) // authored a full beat — would reach 3/2
            };
            level.Notes.Add(note);
            letRing.Add(note.Position);
        }

        // Next sound at 1/1, well before the 1/2 + 1/1 = 3/2 authored ring-out.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 3, Fret = 4, Sustain = FractionalPosition.Zero
        });

        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(new FractionalPosition(1), shape.EndPosition); // clipped to the next sound, not 3/2
    }

    /// <summary>
    ///     A let-ring run whose LAST beat is a CHORD must keep that chord's
    ///     AUTHORED ring-out. This pass runs after <c>GpPostProcessor.Run</c>, where
    ///     <see cref="ChordHandShapeBuilder" /> has already zeroed a plain chord's
    ///     ChordNote sustains to a strike (the chord's own hand shape carries the
    ///     length) — so <c>NotationDuration</c> is the only surviving authored
    ///     length, exactly as it is for a note. Folding it into
    ///     <c>BeatInfo.AuthoredSpan</c> for NOTES only left <c>ringOut == 0</c> for
    ///     a chord-ended run, collapsing the bar onto the final strum's own onset —
    ///     the same "cuts off at the start" failure (owner report, image #12) the
    ///     AuthoredSpan mechanism was added to fix for notes.
    ///     <para>
    ///     The run has to be the LAST thing on the level: the "no authored ring-out
    ///     survived ⇒ extend to the next onset" fallback MASKS this collapse
    ///     whenever anything follows, silently substituting "until the next sound"
    ///     for the authored ring-out.
    ///     </para>
    /// </summary>
    [Fact]
    public void BuildLetRingHandShapes_RunEndingOnAChord_KeepsTheChordsAuthoredRingOut()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        var letRing = new HashSet<FractionalPosition>();

        // Beat 1: a single let-ring note, sustain already trimmed to a strike.
        var note = new GuitarNote
        {
            Position = FractionalPosition.Zero,
            StringIndex = 5, Fret = 3,
            Sustain = FractionalPosition.Zero,
            NotationDuration = new Fraction(1, 2)
        };
        level.Notes.Add(note);
        letRing.Add(note.Position);

        // Beat 2 — the run's LAST: a let-ring CHORD authored a full beat long,
        // whose ChordNote sustains ChordHandShapeBuilder has already zeroed.
        var chordPos = new FractionalPosition(0, new Fraction(1, 2));
        var chord = new Chord
        {
            Position = chordPos,
            NotationDuration = new Fraction(1, 1)
        };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 3, Sustain = FractionalPosition.Zero });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 5, Sustain = FractionalPosition.Zero });
        level.Chords.Add(chord);
        letRing.Add(chord.Position);

        // NOTHING sounds after the run — no next onset for the fallback to borrow.
        GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRing);

        var shape = Assert.Single(level.HandShapes);
        Assert.Equal(FractionalPosition.Zero, shape.Position);
        // 1/2 (the strum) + the chord's authored 1/1 ring-out. Before the fix the
        // chord's NotationDuration never reached AuthoredSpan, so ringOut was zero
        // and the bar ended at 1/2 — the strum's own onset.
        Assert.Equal(new FractionalPosition(0, new Fraction(3, 2)), shape.EndPosition);
        Assert.True(shape.EndPosition.CompareTo(chordPos) > 0);
    }
}
