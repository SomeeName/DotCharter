using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     <see cref="LivePitchAcceptance.Accepts" /> on its own: which played pitches count as
///     the charted one. Every judging path (the classic candidate match, the chord coverage
///     count, the CQT path's attack onsets) asks this one predicate, so each physics-shaped
///     tolerance is pinned here at its edge — one semitone inside it, one outside.
/// </summary>
public sealed class LivePitchAcceptanceTests
{
    private const int A2 = 45;

    private static bool Accepts(ExpectedSound sound, int played, bool octaveTolerant = false) =>
        LivePitchAcceptance.Accepts(sound, A2, played, octaveTolerant);

    [Fact]
    public void A_plain_note_accepts_its_own_pitch_only()
    {
        var plain = new ExpectedSound { MidiNotes = [A2] };

        Assert.True(Accepts(plain, A2));
        Assert.False(Accepts(plain, A2 + 1));
        Assert.False(Accepts(plain, A2 - 1));
    }

    [Fact]
    public void Octave_tolerance_accepts_one_octave_either_way_and_no_further()
    {
        var plain = new ExpectedSound { MidiNotes = [A2] };

        Assert.True(Accepts(plain, A2 + 12, octaveTolerant: true));
        Assert.True(Accepts(plain, A2 - 12, octaveTolerant: true));
        Assert.False(Accepts(plain, A2 + 24, octaveTolerant: true));
        Assert.False(Accepts(plain, A2 + 13, octaveTolerant: true));
        // Off (Strict voicing, string-aware single notes), the octave is a wrong note.
        Assert.False(Accepts(plain, A2 + 12));
        Assert.False(Accepts(plain, A2 - 12));
    }

    [Theory]
    [InlineData(A2 + 5)] // slide up five frets
    [InlineData(A2 - 3)] // slide down three
    public void A_single_note_slide_accepts_every_pitch_it_traverses(int slideTo)
    {
        var slide = new ExpectedSound { MidiNotes = [A2], SlideToMidi = slideTo };
        var lo = Math.Min(A2, slideTo);
        var hi = Math.Max(A2, slideTo);

        for (var p = lo; p <= hi; p++) Assert.True(Accepts(slide, p), $"{p} lies on the slide");
        Assert.False(Accepts(slide, lo - 1));
        Assert.False(Accepts(slide, hi + 1));
    }

    [Fact]
    public void A_chord_slide_widens_by_its_signed_shape_displacement()
    {
        var up = new ExpectedSound { MidiNotes = [A2], SlideSemis = 3 };
        var down = new ExpectedSound { MidiNotes = [A2], SlideSemis = -2 };

        Assert.True(Accepts(up, A2 + 3));
        Assert.False(Accepts(up, A2 + 4));
        Assert.False(Accepts(up, A2 - 1));
        Assert.True(Accepts(down, A2 - 2));
        Assert.False(Accepts(down, A2 - 3));
        Assert.False(Accepts(down, A2 + 1));
    }

    [Fact]
    public void A_bend_accepts_up_to_its_ceiling_and_never_below_the_fretted_pitch()
    {
        var bend = new ExpectedSound { MidiNotes = [A2], MaxBendSemis = 2 };

        Assert.True(Accepts(bend, A2));
        Assert.True(Accepts(bend, A2 + 1));
        Assert.True(Accepts(bend, A2 + 2));
        Assert.False(Accepts(bend, A2 + 3));
        Assert.False(Accepts(bend, A2 - 1));
    }

    [Fact]
    public void The_bend_ceiling_is_the_highest_step_rounded_up()
    {
        Assert.Equal(0, LivePitchAcceptance.MaxBendSemis([]));
        Assert.Equal(2, LivePitchAcceptance.MaxBendSemis(
            [new BendValue(new FractionalPosition(0), 0.5), new BendValue(new FractionalPosition(1), 1.5)]));
        Assert.Equal(1, LivePitchAcceptance.MaxBendSemis([new BendValue(new FractionalPosition(0), 1.0)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_harmonic_accepts_exactly_its_overtones(bool pinch)
    {
        var harmonic = new ExpectedSound { MidiNotes = [A2], Harmonic = !pinch, PinchHarmonic = pinch };

        foreach (var semis in new[] { 12, 19, 24, 28 })
            Assert.True(Accepts(harmonic, A2 + semis), $"+{semis} is an overtone");
        Assert.False(Accepts(harmonic, A2 + 17));
        Assert.False(Accepts(harmonic, A2 + 31));
    }

    [Fact]
    public void A_fretted_note_does_not_accept_overtones()
    {
        var plain = new ExpectedSound { MidiNotes = [A2] };

        Assert.False(Accepts(plain, A2 + 19));
        Assert.False(Accepts(plain, A2 + 24, octaveTolerant: true));
    }

    [Fact]
    public void Pitchless_sounds_are_routed_to_timbre_evidence()
    {
        Assert.True(LivePitchAcceptance.NeedsTimbreEvidence(new ExpectedSound { MidiNotes = [A2], FullMute = true }));
        Assert.True(LivePitchAcceptance.NeedsTimbreEvidence(new ExpectedSound { MidiNotes = [] }));
        Assert.True(LivePitchAcceptance.NeedsTimbreEvidence(new ExpectedSound { MidiNotes = [A2], UnpitchedSlide = true }));
        Assert.False(LivePitchAcceptance.NeedsTimbreEvidence(new ExpectedSound { MidiNotes = [A2], SlapOrPop = true }));

        Assert.True(LivePitchAcceptance.PitchIsUnreliable(new ExpectedSound { MidiNotes = [A2], SlapOrPop = true }));
        Assert.False(LivePitchAcceptance.PitchIsUnreliable(new ExpectedSound { MidiNotes = [A2], PalmMute = true }));
    }

    /// <summary>The predicate exists because the <c>HashSet</c> it replaced allocated per
    /// candidate sound of every detected attack. It runs on the detection worker.</summary>
    [Fact]
    public void Accepts_is_allocation_free()
    {
        ExpectedSound[] sounds =
        [
            new() { MidiNotes = [A2] },
            new() { MidiNotes = [A2], SlideToMidi = A2 + 5, MaxBendSemis = 1 },
            new() { MidiNotes = [A2], SlideSemis = -2 },
            new() { MidiNotes = [A2], Harmonic = true }
        ];
        var hits = 0;

        AllocationProbe.AssertAllocationFree("LivePitchAcceptance.Accepts", () =>
        {
            foreach (var sound in sounds)
            {
                for (var played = A2 - 14; played <= A2 + 30; played++)
                {
                    if (LivePitchAcceptance.Accepts(sound, A2, played)) hits++;
                }
            }
        });

        Assert.True(hits > 0);
    }
}
