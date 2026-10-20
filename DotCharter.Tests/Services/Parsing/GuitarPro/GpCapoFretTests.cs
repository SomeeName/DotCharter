using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     <b>Guitar Pro writes tab fret numbers RELATIVE to the track's capo; our
///     chart model is ABSOLUTE on the un-capoed neck</b> (fret <c>0</c> = open,
///     which the capo stops at the capo fret, so a sound's sounding offset is
///     <c>max(fret, capo)</c>). Both importers used to read <c>CapoFret</c> onto
///     <see cref="Arrangement.Capo"/> and then store GP's number verbatim, so
///     every fretted note on a capo'd track sat <c>capo</c> semitones flat — and
///     with it every chord template, suggested name, fingering, hand shape and
///     FHP anchor derived from those frets.
///     <para>
///     Measured against the hand-corrected EOF corpus, the lead track of
///     "Ghost In A Flower" (<c>CapoFret</c> 2) exported frets 2/3/5/7/8 where the
///     ground truth had 4/5/7/9/10 — identical note COUNTS, every nonzero fret
///     exactly 2 low, fret 0 unshifted — while the same file's capo-0 bass track
///     matched perfectly. The conversion is EOF's own ("Capo added to fretted
///     strings only", <c>rs.c</c>): see <see cref="GpCapoFrets"/>.
///     </para>
///     Every test here builds its fixture in code — nothing depends on
///     <c>TestData/</c>. Both import paths are covered, because they read frets
///     through completely separate code (gpif <see cref="GpNoteParser"/> vs. the
///     GP3-5 binary reader) and only one of them being right is the bug.
/// </summary>
public sealed class GpCapoFretTests
{
    // ── gpif fixture plumbing (same shape as GpChordGraceHintTests) ─────────

