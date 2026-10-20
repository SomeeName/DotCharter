using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Highway.HighwayCommands;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     The keyboard fret verbs and the CAPO.
///     <para>
///         <c>SelectedNoteMutationCommands</c>' own summary claimed these verbs
///         were "capo-agnostic by construction" because fret 0 is the capo. Half
///         of that is true — fret 0 IS the open, capo'd string — and the other
///         half is the bug: a NON-ZERO fret below the capo is malformed,
///         inspector rule <c>L01</c> (<c>NoteFretBelowCapo</c>). A plain
///         <c>Math.Clamp(fret, 0, 24)</c> wrote one on every capo'd chart, and
///         because <c>ArrangementAutoFixService.FixCapoFrets</c> raises it
///         silently on the next auto-fix, the keystroke and the correction were
///         separated by an unrelated edit — which is why nobody caught it.
///     </para>
///     <para>
///         The legal set is <c>{0} ∪ [capo, 24]</c>
///         (<see cref="SoundingPitch.SnapToNeck" />), and <b>capo 0 is a strict
///         no-op</b>: every other test in the suite pins the un-capo'd
///         behaviour and none of them moved.
///     </para>
/// </summary>
public sealed class CapoFretVerbTests
{
    // ── the pure rule ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(24, 24)]
    [InlineData(30, 24)]
    [InlineData(-4, 0)]
    public void WithNoCapoTheSnapIsAPlainClamp(int fret, int expected) => Assert.Equal(expected, SoundingPitch.SnapToNeck(fret, 0));

    [Fact]
    public void AnAbsoluteWriteBehindTheCapoLandsOnTheCapo()
    {
        // The same answer FixCapoFrets gives: fret > 0 && fret < capo => capo.
        Assert.Equal(5, SoundingPitch.SnapToNeck(3, 5));
        Assert.Equal(5, SoundingPitch.SnapToNeck(1, 5));
    }

    [Fact]
    public void ADescentOffTheBackOfTheCapoLandsOpen()
    {
        // A fret-down run has to stay monotone and still be able to reach the
        // open string; landing it back on the capo would make FretDown a no-op
        // at the capo and put fret 0 out of the keyboard's reach entirely.
        Assert.Equal(0, SoundingPitch.SnapToNeck(4, 5, -1));
        Assert.Equal(0, SoundingPitch.SnapToNeck(0, 5, -1));
    }

    [Fact]
    public void AnOpenStringIsAlwaysLegalAndTheFrettedRangeIsUntouched()
    {
        Assert.Equal(0, SoundingPitch.SnapToNeck(0, 5));
        Assert.Equal(5, SoundingPitch.SnapToNeck(5, 5));
        Assert.Equal(24, SoundingPitch.SnapToNeck(24, 5));
    }

    // ── the verbs ───────────────────────────────────────────────────────────

    [Fact]
    public void ATypedFretBehindTheCapoIsRaisedToTheCapo()
    {
        var h = Capoed(5);
        var note = h.AddNote(2, 0, 7);
        h.Selection.SelectedNotes.Add(note);

        h.Service.SetSelectedFret(3);

        Assert.Equal(5, note.Fret);
    }

    [Fact]
    public void ATypedZeroIsStillTheOpenString()
    {
        var h = Capoed(5);
        var note = h.AddNote(2, 0, 7);
        h.Selection.SelectedNotes.Add(note);

        h.Service.SetSelectedFret(0);

        Assert.Equal(0, note.Fret);
    }

    [Fact]
    public void FretDownFromTheCapoReachesTheOpenStringInsteadOfStalling()
    {
        var h = Capoed(5);
        var note = h.AddNote(2, 0, 5);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretDown();

        Assert.Equal(0, note.Fret);
    }

    [Fact]
    public void FretUpFromTheOpenStringLandsOnTheFirstFretThatExists()
    {
        var h = Capoed(5);
        var note = h.AddNote(2, 0, 0);
        h.Selection.SelectedNotes.Add(note);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(5, note.Fret);
    }

    [Fact]
    public void AnOctaveDropNeverLandsBehindTheCapo()
    {
        // capo 5, fret 9: 9 - 12 = -3, clamped to 0 => open, which is legal.
        // capo 5, fret 15: 15 - 12 = 3, which is the L01 the clamp used to write.
        var h = Capoed(5);
        var low = h.AddNote(2, 0, 15);
        h.Selection.SelectedNotes.Add(low);

        h.Service.MoveSelectedFretByOctave(-1);

        Assert.Equal(0, low.Fret);
        Assert.True(low.Fret is 0 or >= 5, $"fret {low.Fret} is behind the capo (L01)");
    }

    [Fact]
    public void AMaskedFretChangeOnAChordStaysOnTheNeck()
    {
        var h = Capoed(5);
        var chord = h.AddChord(2, templateId: -1, (0, 7), (1, 9));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(0);

        h.Service.SetSelectedFret(2);

        Assert.Equal(new[] { 5, 9 }, chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void TransposingAShapeBehindTheCapoRaisesTheWholeGrip()
    {
        // The pen's NO-MASK typed fret: the number becomes the shape's new
        // LOWEST fret and the grip follows. Behind the capo the number is first
        // snapped onto a fret that exists, so the shape lands on the neck
        // instead of two frets behind the capo.
        var h = Capoed(5);
        var chord = h.AddChord(2, templateId: -1, (0, 10), (1, 12));
        h.Selection.SelectedChords.Add(chord);

        TransposeTo(h, chord, 3);

        Assert.Equal(new[] { 5, 7 }, chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void ATransposeThatWouldStrandAStringBehindTheCapoIsRefused()
    {
        // Same refusal as running off the 24th, for the same reason: snapping
        // the stranded string forward onto the capo squashes the grip into a
        // DIFFERENT chord. 10/12 moved down to fret 0 would leave the second
        // string on fret 2, which the neck has not got.
        var h = Capoed(5);
        var chord = h.AddChord(2, templateId: -1, (0, 10), (1, 12));
        h.Selection.SelectedChords.Add(chord);

        TransposeTo(h, chord, 0);

        Assert.Equal(new[] { 10, 12 }, chord.ChordNotes.Select(n => n.Fret));
    }

    private static void TransposeTo(ChordKeyboardMutationTests.Harness h, Chord chord, int fret)
    {
        var targets = new SelectedSoundMutationTargets(
            h.State.PlacedNotes, h.State.PlacedChords, [], [chord],
            h.Arrangement, h.State.StringEditMask, null);
        SelectedNoteMutationCommands.TransposeShapeTo(targets, fret).Execute(h.History);
    }

    [Fact]
    public void WithNoCapoEveryVerbIsExactlyWhatItWas()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var note = h.AddNote(2, 0, 3);
        h.Selection.SelectedNotes.Add(note);

        h.Service.SetSelectedFret(1);
        Assert.Equal(1, note.Fret);

        h.Service.MoveSelectedFretDown();
        Assert.Equal(0, note.Fret);

        h.Service.MoveSelectedFretUp();
        Assert.Equal(1, note.Fret);

        h.Service.MoveSelectedFretByOctave(1);
        Assert.Equal(13, note.Fret);
    }

    private static ChordKeyboardMutationTests.Harness Capoed(int capo)
    {
        var h = new ChordKeyboardMutationTests.Harness();
        h.Arrangement.Capo = capo;
        return h;
    }
}
