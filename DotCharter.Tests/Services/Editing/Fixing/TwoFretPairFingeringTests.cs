using System.Collections.ObjectModel;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing.Fixing;

/// <summary>
///     #498 / #511: one command switches every two-fret octave, another every
///     two-string power chord, between <c>1·4</c> and <c>1·3</c>, because the
///     owner's verified charts pick one per SONG and the fret shape cannot. These
///     pin which shapes count (by sounding interval, so tuning and capo are
///     honoured), that a deliberate other fingering is left alone, the toggle
///     direction, and each command's one undo node.
/// </summary>
public sealed class TwoFretPairFingeringTests
{
    private static Arrangement Guitar(params int[] offsets)
    {
        var arrangement = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        if (offsets.Length > 0)
            arrangement.TuningPitches = new ObservableCollection<TuningOffset>(offsets.Select(o => new TuningOffset(o)));
        return arrangement;
    }

    private static ChordTemplate Shape(int id, (int String, int Fret)[] frets, params (int String, int Finger)[] fingers)
    {
        var template = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        foreach (var (s, f) in fingers) template.Fingers[s] = f;
        return template;
    }

    [Theory]
    [InlineData(1, 5, 3, 7, true)] // A root -> G string
    [InlineData(0, 3, 2, 5, true)] // low E root -> D string
    [InlineData(2, 7, 4, 10, false)] // D root -> B: three frets apart, always 1-4, never touched
    [InlineData(3, 5, 5, 8, false)] // G root -> high e: three frets apart
    [InlineData(1, 5, 2, 7, false)] // adjacent strings: not an octave shape
    [InlineData(1, 5, 3, 6, false)] // one string skipped but not an octave
    [InlineData(1, 0, 3, 2, false)] // an open root is not a fretted octave grip
    public void RecognisesTheTwoFretOctaveInStandardTuning(int lowString, int lowFret, int highString, int highFret, bool expected)
    {
        var template = Shape(0, [(lowString, lowFret), (highString, highFret)]);
        Assert.Equal(expected, TwoFretPairFingering.IsTwoFretOctave(Guitar(), template, out _, out _));
    }

    [Fact]
    public void AThirdNoteMakesItAChordNotAnOctave()
    {
        var template = Shape(0, [(1, 5), (2, 7), (3, 7)]);
        Assert.False(TwoFretPairFingering.IsTwoFretOctave(Guitar(), template, out _, out _));
    }

    [Fact]
    public void TheIntervalIsReadThroughTheTuning()
    {
        // Drop D: the low string is a tone lower, so low-E fret 3 + D fret 5 is
        // F + G — two frets apart on skipped strings, but no octave.
        var dropD = Guitar(-2, 0, 0, 0, 0, 0);
        Assert.False(TwoFretPairFingering.IsTwoFretOctave(dropD, Shape(0, [(0, 3), (2, 5)]), out _, out _));
        // The A+G pair is unaffected by the drop.
        Assert.True(TwoFretPairFingering.IsTwoFretOctave(dropD, Shape(1, [(1, 5), (3, 7)]), out _, out _));
    }

    [Fact]
    public void ACapoDoesNotHideAnOctave()
    {
        var capo = Guitar();
        capo.Capo = 2;
        Assert.True(TwoFretPairFingering.IsTwoFretOctave(capo, Shape(0, [(1, 5), (3, 7)]), out _, out _));
    }

    [Fact]
    public void ADeliberateOtherFingeringIsNotATarget()
    {
        var arrangement = Guitar();
        var pinky = Shape(0, [(1, 5), (3, 7)], (1, 1), (3, 4));
        var unfingered = Shape(1, [(0, 3), (2, 5)]);
        var middlePinky = Shape(2, [(1, 7), (3, 9)], (1, 2), (3, 4));
        arrangement.ChordTemplates.Add(pinky);
        arrangement.ChordTemplates.Add(unfingered);
        arrangement.ChordTemplates.Add(middlePinky);

        var targets = TwoFretPairFingering.Targets(arrangement, TwoFretPair.TwoFretOctave);

        Assert.Equal([pinky, unfingered], targets);
    }

