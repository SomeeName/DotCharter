using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// Cross-tool fidelity gaps found by diffing the LEGACY binary (.gp3/.gp4/.gp5)
/// importer against the GPIF (.gp) importer on the same score — the GPIF path is
/// the one measured note-for-note against Editor-on-Fire and Log Charter, so any
/// disagreement between our own two importers is a defect in this one.
/// <list type="bullet">
///     <item>GP3/GP4 spell the two slide-INs as NEGATIVE slide values (−1 from
///     below, −2 from above). The reader's pre-GP5 switch only handled the four
///     positive out-slides, so every pre-GP5 slide-in was silently dropped.</item>
///     <item>A slide OUT and a slide IN are independent slots in GP5's bitmask
///     but our model has ONE <c>SlideTo</c>, so out wins — the same precedence
///     <c>GpNoteParser.ApplySlideFlags</c> applies on the GPIF path.</item>
///     <item>Triplet feel (swing) was read and discarded on the legacy path, so
///     <c>GpBarInfo.TripletFeel</c> was always empty for a binary file.</item>
///     <item>A grace ornament on a CHORD was dropped outright: the binary format
///     hangs the grace on each chord note, and the chord builder never copied
///     it. That cost the reference file its three chord grace SLIDES.</item>
/// </list>
/// Fixtures are authored in code by <see cref="LegacyGpFixtureBuilder"/>; no test
/// here depends on a file outside the repository.
/// </summary>
public sealed class GpLegacySlideAndFeelTests
{
    private static GpImportResult Import(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static ChartLevel ImportLevel(string path)
        => Assert.Single(Assert.Single(Import(path).Arrangements).Levels);

    // ---- Slide value decoding (pure) -------------------------------------

    /// <summary>
    ///     GP3/GP4 encode the two slide-INs as negative values. Log Charter's
    ///     <c>GP5FileReader.readSlide()</c> pre-500 branch reads them as
    ///     <c>case -1 -> IN_FROM_BELOW</c> / <c>case -2 -> IN_FROM_ABOVE</c>;
    ///     ours dropped straight through the switch.
    /// </summary>
    [Theory]
    [InlineData(-1, GpNoteParser.SlideInFromBelow)]
    [InlineData(-2, GpNoteParser.SlideInFromAbove)]
    public void Gp4SlideIn_ParksTheMaterializerSentinel(int slideValue, int expectedSentinel)
    {
        var note = new GuitarNote { Fret = 5 };

        GpLegacyNoteEffectsHelpers.ApplySlide(slideValue, note, isVersion5: false);

        Assert.Equal(expectedSentinel, note.SlideTo);
        Assert.False(note.UnpitchedSlide); // a slide-in is pitched
        Assert.False(note.LinkNext);       // the materialized ORIGIN carries the link
    }

    /// <summary>The four positive GP3/GP4 out-slides keep their existing meaning —
    /// adding the negative cases must not disturb them.</summary>
    [Theory]
    [InlineData(1, -1, false)]  // shift: target resolved later from the -1 marker
    [InlineData(2, -1, false)]  // legato: same, plus LinkNext (asserted below)
    [InlineData(3, 5, true)]    // out down five frets from 10
    [InlineData(4, 15, true)]   // out up five frets from 10
    public void Gp4SlideOut_KeepsItsExistingMeaning(int slideValue, int expectedSlideTo, bool expectedUnpitched)
    {
        var note = new GuitarNote { Fret = 10 };

        GpLegacyNoteEffectsHelpers.ApplySlide(slideValue, note, isVersion5: false);

        Assert.Equal(expectedSlideTo, note.SlideTo);
        Assert.Equal(expectedUnpitched, note.UnpitchedSlide);
        Assert.Equal(slideValue == 2, note.LinkNext);
    }

    /// <summary>An unknown/absent slide value leaves the note completely alone.</summary>
    [Fact]
    public void Gp4UnknownSlideValue_LeavesTheNoteUntouched()
    {
        var note = new GuitarNote { Fret = 10 };

        GpLegacyNoteEffectsHelpers.ApplySlide(0, note, isVersion5: false);

        Assert.Null(note.SlideTo);
        Assert.False(note.UnpitchedSlide);
    }

    /// <summary>GP5's slide-IN bits alone park the same sentinels.</summary>
    [Theory]
    [InlineData(0x10, GpNoteParser.SlideInFromBelow)]
    [InlineData(0x20, GpNoteParser.SlideInFromAbove)]
    public void Gp5SlideInBits_ParkTheMaterializerSentinel(int flags, int expectedSentinel)
    {
        var note = new GuitarNote { Fret = 5 };

        GpLegacyNoteEffectsHelpers.ApplySlide(flags, note, isVersion5: true);

        Assert.Equal(expectedSentinel, note.SlideTo);
    }

    /// <summary>
    ///     GP5 can flag a slide OUT and a slide IN on one note (Log Charter reads
    ///     them into two independent slots). <see cref="GuitarNote.SlideTo"/> is a
    ///     single <c>int?</c>, so exactly one survives and it is the OUT slide —
    ///     locking in the decision so a later "fix" cannot silently make the two
    ///     DotCharter importers disagree, because <c>GpNoteParser.ApplySlideFlags</c>
    ///     resolves the same collision the same way on the GPIF path.
    /// </summary>
    [Theory]
    [InlineData(0x01 | 0x10, -1, false)] // shift out + in from below
    [InlineData(0x02 | 0x20, -1, false)] // legato out + in from above
    [InlineData(0x04 | 0x10, 5, true)]   // unpitched out-down + in from below
    [InlineData(0x08 | 0x20, 15, true)]  // unpitched out-up + in from above
    public void Gp5SlideOutAndSlideInTogether_KeepsTheOutSlide(int flags, int expectedSlideTo,
        bool expectedUnpitched)
    {
        var note = new GuitarNote { Fret = 10 };

        GpLegacyNoteEffectsHelpers.ApplySlide(flags, note, isVersion5: true);

        // Whatever the expectation is, it is never one of the slide-in sentinels:
        // an out-slide claimed the note and the sentinel must not leak past it.
        Assert.Equal(expectedSlideTo, note.SlideTo);
        Assert.Equal(expectedUnpitched, note.UnpitchedSlide);
    }

    // ---- Slide-in end to end on a GP4 file --------------------------------

    /// <summary>
    ///     The whole GP4 path: a −1 slide value must arrive as a REAL pitched
    ///     lead-in below the target (<c>GpPostProcessor.MaterializeSlideIns</c>),
    ///     exactly what the GPIF path produces for <c>Slide=16</c>. Before the fix
    ///     the file imported as a bare note with no slide at all. The origin sits at
    ///     the default distance of TWO frets below (owner default; EOF uses 1 —
    ///     both distances are pinned directly on the materializer elsewhere).
    /// </summary>
    [Fact]
    public void Gp4SlideInFromBelow_ImportsAsAPitchedLeadIn()
    {
        var measure = new FxMeasure().AddVoice(
            new FxBeat { Duration = 0, Notes = { new FxNote(0, 5, Slide: -1) } });

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("4.06", new[] { measure }));

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);

