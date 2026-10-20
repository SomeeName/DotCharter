namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     The pure pitch rules live judging shares: what a charted position sounds like
///     (<see cref="SoundingMidi" />), which played pitches count as that note
///     (<see cref="Accepts" />), and which sounds carry no pitch worth verifying. Stateless
///     and allocation-free, so <see cref="LiveNoteMatcher" />, <see cref="LiveAttackOnsets" />
///     and <see cref="LiveCqtVerifier" /> can all ask on the detection path.
/// </summary>
public static class LivePitchAcceptance
{
    // Natural/pinch harmonics ring at exact overtones of the fretted pitch:
    // +12 (2nd partial), +19 (3rd), +24 (4th), +28 (5th) semitones.
    internal static readonly int[] HarmonicOvertoneSemis = [12, 19, 24, 28];

    /// <summary>Sounds with no reliable charted pitch to verify against the CQT
    /// (full mutes, empty chords, unpitched slides) — in CQT mode these keep the
    /// pushed percussive/timbre evidence path instead of the pull-verifier.</summary>
    internal static bool NeedsTimbreEvidence(ExpectedSound sound)
        => sound.FullMute || sound.MidiNotes.Length == 0 || sound.UnpitchedSlide;

    internal static bool PitchIsUnreliable(ExpectedSound sound)
        => sound.FullMute || sound.SlapOrPop || sound.UnpitchedSlide;

    /// <summary>
    ///     Does <paramref name="played"/> count as "the right note" for the
    ///     charted pitch <paramref name="m"/> of <paramref name="sound"/>? The
    ///     chart pitch itself — ±1 octave for detector sub/octave errors unless
    ///     <paramref name="octaveTolerant"/> is off — the chromatic range a
    ///     slide traverses (<see cref="ExpectedSound.SlideToMidi"/> for a single
    ///     note, the signed shape displacement
    ///     <see cref="ExpectedSound.SlideSemis"/> for a strum), the range a bend
    ///     reaches, and — for harmonics — the exact overtone pitches the node
    ///     rings at.
    ///     <para>
    ///     This is a PREDICATE rather than the <c>HashSet</c> of accepted pitches
    ///     it replaced: that set was rebuilt for every candidate sound of every
    ///     detected attack, i.e. an allocation on the detection path, and the
    ///     chord path could not use it at all (it open-coded a narrower ±12 test,
    ///     which is how harmonic and sliding strums lost their tolerance).
    ///     </para>
    /// </summary>
    internal static bool Accepts(ExpectedSound sound, int m, int played, bool octaveTolerant = true)
    {
        var lo = m;
        var hi = m;
        if (sound.SlideToMidi is { } slideTo)
        {
            lo = Math.Min(lo, slideTo);
            hi = Math.Max(hi, slideTo);
        }

        if (sound.SlideSemis > 0) hi = Math.Max(hi, m + sound.SlideSemis);
        else if (sound.SlideSemis < 0) lo = Math.Min(lo, m + sound.SlideSemis);

        if (sound.MaxBendSemis > 0) hi += sound.MaxBendSemis;

        if (played >= lo && played <= hi) return true;
        if (octaveTolerant &&
            ((played - 12 >= lo && played - 12 <= hi) || (played + 12 >= lo && played + 12 <= hi)))
        {
            return true;
        }

        if (!sound.Harmonic && !sound.PinchHarmonic) return false;
        foreach (var semis in HarmonicOvertoneSemis)
        {
            if (played == m + semis)
                return true;
        }

        return false;
    }

    /// <summary>The highest bend step a note's bend curve reaches, rounded UP to whole
    /// semitones — the ceiling <see cref="Accepts" /> widens the accepted range to.</summary>
    internal static int MaxBendSemis(IEnumerable<BendValue> bends)
    {
        var max = 0.0;
        foreach (var b in bends)
        {
            if (b.Step > max)
                max = b.Step;
        }

        return (int)Math.Ceiling(max);
    }

    /// <summary>
    ///     Sounding MIDI note for a chart position (capo raises open strings).
    ///     The derivation live judging uses — the v0.29 audio lint
    ///     (`AudioChartLintService`) and the chart-from-audio wizard's string map
    ///     both call it too, so nothing that LISTENS to real audio can disagree
    ///     with live judging about what a charted note sounds like.
    ///     <para>
    ///     <b>A bass sounds an OCTAVE below where it is charted</b>, exactly as
    ///     <see cref="SoundingPitch.MidiFor" /> and the SNG mapper have always
    ///     said, and as <c>ArrangementTunerLogic</c> (whose bass anchor is E1 =
    ///     28) tells the player when they tune up. This method must not anchor
    ///     every arrangement at E2 = <see cref="StandardOpenStrings.GuitarLowEMidi" />, or a bass chart
    ///     would expect pitches twelve semitones above what the instrument produces.
    ///     The classic candidate path would hide it behind its ±12 detector-error
    ///     tolerance; the two paths that deliberately DROP that tolerance would
    ///     not. With <see cref="LiveNoteMatcher.StringAwareVerification" /> on, every bass note
    ///     would miss. In CQT mode it would be worse than a plain mismatch: the played
    ///     E1's second harmonic lands exactly on the charted E2 bin, so
    ///     <c>JuceChartVerifier</c>'s sub-octave veto — which exists to stop a
    ///     player scoring by playing an octave DOWN — would fire on the correct note
    ///     and refuse it. <c>LiveCqtVerifier.LiveMinMidi</c> is 28
    ///     "for bass" so that a pitch that low can be asked for.
    ///     </para>
    ///     <para>
    ///     Offsets come from <see cref="TuningOffsetResolution" /> rather than
    ///     from <c>TuningPitches</c> directly, for the reason that type states:
    ///     a chart may carry its tuning as note LETTERS only, and reading the
    ///     offsets raw judged such a chart as E standard.
    ///     </para>
    ///     <para>
    ///     <b>A tuning reference an octave down moves every note an octave down
    ///     too</b> (<see cref="ArrangementInstrument.ReferenceOctaveSemitones" />).
    ///     That is how a 5/6-string bass is charted on a GUITAR path (A220,
    /// <c>CentOffset</c> −1200): its low B sounds B0 = 23, and anchored
    ///     on the guitar's B1 = 35 every note would sit an octave above what the
    ///     player's bass produces — the bug the bass octave above fixed for the
    ///     Bass path, back again for these. The bass-path octave stays keyed on the
    ///     PATH: the offsets are measured from the bass's open strings there.
    ///     </para>
    /// </summary>
    public static int? SoundingMidi(Arrangement arrangement, int stringIndex, int fret)
    {
        if (stringIndex < 0) return null;
        // StandardOpenStrings: a 7-string's string 0 is B1, not E2.
        var open = StandardOpenStrings.SemitonesAboveLowE(arrangement.StringCount, stringIndex);
        var tuning = TuningOffsetResolution.For(arrangement, stringIndex);
        var soundingFret = fret == 0 && arrangement.Capo > 0 ? arrangement.Capo : fret;
        var bassDrop = arrangement.ArrangementType == ArrangementType.Bass ? -12 : 0;
        var referenceOctave = ArrangementInstrument.ReferenceOctaveSemitones(arrangement.CentOffset);
        return StandardOpenStrings.GuitarLowEMidi + open + tuning + soundingFret + bassDrop + referenceOctave;
    }
}