    [Fact]
    public void AMixedArrangementBecomesConsistentOnTheFirstPressThenTogglesBack()
    {
        var arrangement = Guitar();
        var ring = Shape(0, [(1, 5), (3, 7)], (1, 1), (3, 3));
        var pinky = Shape(1, [(1, 7), (3, 9)], (1, 1), (3, 4));
        arrangement.ChordTemplates.Add(ring);
        arrangement.ChordTemplates.Add(pinky);
        Assert.Equal(PairFingering.IndexRing, TwoFretPairFingering.Next(arrangement, TwoFretPair.TwoFretOctave, [ring, pinky]));

        pinky.Fingers[3] = 3;
        Assert.Equal(PairFingering.IndexPinky, TwoFretPairFingering.Next(arrangement, TwoFretPair.TwoFretOctave, [ring, pinky]));
    }

    [Fact]
    public void TheCommandFlipsEveryTwoFretOctaveInOneUndoNodeAndLeavesTheRestAlone()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;
        var octaveAG = Shape(10, [(1, 5), (3, 7)], (1, 1), (3, 4));
        var octaveED = Shape(11, [(0, 3), (2, 5)], (0, 1), (2, 4));
        var octaveDB = Shape(12, [(2, 7), (4, 10)], (2, 1), (4, 4));
        var powerChord = Shape(13, [(0, 5), (1, 7)], (0, 1), (1, 3));
        foreach (var t in new[] { octaveAG, octaveED, octaveDB, powerChord }) arrangement.ChordTemplates.Add(t);
        for (var i = 0; i < 4; i++)
        {
            var template = arrangement.ChordTemplates[^(4 - i)];
            var chord = new Chord { Position = new FractionalPosition(8 + i), TemplateId = template.Id };
            foreach (var (s, f) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        level.Chords.Add(new Chord { Position = new FractionalPosition(12), TemplateId = octaveAG.Id });

        h.Maintenance.ToggleTwoFretOctaveFingering();

        Assert.Equal(3, octaveAG.Fingers[3]);
        Assert.Equal(3, octaveED.Fingers[2]);
        Assert.Equal(1, octaveAG.Fingers[1]);
        Assert.Equal(4, octaveDB.Fingers[4]); // three-fret octave: never touched
        Assert.Equal(3, powerChord.Fingers[1]); // not an octave
        Assert.Equal("3 octave chords are now fingered 1·3.", h.State.MainDisplayMessage);

        // One gesture, one undo node: a single Ctrl+Z restores both templates.
        h.History.Undo();
        Assert.Equal(4, octaveAG.Fingers[3]);
        Assert.Equal(4, octaveED.Fingers[2]);
        Assert.False(h.History.CanUndo);

        // And the second press of a consistent 1-3 arrangement goes back to 1-4.
        h.Maintenance.ToggleTwoFretOctaveFingering();
        h.Maintenance.ToggleTwoFretOctaveFingering();
        Assert.Equal(4, octaveAG.Fingers[3]);
        Assert.Equal(4, octaveED.Fingers[2]);
    }

    [Fact]
    public void WithNoOctaveTheCommandSaysSoAndRecordsNothing()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveArrangement!.ChordTemplates.Add(Shape(10, [(0, 5), (1, 7)], (0, 1), (1, 3)));

        h.Maintenance.ToggleTwoFretOctaveFingering();

        Assert.Equal("No two-fret octave shape in this arrangement.", h.State.MainDisplayMessage);
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData(0, 5, 1, 7, true)] // low E root, fifth on A
    [InlineData(1, 3, 2, 5, true)] // A root, fifth on D
    [InlineData(2, 7, 3, 9, true)] // D root, fifth on G
    [InlineData(4, 5, 5, 7, true)] // B root, fifth on high e
    [InlineData(3, 5, 4, 8, false)] // G root: its fifth on B is three frets up, always 1·4
    [InlineData(3, 5, 4, 7, false)] // two frets up across G-B is a tritone
    [InlineData(0, 5, 1, 5, false)] // same fret: a fourth
    [InlineData(0, 0, 1, 2, false)] // an open root
    public void RecognisesTheTwoStringPowerChordInStandardTuning(int lowString, int lowFret, int highString, int highFret, bool expected)
    {
        var template = Shape(0, [(lowString, lowFret), (highString, highFret)]);
        Assert.Equal(expected, TwoFretPairFingering.Classify(Guitar(), template, out _, out _) == TwoFretPair.PowerChord);
    }

