using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     What <see cref="LiveExpectedTimeline" /> asks a note's pitch contour to show (#645): the
///     bend's step and when the chart reaches it, the slide's signed interval, the vibrato, and
///     a window that ends at the next sound. Chords and harmonics ask for nothing — the contour
///     follows one string's pitch.
/// </summary>
public sealed class TechniqueContourExpectationTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ExpectedSound> Expected(params object[] sounds)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        foreach (var s in sounds)
        {
            if (s is GuitarNote n) arr.Levels[0].Notes.Add(n);
            else arr.Levels[0].Chords.Add((Chord)s);
        }

        var list = new List<ExpectedSound>();
        new LiveExpectedTimeline().Build(list, arr, arr.Levels[0], Beats(), 0);
        return list;
    }

    private static FractionalPosition At(int beat, long num = 0, long den = 1) => new(beat, new Fraction(num, den));

    [Fact]
    public void A_bend_carries_its_largest_step_and_when_the_chart_first_reaches_it()
    {
        var note = new GuitarNote
        {
            Position = At(2), Fret = 7, StringIndex = 3, Sustain = new FractionalPosition(2),
            BendValues =
            {
                new BendValue(At(2, 1, 4), 1),     // half step at +125 ms
                new BendValue(At(2, 1, 2), 2),     // full step at +250 ms
                new BendValue(At(3), 2),           // held
                new BendValue(At(3, 1, 2), 0)      // released
            }
        };

        var c = Assert.Single(Expected(note)).TechniqueContour;

        Assert.Equal(ContourTechniques.Bend, c.Techniques);
        Assert.Equal(2, c.BendSemis);
        Assert.Equal(250, c.BendPeakAfterMs, 3);
        Assert.Equal(1000, c.WindowAfterMs, 3);
    }

    [Fact]
    public void A_slide_carries_its_signed_interval_and_a_vibrato_its_flag()
    {
        var down = new GuitarNote { Position = At(2), Fret = 9, StringIndex = 2, SlideTo = 5, Sustain = new FractionalPosition(1) };
        var vib = new GuitarNote { Position = At(6), Fret = 9, StringIndex = 2, Vibrato = true, Sustain = new FractionalPosition(2) };

        var sounds = Expected(down, vib);

        Assert.Equal(new TechniqueContourExpectation(ContourTechniques.Slide, 0, 0, -4, 500), sounds[0].TechniqueContour);
        Assert.Equal(ContourTechniques.Vibrato, sounds[1].TechniqueContour.Techniques);
        Assert.True(sounds[1].Vibrato);
        Assert.Equal(1000, sounds[1].TechniqueContour.WindowAfterMs, 3);
    }

    [Fact]
    public void The_window_stops_at_the_next_sound()
    {
        var vib = new GuitarNote { Position = At(2), Fret = 9, StringIndex = 2, Vibrato = true, Sustain = new FractionalPosition(4) };
        var next = new GuitarNote { Position = At(3), Fret = 7, StringIndex = 2 };

        Assert.Equal(500, Expected(vib, next)[0].TechniqueContour.WindowAfterMs, 3);
    }

    [Fact]
    public void A_bend_with_no_sustain_is_followed_long_enough_to_arrive()
    {
        var note = new GuitarNote { Position = At(2), Fret = 7, BendValues = { new BendValue(At(2, 1, 2), 1) } };

        Assert.Equal(250 + TechniqueContourJudge.BendLateSlackMs,
            Assert.Single(Expected(note)).TechniqueContour.WindowAfterMs, 3);
    }

    [Fact]
    public void Plain_notes_harmonics_unpitched_slides_and_chords_ask_for_nothing()
    {
        var plain = new GuitarNote { Position = At(2), Fret = 7 };
        var harmonic = new GuitarNote
        {
            Position = At(4), Fret = 12, Harmonic = HarmonicType.Normal, Vibrato = true, Sustain = new FractionalPosition(2)
        };
        var unpitched = new GuitarNote { Position = At(8), Fret = 7, SlideTo = 12, UnpitchedSlide = true, Sustain = new FractionalPosition(1) };
        var chord = new Chord
        {
            Position = At(10),
            ChordNotes =
            {
                new ChordNote { StringIndex = 1, Fret = 5, Vibrato = true, Sustain = new FractionalPosition(2) },
                new ChordNote { StringIndex = 2, Fret = 7, BendValues = { new BendValue(At(10, 1, 2), 2) } }
            }
        };

        var sounds = Expected(plain, harmonic, unpitched, chord);

        Assert.All(sounds, s => Assert.True(s.TechniqueContour.IsEmpty, s.Label));
        // The chord still carries its vibrato for the report card.
        Assert.True(sounds[3].Vibrato);
    }
}
