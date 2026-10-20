namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     "Convert to power chord" derives the 5th (and, big, the octave) from the
///     selected note's PITCH — and it derived that pitch on the un-capoed neck.
///     Frets are absolute and fret <c>0</c> means "open", which a capo stops at
///     the capo fret, so on a capo'd arrangement the tool both computed the wrong
///     root and wrote frets BELOW the capo: physically unplayable, and exactly
///     what inspector rule <c>L01</c> /
///     <c>ChartExportValidator.KeyNoteBelowCapo</c> flags.
/// </summary>
public sealed class PowerChordCapoTests
{
    /// <summary>
    ///     Capo <c>0</c> — which is nearly every chart — must be byte-identical
    ///     to the capo-blind version.
    /// </summary>
    [Fact]
    public void Capo0_IsAStrictNoOp()
    {
        Assert.Equal(
            new Dictionary<int, int> { { 0, 5 }, { 1, 7 }, { 2, 7 } },
            Convert(capo: 0, stringIndex: 0, fret: 5, big: true));

        Assert.Equal(
            new Dictionary<int, int> { { 0, 0 }, { 1, 2 } },
            Convert(capo: 0, stringIndex: 0, fret: 0, big: false));
    }

    /// <summary>
    ///     An OPEN string under a capo sounds AT the capo, so its 5th is the 5th
    ///     of that note. Capo-blind, the tool took the open-nut pitch and put the
    ///     5th at fret 2 — on a capo-5 neck a sub-capo fret: the wrong pitch AND
    ///     a note the hand cannot play.
    /// </summary>
    [Fact]
    public void AnOpenStringUnderACapo_TakesItsFifthFromTheSoundingPitch()
    {
        var frets = Convert(capo: 5, stringIndex: 0, fret: 0, big: false);

        // Open low E capo'd at 5 sounds A2; its 5th is E3 = A-string fret 7.
        Assert.Equal(new Dictionary<int, int> { { 0, 0 }, { 1, 7 } }, frets);
    }

    [Fact]
    public void AnOpenStringUnderACapo_TakesItsOctaveFromTheSoundingPitchToo()
    {
        var frets = Convert(capo: 5, stringIndex: 0, fret: 0, big: true);

        Assert.Equal(new Dictionary<int, int> { { 0, 0 }, { 1, 7 }, { 2, 7 } }, frets);
    }

    /// <summary>
    ///     When the interval is unreachable on the next string because the capo
    ///     already stops it higher up, the tool must DECLINE the note rather than
    ///     write the sub-capo fret. The old guard only refused NEGATIVE frets.
    ///     <para>
    ///         Needs a tuning whose adjacent-string interval exceeds a fifth —
    ///         <c>Arrangement.TuningPitches</c> is arbitrary per string, and the
    ///         same arithmetic that makes this shape legal uncapoed puts it under
    ///         a capo of 4.
    ///     </para>
    /// </summary>
    [Fact]
    public void AFifthThatWouldLandBelowTheCapo_IsRefused()
    {
        // String 1 tuned UP three semitones: the 0→1 interval is now 8 semitones,
        // so the 5th sits one fret BELOW its root.
        int[] tuning = [0, 3, 0, 0, 0, 0];

        // Uncapoed this is a perfectly good shape.
        Assert.Equal(
            new Dictionary<int, int> { { 0, 4 }, { 1, 3 } },
            Convert(capo: 0, stringIndex: 0, fret: 4, big: false, tuning));

        // With the capo at 4, fret 3 is under it — unplayable, so: no chord.
        Assert.Null(Convert(capo: 4, stringIndex: 0, fret: 4, big: false, tuning));
    }

    /// <summary>
    ///     The property the cases above are instances of: over every capo, string
    ///     and legally-fretted root, nothing the tool DERIVES may be a sub-capo
    ///     fret. (The root fret is written verbatim — a malformed authored note
    ///     is not the tool's to invent or to fix, so roots that are themselves
    ///     under the capo are excluded.)
    /// </summary>
    [Fact]
    public void NoConvertedPowerChord_EverDerivesAFretBelowTheCapo()
    {
        for (var capo = 0; capo <= 7; capo++)
        {
            for (var stringIndex = 0; stringIndex <= 3; stringIndex++)
            {
                for (var fret = 0; fret <= 12; fret++)
                {
                    if (fret != 0 && fret < capo) continue; // malformed root, not our business
                    foreach (var big in new[] { false, true })
                    {
                        var frets = Convert(capo, stringIndex, fret, big);
                        if (frets == null) continue;
                        foreach (var (str, f) in frets)
                        {
                            if (str == stringIndex) continue;
                            Assert.True(f >= capo,
                                $"capo={capo} root=({stringIndex},{fret}) big={big} derived string {str} at fret {f}.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Runs the real coordinator verb and returns the template it wrote, or
    ///     <c>null</c> when it declined the note.
    /// </summary>
    private static Dictionary<int, int>? Convert(
        int capo, int stringIndex, int fret, bool big, int[]? tuning = null)
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        arrangement.Capo = capo;
        for (var i = 0; i < arrangement.StringCount; i++)
            arrangement.TuningPitches.Add(new TuningOffset(tuning is null ? 0 : tuning[i]));

        var level = arrangement.Levels[0];
        level.Notes.Clear();
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = stringIndex, Fret = fret };
        level.Notes.Add(note);
        h.Selection.SelectedNotes.Add(note);

        h.Maintenance.ConvertSelectedNotesToPowerChord(big);

        if (level.Chords.Count == 0) return null;
        var template = arrangement.ChordTemplates.Single(t => t.Id == level.Chords[0].TemplateId);
        return template.Frets;
    }
}