    [Fact]
    public void RootFifthOctaveIsNotATwoStringPowerChord() =>
        // 1·3·4 in all 2,940 verified chords: there is no per-song choice to make.
        Assert.Null(TwoFretPairFingering.Classify(Guitar(), Shape(0, [(0, 5), (1, 7), (2, 7)]), out _, out _));

    [Fact]
    public void APowerChordPressGoesToTheFingeringTheSuggesterDoesNotGive()
    {
        var arrangement = Guitar();
        var ring = Shape(0, [(0, 5), (1, 7)], (0, 1), (1, 3));
        var pinky = Shape(1, [(1, 3), (2, 5)], (1, 1), (2, 4));
        arrangement.ChordTemplates.Add(ring);
        arrangement.ChordTemplates.Add(pinky);
        Assert.Equal(PairFingering.IndexPinky, TwoFretPairFingering.Next(arrangement, TwoFretPair.PowerChord, [ring, pinky]));

        ring.Fingers[1] = 4;
        Assert.Equal(PairFingering.IndexRing, TwoFretPairFingering.Next(arrangement, TwoFretPair.PowerChord, [ring, pinky]));
    }

    [Fact]
    public void ThePowerChordCommandFlipsEveryPowerChordBothWaysInOneUndoNodeAndLeavesTheRestAlone()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;
        var lowE = Shape(10, [(0, 5), (1, 7)], (0, 1), (1, 3));
        var onA = Shape(11, [(1, 3), (2, 5)]); // unfingered: a target
        var middlePinky = Shape(12, [(2, 7), (3, 9)], (2, 2), (3, 4)); // deliberate
        var thumb = Shape(13, [(0, 3), (1, 5)], (0, 0), (1, 3)); // deliberate
        var gb = Shape(14, [(3, 5), (4, 8)], (3, 1), (4, 4)); // three frets: not a target
        var full = Shape(15, [(0, 5), (1, 7), (2, 7)], (0, 1), (1, 3), (2, 4)); // root-fifth-octave
        var octave = Shape(16, [(0, 3), (2, 5)], (0, 1), (2, 4));
        ChordTemplate[] all = [lowE, onA, middlePinky, thumb, gb, full, octave];
        for (var i = 0; i < all.Length; i++)
        {
            arrangement.ChordTemplates.Add(all[i]);
            var chord = new Chord { Position = new FractionalPosition(8 + i), TemplateId = all[i].Id };
            foreach (var (s, f) in all[i].Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            level.Chords.Add(chord);
        }

        level.Chords.Add(new Chord { Position = new FractionalPosition(20), TemplateId = lowE.Id });
        var untouched = new[] { middlePinky, thumb, gb, full, octave }
            .Select(t => new Dictionary<int, int>(t.Fingers)).ToList();

        h.Maintenance.TogglePowerChordFingering();

        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 4 }, lowE.Fingers);
        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 4 }, onA.Fingers);
        Assert.Equal(untouched, new[] { middlePinky, thumb, gb, full, octave }.Select(t => t.Fingers));
        Assert.Equal("3 power chords are now fingered 1·4.", h.State.MainDisplayMessage);

        // One gesture, one undo node: a single Ctrl+Z restores both templates.
        h.History.Undo();
        Assert.Equal(3, lowE.Fingers[1]);
        Assert.Empty(onA.Fingers);
        Assert.False(h.History.CanUndo);

        // And the second press of a consistent 1·4 arrangement goes back to 1·3.
        h.Maintenance.TogglePowerChordFingering();
        h.Maintenance.TogglePowerChordFingering();
        Assert.Equal(3, lowE.Fingers[1]);
        Assert.Equal(3, onA.Fingers[2]);
        Assert.Equal(untouched, new[] { middlePinky, thumb, gb, full, octave }.Select(t => t.Fingers));
    }

    [Fact]
    public void WithNoPowerChordTheCommandSaysSoAndRecordsNothing()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveArrangement!.ChordTemplates.Add(Shape(10, [(0, 3), (2, 5)], (0, 1), (2, 4)));

        h.Maintenance.TogglePowerChordFingering();

        Assert.Equal("No two-string power chord in this arrangement.", h.State.MainDisplayMessage);
        Assert.False(h.History.CanUndo);
    }
}