        Assert.Equal(3, notes[0].Fret); // target 5 minus the default two frets
        Assert.Equal(5, notes[0].SlideTo);
        Assert.True(notes[0].LinkNext);
        Assert.Equal(FractionalPosition.Zero, notes[0].Position);

        Assert.Equal(5, notes[1].Fret);
        Assert.Null(notes[1].SlideTo); // the sentinel never survives the pass
        Assert.Equal(notes[0].Position + notes[0].Sustain, notes[1].Position);
    }

    /// <summary>The mirror case: −2 arrives from ABOVE the target (default two frets).</summary>
    [Fact]
    public void Gp4SlideInFromAbove_ImportsAsAPitchedLeadIn()
    {
        var measure = new FxMeasure().AddVoice(
            new FxBeat { Duration = 0, Notes = { new FxNote(0, 7, Slide: -2) } });

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("4.06", new[] { measure }));

        var notes = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(9, notes[0].Fret); // target 7 plus the default two frets
        Assert.Equal(7, notes[0].SlideTo);
        Assert.True(notes[0].LinkNext);
    }

    // ---- Triplet feel (swing) --------------------------------------------

    /// <summary>
    ///     GP5 carries the feel per measure header: 1 = eighth, 2 = sixteenth,
    ///     anything else none (Log Charter <c>GP5FileReader.readMasterBar</c>). The
    ///     byte was read and thrown away, so a swung binary file reached the beat
    ///     map with no feel at all. The strings must be the GPIF spellings — that
    ///     is the vocabulary <c>GpBarInfo.TripletFeel</c> is compared against.
    /// </summary>
    [Fact]
    public void Gp5TripletFeel_IsCapturedPerBarWithTheGpifSpelling()
    {
        var straight = new FxMeasure { TripletFeel = 0 }.AddVoice(FxBeat.Note(0, 0, 5));
        var swungEighths = new FxMeasure { TripletFeel = 1 }.AddVoice(FxBeat.Note(0, 0, 5));
        var swungSixteenths = new FxMeasure { TripletFeel = 2 }.AddVoice(FxBeat.Note(0, 0, 5));

        var result = Import(LegacyGpFixtureBuilder.WriteFile("5.10",
            new[] { straight, swungEighths, swungSixteenths }));

        Assert.Equal(new[] { "", "Triplet8th", "Triplet16th" },
            result.Metadata.Bars.Select(b => b.TripletFeel).ToArray());
    }

    /// <summary>
    ///     GP3/GP4 have no per-bar feel — they carry ONE song-wide shuffle boolean
    ///     in the pre-song block, which Log Charter reads as
    ///     <c>readBoolean ? EIGHTH : NONE</c>. It applies to every bar.
    /// </summary>
    [Theory]
    [InlineData("4.06", true, "Triplet8th")]
    [InlineData("4.06", false, "")]
    [InlineData("3.00", true, "Triplet8th")]
    [InlineData("3.00", false, "")]
    public void Gp3And4Shuffle_IsSongWideAndStampsEveryBar(string version, bool shuffle, string expected)
    {
        var measures = new[]
        {
            new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5)),
            new FxMeasure().AddVoice(FxBeat.Note(0, 0, 7))
        };

        var result = Import(LegacyGpFixtureBuilder.WriteFile(version, measures, globalShuffle: shuffle));

        Assert.Equal(2, result.Metadata.Bars.Count);
        Assert.All(result.Metadata.Bars, bar => Assert.Equal(expected, bar.TripletFeel));
    }

    // ---- Chord graces ------------------------------------------------------

    /// <summary>
    ///     A "sl." grace on a STRUM must materialize a real origin chord that
    ///     slides into it — the GPIF path does exactly this
    ///     (<c>GpBeatCarry.ApplyPendingGrace</c>), and EOF and Log Charter
    ///     both emit it. The binary path dropped the ornament entirely because it
    ///     arrives on each chord note and the chord builder copied only the
    ///     playable fields, which is what made the .gp5 import of the cross-tool
    ///     reference file three pitched slides short of the .gp import.
    /// </summary>
    [Fact]
    public void ChordGraceSlide_MaterializesAnOriginChordThatSlidesIn()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(0,
                new FxNote(0, 7, GraceFret: 5, GraceSlide: true),
                new FxNote(1, 7, GraceFret: 5, GraceSlide: true)));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }));

        var chords = level.Chords.OrderBy(c => c.Position).ToList();
        Assert.Equal(2, chords.Count);

        var origin = chords[0];
        Assert.Equal(2, origin.ChordNotes.Count);
        Assert.All(origin.ChordNotes, cn =>
        {
            Assert.Equal(5, cn.Fret);
            Assert.Equal(7, cn.SlideTo);
            Assert.True(cn.LinkNext);
        });

        // The ornament sits ON the beat and pushes the strum (there is no room
        // ahead of bar start for a before-beat grace), so the target moved.
        var target = chords[1];
        Assert.True(target.Position.CompareTo(origin.Position) > 0);
        Assert.All(target.ChordNotes, cn => Assert.Equal(7, cn.Fret));
        // The hint is consumed — it became a real note, it is not also an ornament.
        Assert.All(target.ChordNotes, cn => Assert.Null(cn.GraceFret));
    }

    /// <summary>
    ///     A PLAIN (non-slide) chord grace has no playable gesture, so it stays the
    ///     notation-only per-string hint instead of being dropped — same rule the
    ///     single-note path has always had, and the same one the GPIF path applies.
    /// </summary>
    [Fact]
    public void PlainChordGrace_StaysAPerStringNotationHint()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(0,
                new FxNote(0, 7, GraceFret: 5),
                new FxNote(1, 7, GraceFret: 5)));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }));

        var chord = Assert.Single(level.Chords);
        Assert.Equal(FractionalPosition.Zero, chord.Position); // nothing was pushed
        Assert.All(chord.ChordNotes, cn =>
        {
            Assert.Equal(7, cn.Fret);
            Assert.Equal(5, cn.GraceFret);
            Assert.Null(cn.SlideTo);
        });
    }

    // ---- Auto-FHP: the capo argument ---------------------------------------

    /// <summary>
    ///     The binary path generated anchors WITHOUT the arrangement's capo while
    ///     the gpif and Songsterr paths both passed it, so a capo'd binary import
    ///     laid anchors below the capo — frets that are physically open there, and
    ///     which validation rule L02 flags. A capo of 0 is a strict no-op, so this
    ///     only ever changed output for scores that were already wrong.
    /// </summary>
    [Fact]
    public void CapoedBinaryImport_LaysNoAnchorBelowTheCapo()
    {
        // Fret 2 sits UNDER a capo of 3 — that is the note whose anchor used to
        // land at fret 2 (below the capo) because the generator ran with capo 0.
        var measure = new FxMeasure().AddVoice(
            FxBeat.Note(0, 0, 7),
            FxBeat.Note(0, 1, 2),
            FxBeat.Note(0, 0, 9),
            FxBeat.Note(0, 2, 2));

        var arr = Assert.Single(Import(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }, capo: 3))
            .Arrangements);
        var level = Assert.Single(arr.Levels);

        Assert.Equal(3, arr.Capo);
        Assert.NotEmpty(level.Anchors);
        Assert.All(level.Anchors, anchor => Assert.True(anchor.Fret > arr.Capo,
            $"anchor at fret {anchor.Fret} sits at or below the capo ({arr.Capo})"));
    }

    /// <summary>
    ///     A grace that lands on the fret it decorates (or on an open string)
    ///     cannot slide anywhere, so it degrades to the hint rather than
    ///     materializing a same-fret origin chord — the same guard the single-note
    ///     materializer uses.
    /// </summary>
    [Fact]
    public void SameFretChordGraceSlide_DegradesToAHint()
    {
        var measure = new FxMeasure().AddVoice(
            FxBeat.Chord(0,
                new FxNote(0, 7, GraceFret: 7, GraceSlide: true),
                new FxNote(1, 7, GraceFret: 7, GraceSlide: true)));

        var level = ImportLevel(LegacyGpFixtureBuilder.WriteFile("5.10", new[] { measure }));

        var chord = Assert.Single(level.Chords);
        Assert.All(chord.ChordNotes, cn => Assert.Equal(7, cn.GraceFret));
    }
}
