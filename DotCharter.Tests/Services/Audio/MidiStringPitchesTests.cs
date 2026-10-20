using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     Issue #37: the MIDI preview played every bass an octave high because it priced
///     (string, fret) off a private guitar table. <see cref="MidiStringPitches" /> is what
///     <c>JuceAudioPlaybackService.UpdateMidiNotes</c> now asks, and it answers with
///     <c>SoundingPitch</c>'s rule.
/// </summary>
public sealed class MidiStringPitchesTests
{
    private static Arrangement Bass(params int[] offsets) => new()
    {
        ArrangementType = ArrangementType.Bass,
        Tuning = offsets.Length == 4 ? ["E", "A", "D", "G"] : [.. Enumerable.Repeat("?", offsets.Length)],
        TuningPitches = [.. offsets.Select(o => new TuningOffset(o))]
    };

    private static int[] OpenStrings(Arrangement arrangement, int count)
    {
        var pitches = new MidiStringPitches();
        pitches.Refresh(arrangement);
        return [.. Enumerable.Range(0, count).Select(s => pitches.MidiFor(s, 0, arrangement.Capo))];
    }

    [Fact]
    public void AFourStringBass_PlaysE1A1D2G2() =>
        Assert.Equal([28, 33, 38, 43], OpenStrings(Bass(0, 0, 0, 0), 4));

    [Fact]
    public void AFrettedBassNote_IsTheOpenStringPlusTheFret()
    {
        var pitches = new MidiStringPitches();
        pitches.Refresh(Bass(0, 0, 0, 0));
        Assert.Equal(33, pitches.MidiFor(0, 5, 0)); // E string, 5th fret = A1
        Assert.Equal(55, pitches.MidiFor(3, 12, 0)); // G string, 12th fret = G3
    }

    [Fact]
    public void ASixStringBassOnAGuitarPathAtA220_PlaysB0ToC3_AndFollowsARetune()
    {
        var bass = DotCharter.Tests.Core.GuitarPathBassShapes.SixStringBassOnAltRhythm();
        var pitches = new MidiStringPitches();
        pitches.Refresh(bass);
        Assert.Equal([23, 28, 33, 38, 43, 48], [.. Enumerable.Range(0, 6).Select(s => pitches.MidiFor(s, 0, 0))]);

        bass.CentOffset = 0; // the same chart re-referenced to A440 is a guitar-register part
        pitches.Refresh(bass);
        Assert.Equal(35, pitches.MidiFor(0, 0, 0));
    }

    [Fact]
    public void AGuitar_IsUnchanged()
    {
        var guitar = new Arrangement { ArrangementType = ArrangementType.Lead };
        Assert.Equal([40, 45, 50, 55, 59, 64], OpenStrings(guitar, 6));
        Assert.Equal([40, 45, 50, 55, 59, 64], OpenStrings(new Arrangement { ArrangementType = ArrangementType.Rhythm }, 6));
    }

    [Fact]
    public void ADropDBass_DropsItsLowString() =>
        Assert.Equal([26, 33, 38, 43], OpenStrings(Bass(-2, 0, 0, 0), 4));

    [Fact]
    public void ALettersOnlyBass_IsReadFromItsLetters()
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, Tuning = ["D", "A", "D", "G"] };
        Assert.Equal([26, 33, 38, 43], OpenStrings(bass, 4));
    }

    /// <summary>
    ///     Five- and six-string basses carry offsets measured against E A D G B (E) — the SNG
    ///     reading <c>TuningUtilities.GetStandardMidiPitches</c> documents — so B E A D G is
    ///     -5 -5 -5 -5 -4 and sounds B0 E1 A1 D2 G2, and B E A D G C adds C3.
    /// </summary>
    [Fact]
    public void FiveAndSixStringBasses_SoundAnOctaveBelowTheirGuitarReading()
    {
        Assert.Equal([23, 28, 33, 38, 43], OpenStrings(Bass(-5, -5, -5, -5, -4), 5));
        Assert.Equal([23, 28, 33, 38, 43, 48], OpenStrings(Bass(-5, -5, -5, -5, -4, -4), 6));
    }

    [Fact]
    public void ACapo_StopsTheOpenString_AndAFretBelowItStillPlaysAtTheCapo()
    {
        var bass = Bass(0, 0, 0, 0);
        bass.Capo = 2;
        var pitches = new MidiStringPitches();
        pitches.Refresh(bass);
        Assert.Equal(30, pitches.MidiFor(0, 0, bass.Capo)); // open behind a capo = fret 2
        Assert.Equal(33, pitches.MidiFor(0, 5, bass.Capo));
        Assert.Equal(30, pitches.MidiFor(0, 1, bass.Capo)); // malformed (L01): plays, at the capo
    }

    [Fact]
    public void NothingIsPlayed_ForAMutedFretOrAStringPastTheSixth()
    {
        var pitches = new MidiStringPitches();
        pitches.Refresh(Bass(0, 0, 0, 0));
        Assert.Equal(0, pitches.MidiFor(0, -1, 0));
        Assert.Equal(0, pitches.MidiFor(6, 3, 0));
        Assert.Equal(0, new MidiStringPitches().MidiFor(0, 3, 0)); // never refreshed
    }

    [Fact]
    public void ALiveRetune_IsPickedUpOnTheNextRefresh()
    {
        var bass = Bass(0, 0, 0, 0);
        var pitches = new MidiStringPitches();
        pitches.Refresh(bass);
        Assert.Equal(28, pitches.MidiFor(0, 0, 0));

        bass.TuningPitches[0].Step = -2;
        pitches.Refresh(bass);
        Assert.Equal(26, pitches.MidiFor(0, 0, 0));

        bass.ArrangementType = ArrangementType.Lead;
        pitches.Refresh(bass);
        Assert.Equal(38, pitches.MidiFor(0, 0, 0));
    }

    /// <summary>The wiring half: the preview asks this type, and no second pitch table survives in the service.</summary>
    [Fact]
    public void ThePreview_PricesNotesThroughIt_AndKeepsNoPitchTableOfItsOwn()
    {
        var source = string.Concat(PlaybackServiceSourceFiles.All().Select(File.ReadAllText));
        Assert.Contains("_midiPitches.MidiFor(n.StringIndex", source);
        Assert.Contains("_midiPitches.MidiFor(cn.StringIndex", source);
        Assert.DoesNotContain("40, 45, 50, 55, 59, 64", source);
    }

    /// <summary>It runs on the 4 ms position tick: an unchanged arrangement costs nothing.</summary>
    [Fact]
    public void RefreshingAnUnchangedArrangement_AllocatesNothing()
    {
        var bass = Bass(0, 0, 0, 0);
        var pitches = new MidiStringPitches();
        pitches.Refresh(bass);
        AllocationProbe.AssertAllocationFree("MidiStringPitches.Refresh + MidiFor", () =>
        {
            for (var i = 0; i < 100; i++)
            {
                pitches.Refresh(bass);
                _ = pitches.MidiFor(i % 4, i % 12, 0);
            }
        });
    }
}
