using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The chart synth's upload (ABI v38): what the engine is told each note SOUNDS,
///     when it starts and ends, and how it is played. Pure — no engine — so every
///     rule is pinned by the default gate.
/// </summary>
public sealed class ChartNoteEventBuilderTests
{
    /// <summary>120 bpm: one beat every 500 ms, 40 beats.</summary>
    internal static List<BeatMarker> Beats(int count = 40)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 });
        return beats;
    }

    internal static GuitarNote Note(int beat, int stringIndex, int fret, int sustainBeats = 0)
        => new()
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIndex,
            Fret = fret,
            Sustain = new FractionalPosition(sustainBeats),
        };

    private static ChartNoteUpload Build(Arrangement arr, IList<GuitarNote> notes, IList<Chord>? chords = null,
        IList<HandShape>? hand = null)
        => ChartNoteEventBuilder.Build(arr, notes, chords ?? [], Beats(), hand, 120);

    [Fact]
    public void AGuitarNote_SoundsItsStandardTuningPitch_AtItsBeatTime()
    {
        var up = Build(new Arrangement(), [Note(2, 0, 0), Note(4, 5, 5)]);
        Assert.Equal(2, up.Notes.Length);
        Assert.Equal(40f, up.Notes[0].Pitch);          // open low E = E2
        Assert.Equal(1.0, up.Notes[0].StartSeconds, 9); // beat 2 at 500 ms per beat
        Assert.Equal(69f, up.Notes[1].Pitch);          // high E, fret 5 = A4
        Assert.Equal(-1, up.Notes[0].ChordId);
        Assert.Equal(-1f, up.Notes[0].SlideToFret);
        Assert.Equal(-1, up.Notes[0].BendFirst);
        Assert.Equal(0, up.Notes[0].BendCount);
        Assert.Equal(0, up.Notes[0].Capo);
    }

    [Fact]
    public void ABass_SoundsAnOctaveBelow()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        var up = Build(bass, [Note(1, 0, 0), Note(2, 3, 2)]);
        Assert.Equal(28f, up.Notes[0].Pitch);   // E1
        Assert.Equal(45f, up.Notes[1].Pitch);   // G2 + 2 = A2
        Assert.All(up.Notes, n => Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.Bass)));
    }

    [Fact]
    public void TuningOffsets_CapoAndCentOffset_AreInThePitch()
    {
        var dropD = new Arrangement { Capo = 2, CentOffset = -50 };
        for (var i = 0; i < 6; i++) dropD.TuningPitches.Add(new TuningOffset(i == 0 ? -2 : 0));
        var up = Build(dropD, [Note(1, 0, 0), Note(2, 0, 5), Note(3, 1, 0)]);
        Assert.Equal(40f - 2f + 2f - 0.5f, up.Notes[0].Pitch);    // open drop-D string under a capo at 2
        Assert.Equal(40f - 2f + 5f - 0.5f, up.Notes[1].Pitch);    // fret 5 is above the capo: absolute
        Assert.Equal(45f + 2f - 0.5f, up.Notes[2].Pitch);
        Assert.Equal(0, up.Notes[0].Fret);                       // the fret stays as charted, from the nut
        Assert.Equal(5, up.Notes[1].Fret);
        Assert.All(up.Notes, n => Assert.Equal(2, n.Capo));      // and the capo travels with every note
    }

    [Fact]
    public void ANoteBehindTheCapo_IsLeftOut_NotGuessed()
    {
        var up = Build(new Arrangement { Capo = 3 }, [Note(1, 2, 1), Note(2, 2, 3)]);
        Assert.Single(up.Notes);
        Assert.Equal(50f + 3f, up.Notes[0].Pitch);
    }

    [Fact]
    public void EveryArticulation_MapsToItsFlag()
    {
        var palm = Note(1, 0, 3); palm.Mute = MuteType.Palm;
        var full = Note(2, 0, 3); full.Mute = MuteType.Full;
        var nh = Note(3, 0, 12); nh.Harmonic = HarmonicType.Normal;
        var ph = Note(4, 0, 5); ph.Harmonic = HarmonicType.Pinch;
        var ho = Note(5, 0, 5); ho.Hopo = HopoType.HammerOn;
        var po = Note(6, 0, 5); po.Hopo = HopoType.PullOff;
        var tap = Note(7, 0, 5); tap.Hopo = HopoType.Tap;
        var vib = Note(8, 0, 5); vib.Vibrato = true;
        var trem = Note(9, 0, 5); trem.Tremolo = true;
        var acc = Note(10, 0, 5); acc.Accent = true;
        var uslide = Note(11, 0, 5); uslide.SlideTo = 1; uslide.UnpitchedSlide = true;
        var link = Note(12, 0, 5); link.LinkNext = true;
        var up = Build(new Arrangement(), [palm, full, nh, ph, ho, po, tap, vib, trem, acc, uslide, link]);
        var expected = new[]
        {
            JuceChartNoteFlags.PalmMute, JuceChartNoteFlags.FretHandMute, JuceChartNoteFlags.Harmonic,
            JuceChartNoteFlags.PinchHarmonic, JuceChartNoteFlags.HammerOn, JuceChartNoteFlags.PullOff,
            JuceChartNoteFlags.Tap, JuceChartNoteFlags.Vibrato, JuceChartNoteFlags.Tremolo,
            JuceChartNoteFlags.Accent, JuceChartNoteFlags.UnpitchedSlide, JuceChartNoteFlags.LinkNext,
        };
        for (var i = 0; i < expected.Length; i++)
            Assert.True(up.Notes[i].Flags.HasFlag(expected[i]), $"note {i} lacks {expected[i]} (has {up.Notes[i].Flags})");
        Assert.Equal(1f, up.Notes[10].SlideToFret);
        Assert.True(up.Notes[9].Velocity > up.Notes[0].Velocity, "an accent is not louder");
    }

    [Fact]
    public void BassPickingStyles_MapToSlapPopAndPicked()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, PickedBass = true };
        var slap = Note(1, 0, 3); slap.BassPicking = BassPickingType.Slap;
        var pop = Note(2, 3, 5); pop.BassPicking = BassPickingType.Pop;
        var up = Build(bass, [slap, pop]);
        Assert.True(up.Notes[0].Flags.HasFlag(JuceChartNoteFlags.Slap));
        Assert.True(up.Notes[1].Flags.HasFlag(JuceChartNoteFlags.Pop));
        Assert.All(up.Notes, n => Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.Picked)));
        var fingers = Build(new Arrangement { ArrangementType = ArrangementType.Bass }, [Note(1, 0, 3)]);
        Assert.False(fingers.Notes[0].Flags.HasFlag(JuceChartNoteFlags.Picked));
    }

    [Fact]
    public void AChord_ExpandsToOneNotePerString_SharingAnId_InStartThenStringOrder()
    {
        var chord = new Chord { Position = new FractionalPosition(2), Accent = true };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 });
        var up = Build(new Arrangement(), [Note(1, 0, 0), Note(3, 0, 0)], [chord]);
        Assert.Equal(5, up.Notes.Length);
        var c = up.Notes.Where(n => n.ChordId >= 0).ToArray();
        Assert.Equal(3, c.Length);
        Assert.Single(c.Select(n => n.ChordId).Distinct());
        Assert.Equal([0, 1, 2], c.Select(n => n.StringIndex));
        Assert.All(c, n => Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.Accent)));
        Assert.Equal(JuceChartStrum.Down, c[0].Strum);   // accented: a down-stroke (#52, ChordStrums_FollowTheBeatGrid)
        for (var i = 1; i < up.Notes.Length; i++)
            Assert.True(up.Notes[i - 1].StartSeconds <= up.Notes[i].StartSeconds, "the upload is not sorted by start");
    }

    [Fact]
    public void BendPoints_AreSemitonesInChartTime_AndEachNoteNamesItsOwnRange()
    {
        var later = Note(6, 1, 7, sustainBeats: 2);
        later.BendValues.Add(new BendValue(new FractionalPosition(7), 2.0));
        later.BendValues.Add(new BendValue(new FractionalPosition(6), 0.0));  // out of order on purpose
        var earlier = Note(2, 2, 7, sustainBeats: 1);
        earlier.BendValues.Add(new BendValue(new FractionalPosition(2), 1.0)); // a pre-bend
        var up = Build(new Arrangement(), [later, earlier]);

        Assert.Equal(3, up.Bends.Length);
        var first = up.Notes[0];   // the earlier note sorts first
        Assert.Equal(2, first.StringIndex);
        Assert.Equal(1, first.BendCount);
        Assert.Equal(1.0f, up.Bends[first.BendFirst].Semitones);
        Assert.Equal(first.StartSeconds, up.Bends[first.BendFirst].TimeSeconds, 9);
        var second = up.Notes[1];
        Assert.Equal(2, second.BendCount);
        Assert.Equal(3.0, up.Bends[second.BendFirst].TimeSeconds, 9);       // sorted by time
        Assert.Equal(0f, up.Bends[second.BendFirst].Semitones);
        Assert.Equal(2f, up.Bends[second.BendFirst + 1].Semitones);
        Assert.Equal(1.0, second.SustainSeconds, 9);
    }

    [Fact]
    public void Releases_FollowThePreviewRules_AndAHeldNoteIsMarkedLetRing()
    {
        // Two attacks on one string 1 s apart: the first rings until 8 ms before the
        // second. A note alone at the end rings for at least an eighth note.
        var a = Note(2, 0, 3);
        var b = Note(4, 0, 5);
        var hand = new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4) };
        var up = Build(new Arrangement(), [a, b], hand: [hand]);
        Assert.Equal(2.0 - 0.008, up.Notes[0].ReleaseSeconds, 6);
        Assert.True(up.Notes[0].Flags.HasFlag(JuceChartNoteFlags.LetRing), "a hand-shape hold is not marked let-ring");
        Assert.Equal(2.0 + 0.25, up.Notes[1].ReleaseSeconds, 6);  // an eighth at 120 bpm
        Assert.False(up.Notes[1].Flags.HasFlag(JuceChartNoteFlags.LetRing));

        var accented = Note(10, 0, 3); accented.Accent = true;
        var st = Build(new Arrangement(), [accented]);
        Assert.Equal(5.0 + 0.09, st.Notes[0].ReleaseSeconds, 6);  // staccato
    }

    // ----- hand shapes (Rocksmith semantics, #40) ------------------------------

    private static HandShape Shape(int fromBeat, int toBeat)
        => new() { Position = new FractionalPosition(fromBeat), EndPosition = new FractionalPosition(toBeat) };

    private static Chord StrumChord(int beat, long num, long den, bool palm = false)
    {
        var c = new Chord { Position = new FractionalPosition(beat, new Fraction(num, den)) };
        foreach (var (str, fret) in new[] { (0, 3), (1, 2), (2, 0), (3, 0) })
            c.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret, Mute = palm ? MuteType.Palm : MuteType.None });
        return c;
    }

    private static JuceChartStrum[] StrumsOf(ChartNoteUpload up)
        => [.. up.Notes.Where(n => n.ChordId >= 0).GroupBy(n => n.ChordId).OrderBy(g => g.Key)
            .Select(g => Assert.Single(g.Select(n => n.Strum).Distinct()))];

    /// <summary>
    ///     #52: a chord's strum direction follows the beat grid like a strumming
    ///     hand — eighths go down-up, sixteenths down-up-down-up, triplets
    ///     alternate — a palm-muted chord is a down-stroke wherever it falls, and a
    ///     single note is left to the voice.
    /// </summary>
    [Fact]
    public void ChordStrums_FollowTheBeatGrid()
    {
        var eighths = Build(new Arrangement(), [], [StrumChord(2, 0, 1), StrumChord(2, 1, 2), StrumChord(3, 0, 1), StrumChord(3, 1, 2)]);
        Assert.Equal([JuceChartStrum.Down, JuceChartStrum.Up, JuceChartStrum.Down, JuceChartStrum.Up], StrumsOf(eighths));

        var sixteenths = Build(new Arrangement(), [],
            [StrumChord(4, 0, 1), StrumChord(4, 1, 4), StrumChord(4, 1, 2), StrumChord(4, 3, 4), StrumChord(5, 0, 1)]);
        Assert.Equal([JuceChartStrum.Down, JuceChartStrum.Up, JuceChartStrum.Down, JuceChartStrum.Up, JuceChartStrum.Down],
            StrumsOf(sixteenths));

        var triplets = Build(new Arrangement(), [], [StrumChord(6, 0, 1), StrumChord(6, 1, 3), StrumChord(6, 2, 3), StrumChord(7, 0, 1)]);
        Assert.Equal([JuceChartStrum.Down, JuceChartStrum.Up, JuceChartStrum.Down, JuceChartStrum.Down], StrumsOf(triplets));

        var palmOffBeat = Build(new Arrangement(), [], [StrumChord(8, 0, 1), StrumChord(8, 1, 2, palm: true)]);
        Assert.Equal([JuceChartStrum.Down, JuceChartStrum.Down], StrumsOf(palmOffBeat));

        var single = Build(new Arrangement(), [Note(9, 0, 3)]);
        Assert.Equal(JuceChartStrum.Unknown, Assert.Single(single.Notes).Strum);
    }

    private static Chord ChordAt(int beat, bool accent = false, params (int String, int Fret)[] notes)
    {
        var c = new Chord { Position = new FractionalPosition(beat), Accent = accent };
        foreach (var (str, fret) in notes) c.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
        return c;
    }

    /// <summary>A chord with no authored sustain inside a hand shape rings to the
    /// shape's END — every note of it — and says so. Accented or not.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChordInAHandShape_RingsToTheShapeEnd(bool accent)
    {
        var up = Build(new Arrangement(), [], [ChordAt(2, accent, (0, 3), (1, 2), (2, 0))], [Shape(2, 8)]);
        Assert.Equal(3, up.Notes.Length);
        Assert.All(up.Notes, n =>
        {
            Assert.Equal(4.0, n.ReleaseSeconds, 6);   // beat 8 at 500 ms per beat
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndShape), $"string {n.StringIndex}: {n.Flags}");
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.LetRing));
            Assert.False(n.Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        });
    }

    /// <summary>The next note on ONE string cuts that string only; the rest of the
    /// chord rings on to the shape's end.</summary>
    [Fact]
    public void TheNextNoteOnOneString_CutsOnlyThatString()
    {
        var up = Build(new Arrangement(), [Note(5, 1, 5)], [ChordAt(2, false, (0, 3), (1, 2), (2, 0))], [Shape(2, 8)]);
        var chord = up.Notes.Where(n => n.ChordId >= 0).ToDictionary(n => n.StringIndex);
        Assert.Equal(2.5 - 0.008, chord[1].ReleaseSeconds, 6);
        Assert.True(chord[1].Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        Assert.False(chord[1].Flags.HasFlag(JuceChartNoteFlags.EndShape));
        foreach (var s in new[] { 0, 2 })
        {
            Assert.Equal(4.0, chord[s].ReleaseSeconds, 6);
            Assert.True(chord[s].Flags.HasFlag(JuceChartNoteFlags.EndShape));
        }
    }

    /// <summary>A note whose own sustain outlasts the shape keeps its sustain.</summary>
    [Fact]
    public void ASustainPastTheShapeEnd_KeepsItsSustain()
    {
        var c = ChordAt(2, false, (0, 3), (1, 2));
        c.ChordNotes[0].Sustain = new FractionalPosition(10);   // to beat 12 = 6.0 s
        var up = Build(new Arrangement(), [], [c], [Shape(2, 6)]);
        var byString = up.Notes.ToDictionary(n => n.StringIndex);
        Assert.Equal(6.0, byString[0].ReleaseSeconds, 6);
        Assert.False(byString[0].Flags.HasFlag(JuceChartNoteFlags.EndShape));
        Assert.Equal(3.0, byString[1].ReleaseSeconds, 6);
        Assert.True(byString[1].Flags.HasFlag(JuceChartNoteFlags.EndShape));
    }

    /// <summary>Palm- and fret-hand-muted notes inside a shape keep their short,
    /// damped endings — a muted note never rings on.</summary>
    [Fact]
    public void MutedNotesInAShape_StayShort()
    {
        var c = ChordAt(2, false, (0, 3), (1, 3), (2, 0));
        c.ChordNotes[0].Mute = MuteType.Palm;
        c.ChordNotes[1].Mute = MuteType.Full;
        var up = Build(new Arrangement(), [], [c], [Shape(2, 10)]);
        var byString = up.Notes.ToDictionary(n => n.StringIndex);
        Assert.Equal(1.0 + 0.25, byString[0].ReleaseSeconds, 6);   // an eighth at 120 bpm
        Assert.Equal(1.0 + 0.09, byString[1].ReleaseSeconds, 6);   // a fret-hand mute is a thump
        Assert.Equal(5.0, byString[2].ReleaseSeconds, 6);          // the open string still rings
        foreach (var s in new[] { 0, 1 })
        {
            Assert.False(byString[s].Flags.HasFlag(JuceChartNoteFlags.EndShape));
            Assert.False(byString[s].Flags.HasFlag(JuceChartNoteFlags.LetRing));
        }
    }

    /// <summary>An ARPEGGIO: single notes on different strings, one after another,
    /// inside one hand shape — each rings until the shape ends, and a later note on
    /// a string already sounding damps it.</summary>
    [Fact]
    public void AnArpeggioShape_LetsEveryNoteRingToItsEnd()
    {
        var notes = new List<GuitarNote> { Note(2, 0, 3), Note(3, 1, 2), Note(4, 2, 0), Note(5, 3, 0), Note(6, 0, 5) };
        var up = Build(new Arrangement(), notes, hand: [Shape(2, 8)]);
        var at = up.Notes.ToDictionary(n => (n.StringIndex, n.Fret));
        Assert.Equal(3.0 - 0.008, at[(0, 3)].ReleaseSeconds, 6);   // damped by fret 5 on the same string
        Assert.True(at[(0, 3)].Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        foreach (var key in new[] { (1, 2), (2, 0), (3, 0), (0, 5) })
        {
            Assert.Equal(4.0, at[key].ReleaseSeconds, 6);
            Assert.True(at[key].Flags.HasFlag(JuceChartNoteFlags.EndShape), $"{key}: {at[key].Flags}");
        }
    }

    private static HandShape ShapeTo(int fromBeat, int toBeat, int num, int den)
        => new()
        {
            Position = new FractionalPosition(fromBeat),
            EndPosition = new FractionalPosition(toBeat, new Fraction(num, den)),
        };

    /// <summary>Repeated chords under their own shapes, the first shape ending an
    /// eighth before the next chord (#90): the chord holds up to the next attack and
    /// is damped there — no staccato gap, and never over the next chord.</summary>
    [Fact]
    public void AShapeEndingUnderAQuarterBeforeTheNextChord_HoldsUpToIt()
    {
        var up = Build(new Arrangement(), [],
            [ChordAt(2, false, (1, 10), (2, 7)), ChordAt(6, false, (1, 10), (2, 7))],
            [ShapeTo(2, 5, 1, 2), Shape(6, 9)]);
        var first = up.Notes.Where(n => n.StartSeconds < 2.0).ToList();
        Assert.Equal(2, first.Count);
        Assert.All(first, n =>
        {
            Assert.Equal(3.0 - 0.008, n.ReleaseSeconds, 6);   // the next chord at beat 6, not the shape end at 2.75 s
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndDamped), $"string {n.StringIndex}: {n.Flags}");
            Assert.False(n.Flags.HasFlag(JuceChartNoteFlags.EndShape));
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.LetRing));
        });
    }

    /// <summary>The same with an accented chord, a gap of a quarter or more, or a
    /// next chord ON the shape's end: the chord still ends at the shape's end.</summary>
    [Theory]
    [InlineData(true, 1, 2, 2.75)]    // accented: short, even with an eighth gap
    [InlineData(false, 0, 1, 2.5)]    // shape ends on beat 5, next chord on 6: a quarter gap
    public void AShapeEnd_IsKept_WhenAccentedOrTheGapIsAQuarter(bool accent, int num, int den, double endSeconds)
    {
        var up = Build(new Arrangement(), [],
            [ChordAt(2, accent, (1, 10), (2, 7)), ChordAt(6, false, (1, 10), (2, 7))],
            [ShapeTo(2, 5, num, den), Shape(6, 9)]);
        Assert.All(up.Notes.Where(n => n.StartSeconds < 2.0), n =>
        {
            Assert.Equal(endSeconds, n.ReleaseSeconds, 6);
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndShape), $"string {n.StringIndex}: {n.Flags}");
            Assert.False(n.Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        });
    }

    /// <summary>Shapes that touch (the next chord, on other strings, ON the shape's
    /// end) already join; the end stays the shape's.</summary>
    [Fact]
    public void TouchingShapes_KeepTheShapeEnd()
    {
        var up = Build(new Arrangement(), [],
            [ChordAt(2, false, (1, 10), (2, 7)), ChordAt(6, false, (3, 9), (4, 9))],
            [Shape(2, 6), Shape(6, 9)]);
        Assert.All(up.Notes.Where(n => n.StartSeconds < 2.0), n =>
        {
            Assert.Equal(3.0, n.ReleaseSeconds, 6);
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndShape), $"string {n.StringIndex}: {n.Flags}");
        });
    }

    /// <summary>Outside any shape the end stays sustain-based, and a note ON a
    /// shape's end belongs to no shape (half-open).</summary>
    [Fact]
    public void OutsideAShape_TheEndIsSustainBased()
    {
        var up = Build(new Arrangement(), [Note(8, 2, 2, sustainBeats: 1), Note(12, 3, 2)], hand: [Shape(2, 8)]);
        Assert.Equal(4.5, up.Notes[0].ReleaseSeconds, 6);           // its own 1-beat sustain
        Assert.Equal(6.25, up.Notes[1].ReleaseSeconds, 6);          // an eighth
        Assert.All(up.Notes, n => Assert.Equal(JuceChartNoteFlags.None,
            n.Flags & (JuceChartNoteFlags.EndShape | JuceChartNoteFlags.EndDamped | JuceChartNoteFlags.LetRing)));
    }

    // ----- outside a hand shape: never over the next note (owner rules, #40) ----

    private static GuitarNote At(int beat, int num, int den, int stringIndex, int fret)
        => new()
        {
            Position = new FractionalPosition(beat, new Fraction(num, den)),
            StringIndex = stringIndex,
            Fret = fret,
        };

    /// <summary>No two notes of the upload sound at once (a note's release is at or
    /// before the next attack).</summary>
    private static void AssertNoOverlap(ChartNoteUpload up)
    {
        for (var i = 0; i + 1 < up.Notes.Length; i++)
        {
            Assert.True(up.Notes[i].ReleaseSeconds <= up.Notes[i + 1].StartSeconds + 1e-9,
                $"note {i} (string {up.Notes[i].StringIndex}) is still sounding at {up.Notes[i + 1].StartSeconds} s "
                + $"when note {i + 1} starts (released at {up.Notes[i].ReleaseSeconds} s)");
        }
    }

    /// <summary>A zero-sustain run with gaps under a quarter note is LEGATO — each
    /// note holds right up to the next attack, on the same string or another, and is
    /// damped there: 6-5-6-5 on one string, then across two strings.</summary>
    [Fact]
    public void AZeroSustainRun_UnderAQuarter_RingsUpToEachNextAttack()
    {
        foreach (var (strA, strB) in new[] { (0, 0), (2, 3) })
        {
            var up = Build(new Arrangement(),
                [At(2, 0, 4, strA, 6), At(2, 3, 8, strB, 5), At(3, 0, 4, strA, 6), At(3, 3, 8, strB, 5)]);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(up.Notes[i + 1].StartSeconds - 0.008, up.Notes[i].ReleaseSeconds, 6);
                Assert.True(up.Notes[i].Flags.HasFlag(JuceChartNoteFlags.EndDamped));
                Assert.False(up.Notes[i].Flags.HasFlag(JuceChartNoteFlags.LetRing));
            }
            AssertNoOverlap(up);
        }
    }

    /// <summary>A gap of a quarter note or more ends the note at its own end with a
    /// natural release — not held, not damped.</summary>
    [Fact]
    public void AGapOfAQuarterOrMore_EndsWithANaturalRelease()
    {
        var up = Build(new Arrangement(), [Note(2, 2, 7), Note(4, 3, 5)]);   // 1 s apart, 500 ms quarter
        Assert.Equal(1.25, up.Notes[0].ReleaseSeconds, 6);
        Assert.Equal(JuceChartNoteFlags.None,
            up.Notes[0].Flags & (JuceChartNoteFlags.EndDamped | JuceChartNoteFlags.LetRing | JuceChartNoteFlags.EndShape));
    }

    // ----- hammer-ons, pull-offs, taps and links continue the string (#100) ----

    private static GuitarNote Legato(GuitarNote n, HopoType hopo)
    {
        n.Hopo = hopo;
        return n;
    }

    /// <summary>A note followed on its own string by a hammer-on, pull-off or tap
    /// rings INTO it (30 ms past its start, not damped), so the voice continues the
    /// string instead of picking it again.</summary>
    [Theory]
    [InlineData(HopoType.HammerOn)]
    [InlineData(HopoType.PullOff)]
    [InlineData(HopoType.Tap)]
    public void ANoteBeforeAHammerOnPullOffOrTap_RingsIntoIt(HopoType hopo)
    {
        var up = Build(new Arrangement(), [Note(2, 1, 5), Legato(At(2, 1, 2, 1, 7), hopo)]);
        var first = up.Notes[0];
        Assert.Equal(1.25 + 0.030, first.ReleaseSeconds, 6);   // the legato note starts at 1.25 s
        Assert.False(first.Flags.HasFlag(JuceChartNoteFlags.EndDamped), $"{first.Flags}");
    }

    /// <summary>A note that LINKS into the next one rings into it the same way.</summary>
    [Fact]
    public void ANoteLinkedIntoTheNext_RingsIntoIt()
    {
        var linked = Note(2, 1, 5);
        linked.LinkNext = true;
        var up = Build(new Arrangement(), [linked, At(2, 1, 2, 1, 7)]);
        Assert.Equal(1.25 + 0.030, up.Notes[0].ReleaseSeconds, 6);
        Assert.False(up.Notes[0].Flags.HasFlag(JuceChartNoteFlags.EndDamped));
    }

    /// <summary>The rule is the fretting hand's: a PICKED next note still damps the
    /// string just before it, and so does a note on another string that comes first.</summary>
    [Fact]
    public void APickedNextNote_OrAnotherStringFirst_StillDamps()
    {
        var picked = Build(new Arrangement(), [Note(2, 1, 5), At(2, 1, 2, 1, 7)]);
        Assert.Equal(1.25 - 0.008, picked.Notes[0].ReleaseSeconds, 6);
        Assert.True(picked.Notes[0].Flags.HasFlag(JuceChartNoteFlags.EndDamped));

        var between = Build(new Arrangement(), [Note(2, 1, 5), At(2, 1, 4, 2, 7), Legato(At(2, 1, 2, 1, 7), HopoType.HammerOn)]);
        Assert.Equal(1.125 - 0.008, between.Notes[0].ReleaseSeconds, 6);   // the string-2 note at 1.125 s cuts it
        Assert.True(between.Notes[0].Flags.HasFlag(JuceChartNoteFlags.EndDamped));
    }

    /// <summary>Inside a hand shape a hammer-on on the same string no longer damps
    /// the note before it, however far apart they are.</summary>
    [Fact]
    public void InAHandShape_AHammerOnContinuesTheString()
    {
        var up = Build(new Arrangement(), [Note(2, 1, 5), Legato(Note(4, 1, 7), HopoType.HammerOn)], hand: [Shape(2, 8)]);
        var first = up.Notes[0];
        Assert.Equal(2.0 + 0.030, first.ReleaseSeconds, 6);
        Assert.False(first.Flags.HasFlag(JuceChartNoteFlags.EndDamped));
        Assert.True(first.Flags.HasFlag(JuceChartNoteFlags.LetRing));
    }

    /// <summary>Accented and muted notes stay short even when the next note is near.</summary>
    [Fact]
    public void AccentedAndMutedNotes_StayShort()
    {
        var accented = Note(2, 2, 7); accented.Accent = true;
        var palm = Note(4, 2, 7); palm.Mute = MuteType.Palm;
        var full = Note(6, 2, 7); full.Mute = MuteType.Full;
        var up = Build(new Arrangement(),
            [accented, At(2, 3, 4, 3, 5), palm, At(4, 3, 4, 3, 5), full, At(6, 3, 4, 3, 5)]);
        var mine = up.Notes.Where(n => n.StringIndex == 2).ToArray();
        Assert.Equal(1.0 + 0.09, mine[0].ReleaseSeconds, 6);   // staccato
        Assert.Equal(2.0 + 0.25, mine[1].ReleaseSeconds, 6);   // palm: an eighth
        Assert.Equal(3.0 + 0.09, mine[2].ReleaseSeconds, 6);   // fret-hand mute: a thump
        Assert.All(mine, n => Assert.False(n.Flags.HasFlag(JuceChartNoteFlags.LetRing)));
    }

    /// <summary>A SWEEP: six single notes up the strings, no hand shape — no two
    /// sound at once. The same run inside an arpeggio hand shape rings together to
    /// the shape's end.</summary>
    [Fact]
    public void ASweep_IsDampedNoteByNote_UnlessItIsInAnArpeggioShape()
    {
        var sweep = Enumerable.Range(0, 6).Select(i => At(2, i, 8, i, 5 + (i % 3))).ToList();   // 62.5 ms apart
        var dry = Build(new Arrangement(), sweep);
        Assert.Equal(6, dry.Notes.Length);
        AssertNoOverlap(dry);
        Assert.All(dry.Notes.Take(5), n => Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndDamped)));

        var shaped = Build(new Arrangement(), sweep, hand: [Shape(2, 6)]);
        Assert.All(shaped.Notes, n =>
        {
            Assert.Equal(3.0, n.ReleaseSeconds, 6);
            Assert.True(n.Flags.HasFlag(JuceChartNoteFlags.EndShape));
        });
    }

    /// <summary>The quarter note is the one AT the note, from the beat map: the same
    /// 700 ms gap is legato at 60 bpm and a natural release at 120 bpm.</summary>
    [Fact]
    public void TheQuarterNote_FollowsATempoChange()
    {
        // Beats 0-9 every 500 ms (120 bpm), then every 1000 ms (60 bpm) from 4.5 s.
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 30; i++)
            beats.Add(new BeatMarker { Time = i <= 9 ? i * 500.0 : 4500.0 + (i - 9) * 1000.0, Bpm = i < 9 ? 120 : 60 });
        var fast = new List<GuitarNote> { At(2, 0, 1, 2, 3), At(3, 2, 5, 3, 3) };     // 1.0 s, 1.7 s
        var slow = new List<GuitarNote> { At(12, 0, 1, 2, 3), At(12, 7, 10, 3, 3) };  // 7.5 s, 8.2 s
        var fastUp = ChartNoteEventBuilder.Build(new Arrangement(), fast, [], beats, null, 120);
        var slowUp = ChartNoteEventBuilder.Build(new Arrangement(), slow, [], beats, null, 120);
        Assert.Equal(1.7, fastUp.Notes[1].StartSeconds, 6);
        Assert.Equal(8.2, slowUp.Notes[1].StartSeconds, 6);
        Assert.Equal(1.25, fastUp.Notes[0].ReleaseSeconds, 6);           // 700 ms >= a 500 ms quarter
        Assert.Equal(8.2 - 0.008, slowUp.Notes[0].ReleaseSeconds, 6);    // 700 ms < a 1000 ms quarter
        Assert.Equal(500.0, ChartNoteEventBuilder.QuarterNoteMsAt(1000, beats, 1), 6);
        Assert.Equal(1000.0, ChartNoteEventBuilder.QuarterNoteMsAt(7500, beats, 1), 6);
    }

    [Fact]
    public void TheFingerprint_MovesOnEveryEditTheUploadDependsOn_AndOnlyThen()
    {
        var arr = new Arrangement();
        var notes = new List<GuitarNote> { Note(2, 0, 3), Note(4, 1, 5) };
        var beats = Beats();
        long Fp() => ChartNoteEventBuilder.Fingerprint(arr, notes, [], beats, null, 120);

        var baseline = Fp();
        Assert.Equal(baseline, Fp());
        Assert.Equal(baseline, ChartNoteEventBuilder.Build(arr, notes, [], beats, null, 120).Fingerprint);

        var edits = new (string What, Action Do, Action Undo)[]
        {
            ("fret", () => notes[0].Fret = 4, () => notes[0].Fret = 3),
            ("string", () => notes[0].StringIndex = 1, () => notes[0].StringIndex = 0),
            ("position", () => notes[1].Position = new FractionalPosition(5), () => notes[1].Position = new FractionalPosition(4)),
            ("sustain", () => notes[1].Sustain = new FractionalPosition(1), () => notes[1].Sustain = FractionalPosition.Zero),
            ("mute", () => notes[0].Mute = MuteType.Palm, () => notes[0].Mute = MuteType.None),
            ("bend", () => notes[0].BendValues.Add(new BendValue(new FractionalPosition(2), 1)), () => notes[0].BendValues.Clear()),
            ("slide", () => notes[0].SlideTo = 7, () => notes[0].SlideTo = null),
            ("capo", () => arr.Capo = 2, () => arr.Capo = 0),
            ("tuning", () => arr.TuningPitches.Add(new TuningOffset(-2)), () => arr.TuningPitches.Clear()),
            ("arrangement type", () => arr.ArrangementType = ArrangementType.Bass, () => arr.ArrangementType = ArrangementType.Lead),
            ("a beat in the middle", () => beats[10].Time += 20, () => beats[10].Time -= 20),
            ("a note added", () => notes.Add(Note(8, 2, 2)), () => notes.RemoveAt(notes.Count - 1)),
        };
        foreach (var (what, @do, undo) in edits)
        {
            @do();
            Assert.NotEqual(baseline, Fp());
            undo();
            Assert.True(baseline == Fp(), $"undoing the {what} edit did not restore the fingerprint");
        }
    }

    [Fact]
    public void NoBeats_IsAnEmptyUpload()
    {
        var up = ChartNoteEventBuilder.Build(new Arrangement(), [Note(1, 0, 0)], [], [], null, 120);
        Assert.Empty(up.Notes);
        Assert.Empty(up.Bends);
    }
}