    private static string WriteGpFile(string gpif)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpcapo-{Guid.NewGuid():N}.gp");
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
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Envelope(int capo, string voiceBeats, string beats, string notes) => $"""
        <GPIF>
          <Score><Title>Capo Fixture</Title></Score>
          <MasterTrack><Tracks>0</Tracks></MasterTrack>
          <Tracks>
            <Track id="0">
              <Name>Guitar</Name>
              <Staves><Staff><Properties>
                <Property name="Tuning"><Pitches>40 45 50 55 59 64</Pitches></Property>
                <Property name="CapoFret"><Fret>{capo}</Fret></Property>
              </Properties></Staff></Staves>
            </Track>
          </Tracks>
          <MasterBars><MasterBar><Time>4/4</Time><Bars>0</Bars></MasterBar></MasterBars>
          <Bars><Bar id="0"><Voices>0 -1 -1 -1</Voices></Bar></Bars>
          <Voices><Voice id="0"><Beats>{voiceBeats}</Beats></Voice></Voices>
          <Beats>{beats}</Beats>
          <Notes>{notes}</Notes>
          <Rhythms>
            <Rhythm id="0"><NoteValue>Quarter</NoteValue></Rhythm>
            <Rhythm id="1"><NoteValue>32nd</NoteValue></Rhythm>
          </Rhythms>
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

    private static XElement NoteElement(int str, int fret, string extra = "")
        => XElement.Parse(Note(0, str, fret, extra));

    /// <summary>Three quarter beats on one string: an OPEN string and two fretted
    /// notes, which is the whole invariant in one fixture.</summary>
    private static string OpenAndFrettedRun(int capo) => Envelope(capo,
        "0 1 2",
        """
        <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
        <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
        <Beat id="2"><Rhythm ref="0" /><Notes>2</Notes></Beat>
        """,
        Note(0, 0, 0) + Note(1, 0, 2) + Note(2, 0, 5));

    // ── gpif path ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Capo 2: GP's 0/2/5 are the capo'd open string and two notes 2 and 5
    ///     frets above the capo, i.e. absolute 0/4/7. Fret 0 must stay 0 —
    ///     <c>max(0, capo)</c> already sounds it at the capo, and lifting it to
    ///     the capo fret would turn an open string into a stopped one.
    /// </summary>
    [Fact]
    public void Gpif_Capo2_ShiftsFrettedNotesAndLeavesTheOpenStringAlone()
    {
        var arr = ImportSingleTrack(OpenAndFrettedRun(capo: 2));

        Assert.Equal(2, arr.Capo);
        Assert.Equal(new[] { 0, 4, 7 },
            arr.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret).ToArray());
    }

    /// <summary>Capo 0 — nearly every chart — must be a STRICT no-op: the same
    /// fixture imports byte-identical frets to what Guitar Pro wrote.</summary>
    [Fact]
    public void Gpif_Capo0_IsAStrictNoOp()
    {
        var arr = ImportSingleTrack(OpenAndFrettedRun(capo: 0));

        Assert.Equal(0, arr.Capo);
        Assert.Equal(new[] { 0, 2, 5 },
            arr.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret).ToArray());
    }

    /// <summary>
    ///     A chord's per-string frets shift too, and so does the
    ///     <see cref="ChordTemplate"/> the importer find-or-creates from them —
    ///     the template is what the fingering suggester, the chord namer and the
    ///     FHP generator all read, so a relative template poisons all three.
    /// </summary>
    [Fact]
    public void Gpif_Capo2_ShiftsChordNotesAndTheChordTemplate()
    {
        var arr = ImportSingleTrack(Envelope(2, "0",
            """<Beat id="0"><Rhythm ref="0" /><Notes>0 1 2</Notes></Beat>""",
            Note(0, 0, 0) + Note(1, 1, 3) + Note(2, 2, 5)));

        var chord = Assert.Single(arr.Levels[0].Chords);
        Assert.Equal(new[] { 0, 5, 7 },
            chord.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.Fret).ToArray());

        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(new[] { 0, 5, 7 },
            template.Frets.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray());
    }

    /// <summary>A plain grace ornament degrades to the notation-only
    /// <see cref="GuitarNote.GraceFret"/> hint — which is a fret like any other
    /// and is written relative to the capo like any other.</summary>
    [Fact]
    public void Gpif_Capo2_ShiftsAGraceHintFret()
    {
        var arr = ImportSingleTrack(Envelope(2, "0 1",
            """
            <Beat id="0"><GraceNotes>BeforeBeat</GraceNotes><Rhythm ref="1" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            """,
            Note(0, 0, 3) + Note(1, 0, 5)));

        var note = Assert.Single(arr.Levels[0].Notes);
        Assert.Equal(7, note.Fret);      // 5 + 2
        Assert.Equal(5, note.GraceFret); // 3 + 2
    }

    /// <summary>
    ///     A PITCHED slide's landing fret must shift with the note it lands on.
    ///     GP parks the marker <c>-1</c> ("the next sound on this string") and
    ///     <c>GpPostProcessor.LinkSlides</c> resolves it, so the target is correct
    ///     exactly when the landing note itself was converted.
    /// </summary>
    [Fact]
    public void Gpif_Capo2_ShiftsAPitchedSlideTarget()
    {
        var arr = ImportSingleTrack(Envelope(2, "0 1",
            """
            <Beat id="0"><Rhythm ref="0" /><Notes>0</Notes></Beat>
            <Beat id="1"><Rhythm ref="0" /><Notes>1</Notes></Beat>
            """,
            Note(0, 0, 5, Slide(1)) + Note(1, 0, 7)));

        var notes = arr.Levels[0].Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(7, notes[0].Fret);
        Assert.Equal(9, notes[1].Fret);
        Assert.Equal(9, notes[0].SlideTo); // the LANDING fret, absolute
    }

    /// <summary>
    ///     The slide-IN sentinels are NEGATIVE markers, not frets: shifting one
    ///     would turn <c>-2</c> into <c>0</c> on a capo-2 track and silently
    ///     re-read as "slide to the open string". They must survive parse
    ///     untouched while the note's own fret converts.
    /// </summary>
    [Theory]
    [InlineData(16, GpNoteParser.SlideInFromBelow)]
    [InlineData(32, GpNoteParser.SlideInFromAbove)]
    public void Gpif_Capo_NeverShiftsASlideSentinel(int slideFlags, int expectedSentinel)
    {
        var parser = new GpNoteParser { Capo = 2 };

        var note = parser.ParseNote(NoteElement(0, 5, Slide(slideFlags)), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.Equal(7, note.Fret);
        Assert.Equal(expectedSentinel, note.SlideTo);
    }

    /// <summary>
    ///     An UNPITCHED slide-out names a real fret (<c>fret ± 5</c>) derived from
    ///     the note's own fret, so it has to be computed AFTER the conversion.
    ///     With capo 2 a GP fret 5 is absolute 7 and the scrape lands on 2 — not
    ///     on the <c>max(1, 0)</c> floor the relative fret would have produced.
    /// </summary>
    [Fact]
    public void Gpif_Capo2_DerivesUnpitchedSlideTargetsFromTheAbsoluteFret()
    {
        var parser = new GpNoteParser { Capo = 2 };

        var note = parser.ParseNote(NoteElement(0, 5, Slide(4)), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.Equal(7, note.Fret);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(2, note.SlideTo);
    }

    // ── legacy binary path (.gp3/.gp4/.gp5) ────────────────────────────────

    private static Arrangement ImportLegacy(string path)
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

    /// <summary>The binary reader converts identically — the two importers must
    /// never disagree about the same score. Capo 3: GP 0/2/5 ⇒ 0/5/8.</summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_Capo3_ShiftsFrettedNotesAndLeavesTheOpenStringAlone(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version, new[]
        {
            new FxMeasure().AddVoice(
                FxBeat.Note(0, 0, 0),
                FxBeat.Note(0, 0, 2),
                FxBeat.Note(0, 0, 5),
                FxBeat.RestBeat(0))
        }, capo: 3);

        var arr = ImportLegacy(path);

        Assert.Equal(3, arr.Capo);
        Assert.Equal(new[] { 0, 5, 8 },
            arr.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret).ToArray());
    }

    /// <summary>Capo 0 on the binary path is a strict no-op as well.</summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_Capo0_IsAStrictNoOp(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version, new[]
        {
            new FxMeasure().AddVoice(
                FxBeat.Note(0, 0, 0),
                FxBeat.Note(0, 0, 2),
                FxBeat.Note(0, 0, 5),
                FxBeat.RestBeat(0))
        }, capo: 0);

        var arr = ImportLegacy(path);

        Assert.Equal(0, arr.Capo);
        Assert.Equal(new[] { 0, 2, 5 },
            arr.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret).ToArray());
    }

    /// <summary>Chord frets and the resulting chord template convert on the
    /// binary path too.</summary>
    [Fact]
    public void Legacy_Capo3_ShiftsChordNotesAndTheChordTemplate()
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", new[]
        {
            new FxMeasure().AddVoice(
                FxBeat.Chord(0, new FxNote(0, 0), new FxNote(1, 2), new FxNote(2, 4)),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0))
        }, capo: 3);

        var arr = ImportLegacy(path);

        var chord = Assert.Single(arr.Levels[0].Chords);
        Assert.Equal(new[] { 0, 5, 7 },
            chord.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.Fret).ToArray());

        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(new[] { 0, 5, 7 },
            template.Frets.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray());
    }

    /// <summary>The binary format hangs the grace ornament on the note it
    /// decorates; its fret is capo-relative like every other.</summary>
    [Fact]
    public void Legacy_Capo3_ShiftsAGraceHintFret()
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", new[]
        {
            new FxMeasure().AddVoice(
                FxBeat.Note(0, 0, 5, grace: 3),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0))
        }, capo: 3);

        var arr = ImportLegacy(path);

        var note = Assert.Single(arr.Levels[0].Notes);
        Assert.Equal(8, note.Fret);      // 5 + 3
        Assert.Equal(6, note.GraceFret); // 3 + 3
    }

    /// <summary>
    ///     A "sl." grace materializes into a real note that slides into its
    ///     target, so BOTH frets have to be absolute: the origin (the ornament's
    ///     own fret) and the slide target (the landing note's fret).
    /// </summary>
    [Fact]
    public void Legacy_Capo3_ShiftsAMaterializedGraceSlideAndItsTarget()
    {
        // The same ornament as the hint test, but WITH the "sl." transition byte
        // — which is what promotes it from a notation hint to a real sound.
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", new[]
        {
            new FxMeasure().AddVoice(
                new FxBeat
                {
                    Duration = 0,
                    Notes = { new FxNote(0, 5, GraceFret: 3, GraceSlide: true) }
                },
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0))
        }, capo: 3);

        var notes = ImportLegacy(path).Levels[0].Notes.OrderBy(n => n.Position).ToList();

        Assert.Equal(2, notes.Count);
        Assert.Equal(6, notes[0].Fret);    // ornament origin, 3 + 3
        Assert.Equal(8, notes[0].SlideTo); // landing fret, 5 + 3
        Assert.Equal(8, notes[1].Fret);
        Assert.Null(notes[0].GraceFret);   // consumed — it is a real sound now
    }

    /// <summary>
    ///     A GP4 slide-IN is the discrete NEGATIVE value <c>-1</c>, and the GP5
    ///     bitmask spells it <c>0x10</c>. Either way it is a sentinel, never a
    ///     fret: it must reach <c>GpPostProcessor.MaterializeSlideIns</c> intact
    ///     and be resolved against the ABSOLUTE target, the default two frets below
    ///     it. No negative slide target may survive import.
    /// </summary>
    [Theory]
    [InlineData("4.06", -1)]
    [InlineData("5.10", 0x10)]
    public void Legacy_Capo3_ResolvesASlideInAgainstTheAbsoluteFret(string version, int slideValue)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version, new[]
        {
            new FxMeasure().AddVoice(
                new FxBeat { Duration = 0, Notes = { new FxNote(0, 5, Slide: slideValue) } },
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0),
                FxBeat.RestBeat(0))
        }, capo: 3);

        var notes = ImportLegacy(path).Levels[0].Notes.OrderBy(n => n.Position).ToList();

        Assert.Equal(2, notes.Count);
        Assert.Equal(6, notes[0].Fret);    // origin: the default two frets below the absolute 8
        Assert.Equal(8, notes[0].SlideTo);
        Assert.Equal(8, notes[1].Fret);    // target: 5 + 3
        Assert.All(notes, n => Assert.True(n.SlideTo is null or >= 0));
    }

    // ── the conversion itself ──────────────────────────────────────────────

    /// <summary>
    ///     The pure rule, including the two edges: fret <c>0</c> (and Guitar Pro's
    ///     negative dead-note fret) never shifts, and a sum past the model's
    ///     24-fret ceiling clamps rather than storing a fret the neck does not
    ///     have — an unrepresentable fret is rejected by the export validator and
    ///     drawn nowhere, which is strictly worse than being a semitone flat.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 0, 5)]    // capo 0 is the identity
    [InlineData(0, 2, 0)]    // open stays open
    [InlineData(2, 2, 4)]    // the measured Ghost In A Flower case
    [InlineData(-1, 2, 0)]   // dead-note fret clamps to open, unshifted
    [InlineData(23, 3, 24)]  // clamped at the ceiling
    [InlineData(24, 1, 24)]
    [InlineData(21, 3, 24)]  // exactly at the ceiling, unclamped
    public void ToAbsolute_AppliesTheCapoToFrettedNumbersOnly(int gpFret, int capo, int expected)
        => Assert.Equal(expected, GpCapoFrets.ToAbsolute(gpFret, capo));

    // ── an ABSURD capo (none of the three importers owns this number) ───────

    /// <summary>
    ///     The capo is a raw <c>ReadInt32</c> on the binary path, a
    ///     <c>&lt;Property name="CapoFret"&gt;</c> on the gpif path and a JSON
    ///     field on the Songsterr path, and all three used to admit it behind
    ///     nothing but a <c>Math.Max(0, …)</c> floor. <c>gpFret + capo</c> then
    ///     OVERFLOWED for a capo near <see cref="int.MaxValue"/>, wrapping to a
    ///     large negative that sailed straight through the <c>Math.Min(24, …)</c>
    ///     ceiling — so the one helper whose whole job is to keep a fret
    ///     representable returned about −2.1 billion.
    /// </summary>
    [Theory]
    [InlineData(5, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(5, 100_000)]
    public void ToAbsolute_NeverReturnsAnUnrepresentableFret_HoweverAbsurdTheInputs(int gpFret, int capo)
        => Assert.InRange(GpCapoFrets.ToAbsolute(gpFret, capo), 0, GpCapoFrets.MaxAbsoluteFret);

    /// <summary>Every real capo is a strict no-op through the sanitizer.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(24, 24)]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(25, 24)]
    [InlineData(int.MaxValue, 24)]
    public void Sanitize_FoldsACapoOntoTheNeck(int capo, int expected)
        => Assert.Equal(expected, GpCapoFrets.Sanitize(capo));

    /// <summary>
    ///     …and the binary importer must SANITIZE at the read rather than let the
    ///     raw number reach the arrangement: <c>Arrangement.Capo</c> is what
    ///     <c>FretAnchorGenerationService</c>'s anchor floor (<c>capo + 1</c>) and
    ///     every fret consumer fold in, and it is written to the project file.
    /// </summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_AnAbsurdCapo_IsFoldedOntoTheNeckAndLeavesEveryFretRepresentable(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version,
            new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 0), FxBeat.Note(0, 0, 5)) },
            capo: int.MaxValue);

        var arr = ImportLegacy(path);

        Assert.InRange(arr.Capo, 0, GpCapoFrets.MaxAbsoluteFret);
        Assert.All(arr.Levels[0].Notes, n => Assert.InRange(n.Fret, 0, GpCapoFrets.MaxAbsoluteFret));
    }

    /// <summary>The gpif path folds the same way — the two importers must never
    /// disagree about the same absurd score.</summary>
    [Fact]
    public void Gpif_AnAbsurdCapo_IsFoldedOntoTheNeckAndLeavesEveryFretRepresentable()
    {
        var arr = ImportSingleTrack(OpenAndFrettedRun(capo: int.MaxValue));

        Assert.InRange(arr.Capo, 0, GpCapoFrets.MaxAbsoluteFret);
        Assert.All(arr.Levels[0].Notes, n => Assert.InRange(n.Fret, 0, GpCapoFrets.MaxAbsoluteFret));
    }

    // ── the floor under every SYNTHESIZED fret ──────────────────────────────
    //
    // Two importer sites INVENT a fret rather than reading one: the unpitched
    // slide-out landing (`fret - 5`, here and in SongsterrConverter) and the
    // slide-in origin (`fret ± distance`, GpPostProcessor.MaterializeSlideIns).
    // Both floored at a hard 1, which is only the capo-0 form of the rule —
    // frets in the model are ABSOLUTE, so on a capo-5 track a GP fret 1 arrives
    // as 6 and the -5 landed on 1, five frets BELOW the capo where the string is
    // already stopped. ChartExportValidator raises KeyNoteBelowCapo at Error
    // severity for that and BLOCKS the export dialog, and GpCapoFrets.ToRelative
    // folds such a fret back to 0 on re-export, keeping the pitch and destroying
    // the gesture.

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(12, 12)]
    public void MinFrettedFret_IsTheCapoButNeverBelowOne(int capo, int expected) => Assert.Equal(expected, GpCapoFrets.MinFrettedFret(capo));

    /// <summary>Capo 5, GP fret 1 ⇒ absolute 6; the scrape's <c>-5</c> landing
    /// must stop at the capo, not at fret 1.</summary>
    [Fact]
    public void Gpif_Capo5_UnpitchedSlideTargetStopsAtTheCapo()
    {
        var parser = new GpNoteParser { Capo = 5 };

        var note = parser.ParseNote(NoteElement(0, 1, Slide(4)), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.Equal(6, note.Fret);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(5, note.SlideTo);
    }

    /// <summary>No capo ⇒ the historical fret-1 floor, unchanged.</summary>
    [Fact]
    public void Gpif_NoCapo_UnpitchedSlideTargetStillFloorsAtOne()
    {
        var parser = new GpNoteParser { Capo = 0 };

        var note = parser.ParseNote(NoteElement(0, 1, Slide(4)), FractionalPosition.Zero,
            new FractionalPosition(1), stringCount: 6, hasTremolo: false);

        Assert.Equal(1, note.SlideTo);
    }

    /// <summary>
    ///     The GP3-5 BINARY twin of the same rule, in all four of its copies
    ///     (GP3/4 discrete values 3/4, GP5 mask bits 0x04/0x08). The two importers
    ///     are measured against each other, so a floor changed on one side and not
    ///     the other is exactly the divergence that parity rule exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(3, false)] // GP3/4 "slide out down"
    [InlineData(0x04, true)] // GP5 mask, same
    public void Legacy_Capo5_UnpitchedSlideTargetStopsAtTheCapo(int slideFlag, bool isVersion5)
    {
        var note = new GuitarNote { StringIndex = 0, Fret = 6 }; // GP-relative 1 on a capo-5 track

        GpLegacyNoteEffectsHelpers.ApplySlide(slideFlag, note, isVersion5, capo: 5);

        Assert.True(note.UnpitchedSlide);
        Assert.Equal(5, note.SlideTo);
    }

    /// <summary>No capo ⇒ the historical fret-1 floor, on the binary path too.</summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(0x04, true)]
    public void Legacy_NoCapo_UnpitchedSlideTargetStillFloorsAtOne(int slideFlag, bool isVersion5)
    {
        var note = new GuitarNote { StringIndex = 0, Fret = 3 };

        GpLegacyNoteEffectsHelpers.ApplySlide(slideFlag, note, isVersion5);

        Assert.Equal(1, note.SlideTo);
    }

    /// <summary>A slide-in whose synthesized ORIGIN would fall below the capo is
    /// dropped — the same shape the pass already used for an origin below fret 1
    /// — instead of fabricating an unplayable note.</summary>
    [Fact]
    public void MaterializeSlideIns_OriginBelowTheCapo_IsDropped()
    {
        var level = new ChartLevel();
        var target = SlideInTarget(fret: 6);
        level.Notes.Add(target);

        GpPostProcessor.MaterializeSlideIns(level, capo: 5);

        var note = Assert.Single(level.Notes);
        Assert.Same(target, note);
        Assert.Null(note.SlideTo); // the sentinel is consumed either way
    }

    /// <summary>The negative case: with room above the capo it still
    /// materializes, so the floor is not "drop every capo'd slide-in".</summary>
    [Fact]
    public void MaterializeSlideIns_OriginAtOrAboveTheCapo_StillMaterializes()
    {
        var level = new ChartLevel();
        var target = SlideInTarget(fret: 9);
        level.Notes.Add(target);

        GpPostProcessor.MaterializeSlideIns(level, capo: 5);

        var origin = Assert.Single(level.Notes, n => !ReferenceEquals(n, target));
        Assert.Equal(7, origin.Fret);
        Assert.True(origin.Fret >= 5, "a synthesized origin never sits below the capo");
    }

    /// <summary>Capo 0 is a strict no-op: fret 2 minus the default distance 2 is
    /// fret 0, which the fret-1 floor has always dropped.</summary>
    [Fact]
    public void MaterializeSlideIns_NoCapo_KeepsTheFretOneFloor()
    {
        var level = new ChartLevel();
        level.Notes.Add(SlideInTarget(fret: 2));

        GpPostProcessor.MaterializeSlideIns(level);

        Assert.Single(level.Notes);
    }

    private static GuitarNote SlideInTarget(int fret) => new()
    {
        Position = new FractionalPosition(4),
        Sustain = new FractionalPosition(1),
        StringIndex = 0,
        Fret = fret,
        SlideTo = GpNoteParser.SlideInFromBelow
    };
}
