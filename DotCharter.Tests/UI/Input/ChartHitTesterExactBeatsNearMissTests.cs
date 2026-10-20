using Avalonia;
using DotCharter.UI.Constants;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The near-miss ring (<see cref="ChartEditorConstants.NearMissHitTolerance" />)
///     is a BACKSTOP for a click that hit nothing. It must never outrank a click
///     that hit something exactly — and it did, in one direction, because every
///     press path runs the CHORD tester first and then lets a chord win
///     unconditionally.
///     <para>
///         The viewport below is the shape that makes it visible and is not
///         exotic: 6 strings over a 108 px lane area is an 18 px lane pitch,
///         while a gem is <c>2 × (BaseNoteRadius + NoteStrokeThickness / 2)</c> =
///         26 px across. Gems taller than their lanes are the normal case in a
///         stacked track row or a short editor pane, so a chord note in the lane
///         ABOVE is 5 px from a click that is dead centre on a single note —
///         inside the 6 px ring. The chord was selected; the note the user
///         clicked was not, and every verb after it (drag, delete, the context
///         menu, the inspector) followed the chord.
///     </para>
/// </summary>
public sealed class ChartHitTesterExactBeatsNearMissTests
{
    private const double ViewAnchorMs = 450.0; // pixelsPerMs 1 + PlayheadX 450 => viewStartMs 0
    private const double PixelsPerMs = 1.0;
    private const double Width = 1000.0;

    // 6 lanes over (148 - 20 - 20) = 108 px => an 18 px pitch under a 26 px gem.
    private const double Height = 148.0;
    private const double TopMargin = 20.0;
    private const double BottomMargin = 20.0;
    private const int StringCount = 6;
    private const double SoundMs = 500.0;

    private static List<BeatMarker> Beats(int count = 32, double bpm = 120)
        => [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * (60000.0 / bpm), Bpm = bpm })];

    private static double LaneY(int stringIndex)
        => ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, stringIndex, leftyFlip: false);

    private static GuitarNote NoteOn(int stringIndex) => new()
    {
        Position = TimelineMath.PositionFromMs(SoundMs, Beats()),
        StringIndex = stringIndex,
        Fret = 5
    };

    private static Chord ChordOn(params int[] strings)
    {
        var chord = new Chord { Position = TimelineMath.PositionFromMs(SoundMs, Beats()) };
        foreach (var s in strings)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 3 });
        return chord;
    }

    [Fact]
    public void TheLanePitchIsSmallerThanAGem_WhichIsWhatMakesThisReachable()
    {
        // Not decoration: if this stops holding the two cases below become
        // vacuous, because nothing would be inside the other's ring.
        var pitch = (Height - TopMargin - BottomMargin) / StringCount;
        Assert.True(pitch < ChartEditorConstants.NoteHeadHeight,
            $"lane pitch {pitch} is no longer under the {ChartEditorConstants.NoteHeadHeight} px gem");
        Assert.True(pitch - ChartEditorConstants.NoteHeadHeight / 2.0 <= ChartEditorConstants.NearMissHitTolerance,
            "the neighbouring lane's centre is outside the near-miss ring");
    }

    [Fact]
    public void AChordOneLaneAway_IsOnlyANearMiss_WhileTheNoteUnderThePointerIsExact()
    {
        var p = new Point(SoundMs, LaneY(3));

        var note = ChartHitTester.HitTestNote(p, [NoteOn(3)], ViewAnchorMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), leftyFlip: false, out var noteExact);
        var chord = ChartHitTester.HitTestChord(p, [ChordOn(1, 2)], ViewAnchorMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), leftyFlip: false, out var chordExact);

        Assert.NotNull(note);
        Assert.True(noteExact, "the pointer is on the gem's centre and the tester called it inexact");
        Assert.NotNull(chord);
        Assert.False(chordExact, "the chord is a lane away — it can only be a near miss");
    }

    [Fact]
    public void AnExactNote_OutranksANearMissChord()
    {
        var note = NoteOn(3);
        var chord = ChordOn(1, 2);

        var (resolvedChord, resolvedNote) =
            ChartHitTester.ResolveSound(chord, chordExact: false, note, noteExact: true);

        Assert.Null(resolvedChord);
        Assert.Same(note, resolvedNote);
    }

    [Fact]
    public void AnExactChord_StillOutranksAnExactNote()
    {
        // The historical precedence, unchanged: at one column a chord IS the
        // sound, and a chord note shares the gem the note tester would report.
        var note = NoteOn(3);
        var chord = ChordOn(3);

        var (resolvedChord, resolvedNote) =
            ChartHitTester.ResolveSound(chord, chordExact: true, note, noteExact: true);

        Assert.Same(chord, resolvedChord);
        Assert.Same(note, resolvedNote);
    }

    [Fact]
    public void TwoNearMisses_StillLetTheChordWin()
    {
        // The near-miss pass's own job is untouched — it decides between
        // INEXACT candidates, and there the chord keeps precedence.
        var note = NoteOn(3);
        var chord = ChordOn(1, 2);

        var (resolvedChord, _) = ChartHitTester.ResolveSound(chord, chordExact: false, note, noteExact: false);

        Assert.Same(chord, resolvedChord);
    }

    [Fact]
    public void AnExactSustainHit_CountsAsExact()
    {
        // The tail is part of the sound, not a near miss — a click on a long
        // note's tube must beat a chord grazing the lane above it.
        var beats = Beats();
        var note = new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(SoundMs, beats),
            Sustain = TimelineMath.PositionFromMs(SoundMs + 400, beats) - TimelineMath.PositionFromMs(SoundMs, beats),
            StringIndex = 3,
            Fret = 5
        };

        // 200 ms into the tail, well clear of the head box.
        var p = new Point(SoundMs + 200, LaneY(3));
        var hit = ChartHitTester.HitTestNote(p, [note], ViewAnchorMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, beats, leftyFlip: false, out var exact);

        Assert.Same(note, hit);
        Assert.True(exact, "a click inside the drawn sustain tube was reported as a near miss");
    }
}
