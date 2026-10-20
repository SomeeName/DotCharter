using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class LiveNoteMatcherTests
{
    // 120 BPM => 500 ms / beat.
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement NewArr(int capo = 0)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = capo };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static GuitarNote Note(int beat, int fret, int stringIdx = 0,
        MuteType mute = MuteType.None, HarmonicType harmonic = HarmonicType.None)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            Fret = fret,
            StringIndex = stringIdx,
            Mute = mute,
            Harmonic = harmonic
        };
    }

    private static LiveNoteMatcher Loaded(Arrangement arr)
    {
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // Low E string (index 0), fret 3 => E2(40) + 3 = MIDI 43 (G2).
    [Fact]
    public void SingleNote_CorrectPitchInsideWindow_IsHit()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // at 1000 ms
        var matcher = Loaded(arr);

        var result = matcher.OfferNote(midi: 43, timeMs: 1040);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(100, matcher.Stats.Score);
    }

    [Fact]
    public void SingleNote_WrongPitch_DoesNotHit_AndTimesOutAsMiss()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // at 1000 ms
        var matcher = Loaded(arr);

        Assert.Null(matcher.OfferNote(midi: 45, timeMs: 1000));

        var events = matcher.AdvanceTo(playheadMs: 1500);
        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
        Assert.Equal(0, matcher.Stats.Streak);
        Assert.Equal(1, matcher.Stats.Misses);
    }

    [Fact]
    public void SingleNote_OutsideHitWindow_IsIgnored()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // at 1000 ms
        var matcher = Loaded(arr);

        Assert.Null(matcher.OfferNote(midi: 43, timeMs: 400));
        Assert.Equal(0, matcher.Stats.Hits);
    }

    [Fact]
    public void Capo_RaisesOpenStringPitch()
    {
        var arr = NewArr(capo: 2);
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 0)); // open low E, capo 2 => MIDI 42
        var matcher = Loaded(arr);

        Assert.Null(matcher.OfferNote(midi: 40, timeMs: 1000));  // un-capoed open E: wrong
        Assert.NotNull(matcher.OfferNote(midi: 42, timeMs: 1000));
    }

    [Fact]
    public void SingleNote_OctaveError_StillHits()
    {
        // YIN routinely reports the octave/sub-octave of the played note;
        // matching is pitch-class based so a correct performance never
        // reads as a miss over a detector octave error.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // G2 = 43
        var matcher = Loaded(arr);

        var result = matcher.OfferNote(midi: 43 + 12, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void Harmonic_AcceptsOctaveOvertone()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 12, harmonic: HarmonicType.Normal)); // E2+12 = 52
        var matcher = Loaded(arr);

        var result = matcher.OfferNote(midi: 52 + 12, timeMs: 1000); // rings an octave up
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void PalmMute_HighCentroidAttack_CountsAsWrongTechnique()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));
        var matcher = Loaded(arr);

        var result = matcher.OfferNote(midi: 43, timeMs: 1000, centroidHz: 5200f);

        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.HitWrongTechnique, result!.Value.Verdict);
        Assert.Equal(50, matcher.Stats.Score); // half score for wrong technique
    }

    [Fact]
    public void PalmMute_LowCentroidAttack_IsFullHit()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));
        var matcher = Loaded(arr);

        var result = matcher.OfferNote(midi: 43, timeMs: 1000, centroidHz: 900f);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void FullMute_RequiresPercussiveEvidence()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Full));
        var matcher = Loaded(arr);

        // A cleanly pitched attack is NOT a muted stroke.
        Assert.Null(matcher.OfferNote(midi: 99, timeMs: 1000));

        // A percussive-classified event is.
        var result = matcher.OfferPitches([], timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void Chord_ChromaWithExpectedPitchClasses_Hits()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // G2  -> pc 7
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // D3  -> pc 2
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 }); // G3  -> pc 7
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        var chroma = new float[12];
        chroma[7] = 0.5f; // G
        chroma[2] = 0.3f; // D
        chroma[11] = 0.2f; // noise

        var result = matcher.OfferChroma(chroma, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void Chord_ChromaMissingTones_DoesNotHit()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        var chroma = new float[12];
        chroma[0] = 0.9f; // all energy on C — wrong chord

        Assert.Null(matcher.OfferChroma(chroma, timeMs: 1000));
    }

    [Fact]
    public void Streak_BuildsMultiplier_AndMissResetsIt()
    {
        var arr = NewArr();
        for (var beat = 1; beat <= 11; beat++)
            arr.Levels[0].Notes.Add(Note(beat, fret: 3));
        var matcher = Loaded(arr);

        for (var beat = 1; beat <= 10; beat++)
            Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: beat * 500.0));

        Assert.Equal(10, matcher.Stats.Streak);
        Assert.Equal(2, matcher.Stats.Multiplier);

        matcher.AdvanceTo(playheadMs: 11 * 500.0 + 500); // note 11 expires
        Assert.Equal(0, matcher.Stats.Streak);
        Assert.Equal(10, matcher.Stats.BestStreak);
    }

    [Fact]
    public void Rewind_ReArmsSoundsAndResetsTheirProgress()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // 1000 ms
        var matcher = Loaded(arr);

        matcher.AdvanceTo(playheadMs: 4000); // expires as a miss
        Assert.Equal(1, matcher.Stats.Misses);

        // Seeking back clears the verdicts for the replayed region — the
        // totals must NOT grow, the earlier miss simply disappears.
        matcher.Rewind(playheadMs: 500);
        Assert.Equal(0, matcher.Stats.Misses);
        Assert.Equal(0, matcher.Stats.Judged);

        var result = matcher.OfferNote(midi: 43, timeMs: 1000);
        Assert.NotNull(result); // judgeable again after the seek back
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Judged);
        Assert.Equal(1, matcher.Stats.TotalSounds); // denominator never inflates
    }

    [Fact]
    public void Rewind_KeepsVerdictsBeforeTheSeekTarget()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 1, fret: 3)); // 500 ms
        arr.Levels[0].Notes.Add(Note(beat: 6, fret: 3)); // 3000 ms
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: 500)); // hit the first
        matcher.AdvanceTo(playheadMs: 4000);                      // second expires
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Misses);

        matcher.Rewind(playheadMs: 2000); // between the two sounds
        Assert.Equal(1, matcher.Stats.Hits);   // first hit survives
        Assert.Equal(0, matcher.Stats.Misses); // second is re-armed
    }

    [Fact]
    public void Rewind_BestStreakSurvivesAsSessionBest()
    {
        var arr = NewArr();
        for (var beat = 1; beat <= 5; beat++)
            arr.Levels[0].Notes.Add(Note(beat, fret: 3));
        var matcher = Loaded(arr);

        for (var beat = 1; beat <= 5; beat++)
            Assert.NotNull(matcher.OfferNote(midi: 43, timeMs: beat * 500.0));
        Assert.Equal(5, matcher.Stats.BestStreak);

        matcher.Rewind(playheadMs: 0);
        Assert.Equal(0, matcher.Stats.Streak);
        Assert.Equal(5, matcher.Stats.BestStreak);
    }

    // --- v4 leniency rules -------------------------------------------------

    [Fact]
    public void Slide_AcceptsAnyPitchAlongTheTraversedRange()
    {
        var arr = NewArr();
        var note = Note(beat: 2, fret: 3); // G2 = 43
        note.SlideTo = 7;                  // slides up to B2 = 47
        arr.Levels[0].Notes.Add(note);
        var matcher = Loaded(arr);

        // A pitch mid-slide (A2 = 45, pc 9) must count as the right note.
        var result = matcher.OfferNote(midi: 45, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void UnpitchedSlide_AcceptsAnyAttack()
    {
        var arr = NewArr();
        var note = Note(beat: 2, fret: 3);
        note.UnpitchedSlide = true;
        arr.Levels[0].Notes.Add(note);
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 99, timeMs: 1000));
    }

    [Fact]
    public void Bend_AcceptsPitchesUpToTheBendTarget()
    {
        var arr = NewArr();
        var note = Note(beat: 2, fret: 3); // G2 = 43
        note.BendValues.Add(new BendValue(new FractionalPosition(2), step: 2.0)); // whole step
        arr.Levels[0].Notes.Add(note);
        var matcher = Loaded(arr);

        // Bent up a whole step => A2 (45, pc 9) is correct playing.
        var result = matcher.OfferNote(midi: 45, timeMs: 1000);
        Assert.NotNull(result);
    }

    [Fact]
    public void PinchHarmonic_AcceptsFifthOvertone()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, harmonic: HarmonicType.Pinch)); // G2 = 43
        var matcher = Loaded(arr);

        // Pinches ring at the 3rd partial: pc + 7 (D here).
        var result = matcher.OfferNote(midi: 43 + 19, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void SlapNote_AcceptsPercussiveEvidenceOrMatchingPitch()
    {
        var arr = NewArr();
        var note = Note(beat: 2, fret: 3); // G2 = 43
        note.BassPicking = BassPickingType.Slap;
        arr.Levels[0].Notes.Add(note);
        var matcher = Loaded(arr);

        // Wrong pitch without percussive evidence: no hit.
        Assert.Null(matcher.OfferNote(midi: 99, timeMs: 1000));

        // Slap-classified transient counts even when pitch is garbled.
        Span<LivePitch> garbled = [new LivePitch(99, 0f, 1f)];
        var result = matcher.OfferPitches(garbled, timeMs: 1000, techniques: LiveTechniqueFlags.Slap);
        Assert.NotNull(result);
    }

    [Fact]
    public void Chord_SingleToneAttack_DoesNotHit()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // G2
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // D3
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // v6 strictness: one tone is not a chord (coverage 60% of 2 = 2).
        Assert.Null(matcher.OfferNote(midi: 50, timeMs: 1000)); // D3 only
    }

    [Fact]
    public void MutedStrumChord_RequiresPercussiveEvidence()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.Full });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Mute = MuteType.Full });
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // A chroma frame alone (any noise) no longer satisfies a chuck…
        var chroma = new float[12];
        chroma[0] = 0.2f;
        Assert.Null(matcher.OfferChroma(chroma, timeMs: 1000));

        // …a percussive-classified attack does.
        var result = matcher.OfferPitches([], timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void SingleNote_ChromaAlone_NeverHits()
    {
        // The v5 chroma shortcut let ring-out of earlier strings hit fresh
        // notes; single notes now verify via salient pitch candidates only.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // G2 -> pc 7
        var matcher = Loaded(arr);

        var chroma = new float[12];
        chroma[7] = 0.9f;
        Assert.Null(matcher.OfferChroma(chroma, timeMs: 1000));
    }

    [Fact]
    public void SingleNote_LowSalienceCandidate_DoesNotHit()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // G2 = 43
        var matcher = Loaded(arr);

        // The right pitch as a WEAK candidate (ring-out) is not evidence.
        Span<LivePitch> pitches =
        [
            new LivePitch(52, 0f, 1.0f),  // strong wrong note
            new LivePitch(43, 0f, 0.15f)  // weak right note
        ];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void Chord_HalfTheTonesPresent_IsEnough()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // pc 7
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // pc 2
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 4 }); // F#3 -> pc 6
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // Only two of the three distinct classes ring — still a hit at 50%.
        var chroma = new float[12];
        chroma[7] = 0.5f;
        chroma[2] = 0.3f;
        var result = matcher.OfferChroma(chroma, timeMs: 1000);
        Assert.NotNull(result);
    }

    [Fact]
    public void Chord_QuietButClean_RelativeThresholdAccepts()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // pc 7
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // pc 2
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // Both tones ring but the whole frame is quiet: below the absolute
        // floor, yet each ≥ 35% of the frame maximum.
        var chroma = new float[12];
        chroma[7] = 0.05f;
        chroma[2] = 0.03f;
        var result = matcher.OfferChroma(chroma, timeMs: 1000);
        Assert.NotNull(result);
    }

    [Fact]
    public void Chord_DetectedPitchSetCoveringAllTones_Hits()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // G2 = 43
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // D3 = 50
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        Span<LivePitch> pitches =
        [
            new LivePitch(43, 0f, 1.0f), new LivePitch(50, 0f, 0.7f), new LivePitch(62, 0f, 0.4f)
        ];
        var result = matcher.OfferPitches(pitches, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void Chord_DetectedPitchSetMissingAllTones_DoesNotHit()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // pc 7
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // pc 2
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        Span<LivePitch> pitches =
        [
            new LivePitch(48, 0f, 1.0f), new LivePitch(41, 0f, 0.8f)
        ];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000)); // C, F
    }

    [Fact]
    public void SingleNote_PitchSetWithOneMatchingCandidate_Hits()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // G2 = 43
        var matcher = Loaded(arr);

        // The polyphonic detector may report residual candidates alongside
        // the fresh note — a SALIENT matching candidate suffices.
        Span<LivePitch> pitches =
        [
            new LivePitch(45, 0f, 1.0f), new LivePitch(43, 0f, 0.6f)
        ];
        var result = matcher.OfferPitches(pitches, timeMs: 1000);
        Assert.NotNull(result);
    }

    [Fact]
    public void HigherStrings_UseStandardTuningOffsets()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 0, stringIdx: 5)); // high E open = MIDI 64
        var matcher = Loaded(arr);

        Assert.NotNull(matcher.OfferNote(midi: 64, timeMs: 1000));
    }

    [Fact]
    public void TuningOffset_ShiftsExpectedPitch()
    {
        var arr = NewArr();
        arr.TuningPitches.Add(new TuningOffset(-2)); // drop-D style low string
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 0, stringIdx: 0)); // sounds D2 = 38
        var matcher = Loaded(arr);

        Assert.Null(matcher.OfferNote(midi: 40, timeMs: 1000));
        Assert.NotNull(matcher.OfferNote(midi: 38, timeMs: 1000));
    }

    // --- v6.1: pitch is the gate, technique is lenient ---------------------

    [Fact]
    public void PalmMutedNote_ConfirmedDamping_AcceptsWeakMatchingPitch()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm)); // G2 = 43
        var matcher = Loaded(arr);

        // A chug leaves the right pitch only faintly extractable — with the
        // damping confirmed by the analyzer that faint match is enough.
        Span<LivePitch> pitches = [new LivePitch(43, 0f, 0.05f)];
        var result = matcher.OfferPitches(pitches, timeMs: 1000,
            techniques: LiveTechniqueFlags.PalmMute);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void PalmMutedNote_FullyChokedAttack_Hits()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));
        var matcher = Loaded(arr);

        // Hard chug with no extractable pitch at all: the confirmed damping
        // is the whole evidence, and it verifies.
        var result = matcher.OfferPitches([], timeMs: 1000,
            techniques: LiveTechniqueFlags.PalmMute | LiveTechniqueFlags.Percussive);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void PalmMutedNote_ConfirmedDampingButWrongPitch_DoesNotHit()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm)); // G2 = 43
        var matcher = Loaded(arr);

        // Damped leniency relaxes SALIENCE, never which note was played.
        Span<LivePitch> pitches = [new LivePitch(48, 0f, 1.0f)];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000,
            techniques: LiveTechniqueFlags.PalmMute));
    }

    [Fact]
    public void PalmMutedChord_ChugExposingOnlyTheRoot_Hits()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Mute = MuteType.Palm }); // G2 = 43
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Mute = MuteType.Palm }); // D3 = 50
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // A muted power-chord chug rarely exposes more than its lowest
        // string — one verified tone plus confirmed damping is a hit.
        Span<LivePitch> pitches = [new LivePitch(43, 0f, 0.1f)];
        var result = matcher.OfferPitches(pitches, timeMs: 1000,
            techniques: LiveTechniqueFlags.PalmMute | LiveTechniqueFlags.Percussive);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void PalmMutedNote_MissingFlagWithDarkAttack_IsStillFullHit()
    {
        // An analyzer false negative (no PalmMute flag, centroid dark or
        // unknown) must not demote a correctly played mute.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));
        var matcher = Loaded(arr);

        Span<LivePitch> pitches = [new LivePitch(43, 0f, 1.0f)];
        var result = matcher.OfferPitches(pitches, timeMs: 1000,
            techniques: LiveTechniqueFlags.Legato); // flags present, none about damping
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void LegatoNote_AcceptsSofterCandidateThanAPickedNote()
    {
        var arr = NewArr();
        var hammered = Note(beat: 2, fret: 5); // A2 = 45
        hammered.Hopo = HopoType.HammerOn;
        arr.Levels[0].Notes.Add(hammered);
        var matcher = Loaded(arr);

        // 0.18 sits below the picked-note floor but above the legato floor.
        Span<LivePitch> pitches = [new LivePitch(45, 0f, 0.18f)];
        var result = matcher.OfferPitches(pitches, timeMs: 1000);
        Assert.NotNull(result);
        Assert.Equal(LivePlayVerdict.Hit, result!.Value.Verdict);
    }

    [Fact]
    public void PickedNote_SofterCandidate_StillDoesNotHit()
    {
        // Control for the legato floor: the same weak candidate on a plain
        // picked note keeps failing verification.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 5)); // A2 = 45
        var matcher = Loaded(arr);

        Span<LivePitch> pitches = [new LivePitch(45, 0f, 0.18f)];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    // --- v6.2: percussive veto — a chug must never verify a clean note ----

    [Fact]
    public void PercussiveAttack_WithMatchingNoisyCandidate_NeverHitsCleanNote()
    {
        // Muted chugging produces broadband noise whose normalized-salience
        // candidates can land on the expected pitch by chance. The attack's
        // Percussive classification vetoes it — the note expires as a miss.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // clean G2 = 43
        var matcher = Loaded(arr);

        Span<LivePitch> noisy =
        [
            new LivePitch(43, 0f, 1.0f), new LivePitch(55, 0f, 0.6f)
        ];
        Assert.Null(matcher.OfferPitches(noisy, timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute));

        var events = matcher.AdvanceTo(playheadMs: 1500);
        Assert.Single(events);
        Assert.Equal(LivePlayVerdict.Miss, events[0].Verdict);
    }

    [Fact]
    public void PalmMuteFlaggedAttack_DoesNotHitCleanNote()
    {
        // Softer chugs may only trip the decay-based PalmMute flag (no
        // Percussive): still not a clean note.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // clean G2 = 43
        var matcher = Loaded(arr);

        Span<LivePitch> pitches = [new LivePitch(43, 0f, 1.0f)];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000,
            techniques: LiveTechniqueFlags.PalmMute));
    }

    [Fact]
    public void PercussiveAttack_NeverHitsCleanChord_PitchOrChromaPath()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 }); // G2 = 43
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 }); // D3 = 50
        arr.Levels[0].Chords.Add(chord);
        var matcher = Loaded(arr);

        // Pitch path: chug candidates covering both tones still veto.
        Span<LivePitch> noisy =
        [
            new LivePitch(43, 0f, 1.0f), new LivePitch(50, 0f, 0.8f)
        ];
        Assert.Null(matcher.OfferPitches(noisy, timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive));

        // Chroma fallback: broadband chug energy on the right pitch classes
        // still vetoes when the attack is percussive-classified.
        var chroma = new float[12];
        chroma[7] = 0.5f;
        chroma[2] = 0.4f;
        Assert.Null(matcher.OfferChroma(chroma, timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute));

        // Control: the same evidence without percussive flags is a hit.
        Assert.NotNull(matcher.OfferChroma(chroma, timeMs: 1000));
    }

    [Fact]
    public void PercussiveAttack_StillHitsPalmMutedAndSlapSounds()
    {
        // The veto must not break the sounds a chug legitimately plays.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm)); // 1000 ms
        var slap = Note(beat: 4, fret: 3); // 2000 ms
        slap.BassPicking = BassPickingType.Slap;
        arr.Levels[0].Notes.Add(slap);
        var matcher = Loaded(arr);

        Span<LivePitch> faint = [new LivePitch(43, 0f, 0.05f)];
        var muted = matcher.OfferPitches(faint, timeMs: 1000,
            techniques: LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute);
        Assert.NotNull(muted);
        Assert.Equal(LivePlayVerdict.Hit, muted!.Value.Verdict);

        var slapped = matcher.OfferPitches([], timeMs: 2000,
            techniques: LiveTechniqueFlags.Slap | LiveTechniqueFlags.Percussive);
        Assert.NotNull(slapped);
    }

    [Fact]
    public void ChugTrainOverMixedChart_OnlyMutedNotesHit()
    {
        // The owner-reported bug: constant muted chugging while the chart
        // alternates clean and palm-muted notes. Chugs may only clear the
        // muted ones; every clean note must expire as a miss.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 1, fret: 3));                       // clean, 500 ms
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3, mute: MuteType.Palm));  // 1000 ms
        arr.Levels[0].Notes.Add(Note(beat: 3, fret: 3));                       // clean, 1500 ms
        arr.Levels[0].Notes.Add(Note(beat: 4, fret: 3, mute: MuteType.Palm));  // 2000 ms
        var matcher = Loaded(arr);

        Span<LivePitch> chug =
        [
            new LivePitch(43, 0f, 1.0f), new LivePitch(31, 0f, 0.5f)
        ];
        for (var beat = 1; beat <= 4; beat++)
        {
            matcher.OfferPitches(chug, timeMs: beat * 500.0,
                techniques: LiveTechniqueFlags.Percussive | LiveTechniqueFlags.PalmMute);
        }

        matcher.AdvanceTo(playheadMs: 3000);
        Assert.Equal(2, matcher.Stats.Hits);   // the two palm mutes
        Assert.Equal(2, matcher.Stats.Misses); // the two clean notes
    }

    [Fact]
    public void RandomMashing_DoesNotClearNotes()
    {
        // Anti-cheat: strong wrong-pitch attacks all over the window never
        // verify, and the note still expires as a miss.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(Note(beat: 2, fret: 3)); // G2 = 43
        var matcher = Loaded(arr);

        Span<LivePitch> mash = stackalloc LivePitch[2];
        foreach (var midi in new[] { 45, 46, 49, 58, 61 })
        {
            mash[0] = new LivePitch(midi, 0f, 1.0f);
            mash[1] = new LivePitch(midi + 7, 0f, 0.8f);
            Assert.Null(matcher.OfferPitches(mash, timeMs: 1000 + midi));
        }

        matcher.AdvanceTo(playheadMs: 2000);
        Assert.Equal(0, matcher.Stats.Hits);
        Assert.Equal(1, matcher.Stats.Misses);
    }
}
