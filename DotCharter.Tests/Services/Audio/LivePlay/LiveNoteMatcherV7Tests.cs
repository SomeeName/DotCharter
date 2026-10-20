using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Detection v7: chord voicing strictness, string-aware verification
///     (drop-pedal-aware exact pitch map), per-note confidence, hit-window
///     presets and the verdict-preserving difficulty reload.
/// </summary>
public sealed class LiveNoteMatcherV7Tests
{
    private static List<BeatMarker> Beats(int count = 16, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static Arrangement NewArr()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    /// <summary>G5 power chord: G2 (43) + D3 (50) at beat 2 (1000 ms).</summary>
    private static Arrangement PowerChordArr()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });  // G2 = 43
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });  // D3 = 50
        arr.Levels[0].Chords.Add(chord);
        return arr;
    }

    private static LiveNoteMatcher Load(Arrangement arr, LiveNoteMatcher matcher)
    {
        matcher.Load(arr, arr.Levels[0], Beats());
        return matcher;
    }

    // ---- chord voicing strictness ------------------------------------------

    [Fact]
    public void StandardChord_AcceptsOctaveShiftedTone()
    {
        var matcher = Load(PowerChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(55, 0f, 1f),  // G3 — octave error for the G2 tone
            new LivePitch(50, 0f, 0.8f) // D3 exact
        ];
        Assert.NotNull(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void StrictChord_RejectsOctaveShiftedVoicing()
    {
        var matcher = Load(PowerChordArr(),
            new LiveNoteMatcher { ChordStrictness = LiveChordVoicingStrictness.Strict });
        Span<LivePitch> wrongVoicing =
        [
            new LivePitch(55, 0f, 1f),
            new LivePitch(50, 0f, 0.8f)
        ];
        Assert.Null(matcher.OfferPitches(wrongVoicing, timeMs: 1000));

        Span<LivePitch> exactVoicing =
        [
            new LivePitch(43, 0f, 1f),
            new LivePitch(50, 0f, 0.8f)
        ];
        Assert.NotNull(matcher.OfferPitches(exactVoicing, timeMs: 1000));
    }

    [Fact]
    public void StrictChord_DisablesChromaFallback()
    {
        var matcher = Load(PowerChordArr(),
            new LiveNoteMatcher { ChordStrictness = LiveChordVoicingStrictness.Strict });
        var chroma = new float[12];
        chroma[43 % 12] = 0.6f;
        chroma[50 % 12] = 0.4f;
        Assert.Null(matcher.OfferChroma(chroma, timeMs: 1000));
    }

    [Fact]
    public void LenientChord_AcceptsPartialCoverage()
    {
        var matcher = Load(PowerChordArr(),
            new LiveNoteMatcher { ChordStrictness = LiveChordVoicingStrictness.Lenient });
        Span<LivePitch> oneTone = [new LivePitch(50, 0f, 1f)];
        Assert.NotNull(matcher.OfferPitches(oneTone, timeMs: 1000));
    }

    [Fact]
    public void StandardChord_RejectsPartialCoverage()
    {
        var matcher = Load(PowerChordArr(), new LiveNoteMatcher());
        Span<LivePitch> oneTone = [new LivePitch(50, 0f, 1f)];
        Assert.Null(matcher.OfferPitches(oneTone, timeMs: 1000));
    }

    // ---- v7.1 chord leniency (real-strum energy split) -----------------------

    /// <summary>E-shape chord with four distinct tones at beat 2.</summary>
    private static Arrangement BigChordArr()
    {
        var arr = NewArr();
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0 }); // E2 = 40
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 }); // B2 = 47
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 }); // E3 = 52
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 1 }); // G#3 = 56
        arr.Levels[0].Chords.Add(chord);
        return arr;
    }

    [Fact]
    public void StandardChord_SecondaryToneCountsAtHalfFloor()
    {
        // Real strums bury non-root tones: D3 comes back at 0.12 salience
        // (below the 0.20 tone floor, above half of it). One fully salient
        // tone anchors the attack, the weak one still counts.
        var matcher = Load(PowerChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(43, 0f, 1f),
            new LivePitch(50, 0f, 0.12f)
        ];
        Assert.NotNull(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void StandardChord_RootPlusOneToneVerifiesDenseVoicing()
    {
        // 4 distinct tones would need 3 under pure coverage; root-anchored
        // acceptance passes with the bass tone plus any other chord tone.
        var matcher = Load(BigChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(40, 0f, 0.9f),
            new LivePitch(52, 0f, 0.25f)
        ];
        Assert.NotNull(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void StandardChord_WithoutRoot_StillNeedsFullCoverage()
    {
        // Two mid tones without the bass (and not octave-images of it):
        // no root anchor, coverage (3 of 4) not met — stays a miss so
        // wrong chords keep failing.
        var matcher = Load(BigChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(47, 0f, 0.9f),
            new LivePitch(56, 0f, 0.6f)
        ];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void StandardChord_AllWeakTones_StaysRejected()
    {
        // Nothing reaches the full tone floor: no anchor, no verification —
        // a noise floor full of faint coincidences must not strum chords.
        var matcher = Load(PowerChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(43, 0f, 0.15f),
            new LivePitch(50, 0f, 0.15f)
        ];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    [Fact]
    public void StrictChord_KeepsFullFloorForEveryTone()
    {
        var matcher = Load(PowerChordArr(),
            new LiveNoteMatcher { ChordStrictness = LiveChordVoicingStrictness.Strict });
        Span<LivePitch> pitches =
        [
            new LivePitch(43, 0f, 1f),
            new LivePitch(50, 0f, 0.12f) // below the full floor — Strict says no
        ];
        Assert.Null(matcher.OfferPitches(pitches, timeMs: 1000));
    }

    // ---- v7.1 palm-mute leniency ---------------------------------------------

    [Fact]
    public void PalmMutedNote_VerifiesAtLegatoFloor_WithoutConfirmedDamping()
    {
        // Soft mutes often defeat the percussive classifier, so the analyzer
        // reports NO damping flags — the pitch evidence is weak but real.
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 3, // G2 = 43
            Mute = MuteType.Palm
        });
        var matcher = Load(arr, new LiveNoteMatcher());

        // 0.18 sits between the legato floor (0.15) and the note floor (0.25).
        Span<LivePitch> weak = [new LivePitch(43, 0f, 0.18f)];
        Assert.NotNull(matcher.OfferPitches(weak, timeMs: 1000));
    }

    [Fact]
    public void PalmMutedNote_WrongPitch_StillMisses()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 3,
            Mute = MuteType.Palm
        });
        var matcher = Load(arr, new LiveNoteMatcher());

        Span<LivePitch> wrong = [new LivePitch(46, 0f, 0.9f)];
        Assert.Null(matcher.OfferPitches(wrong, timeMs: 1000));
    }

    // ---- string-aware verification -------------------------------------------

    [Fact]
    public void StringAware_DropsOctaveToleranceForKnownStrings()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 // G2 = 43
        });

        var tolerant = Load(arr, new LiveNoteMatcher());
        Assert.NotNull(tolerant.OfferNote(midi: 55, timeMs: 1000)); // octave up passes

        var aware = Load(arr, new LiveNoteMatcher { StringAwareVerification = true });
        Assert.Null(aware.OfferNote(midi: 55, timeMs: 1000));       // exact pitch required
        Assert.NotNull(aware.OfferNote(midi: 43, timeMs: 1000));
    }

    [Fact]
    public void StringAware_StillAcceptsHarmonicOvertones()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 12,
            Harmonic = HarmonicType.Normal // E3 = 52
        });

        var aware = Load(arr, new LiveNoteMatcher { StringAwareVerification = true });
        Assert.NotNull(aware.OfferNote(midi: 52 + 12, timeMs: 1000)); // 2nd partial
    }

    // ---- per-note confidence ---------------------------------------------------

    [Fact]
    public void NoteHit_CarriesVerifyingCandidateSalience()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 3
        });
        var matcher = Load(arr, new LiveNoteMatcher());

        Span<LivePitch> pitches =
        [
            new LivePitch(99, 0f, 1f),   // wrong loud candidate
            new LivePitch(43, 0f, 0.55f) // the chart note, softer
        ];
        var result = matcher.OfferPitches(pitches, timeMs: 1000);

        Assert.NotNull(result);
        Assert.Equal(0.55f, result!.Value.Confidence, 2);
    }

    [Fact]
    public void ChordHit_CarriesVerifiedToneFraction()
    {
        var matcher = Load(PowerChordArr(), new LiveNoteMatcher());
        Span<LivePitch> pitches =
        [
            new LivePitch(43, 0f, 1f),
            new LivePitch(50, 0f, 0.9f)
        ];
        var result = matcher.OfferPitches(pitches, timeMs: 1000);
        Assert.Equal(1f, result!.Value.Confidence, 2);
    }

    [Fact]
    public void Miss_HasZeroConfidence()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 3
        });
        var matcher = Load(arr, new LiveNoteMatcher());

        var events = matcher.AdvanceTo(playheadMs: 2000);
        Assert.Single(events);
        Assert.Equal(0f, events[0].Confidence, 3);
    }

    // ---- hit-window presets ------------------------------------------------------

    [Fact]
    public void HitWindowPresets_MapToMatcherInitProps()
    {
        var lenient = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Lenient);
        var normal = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal);
        var strict = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Strict);

        Assert.True(lenient.HitWindowLateMs > normal.HitWindowLateMs);
        Assert.True(strict.HitWindowLateMs < normal.HitWindowLateMs);
        Assert.True(lenient.MinNoteSalience < normal.MinNoteSalience);
        Assert.True(strict.MinNoteSalience > normal.MinNoteSalience);
    }

    [Fact]
    public void HitWindowParse_IsForgiving()
    {
        Assert.Equal(LiveHitWindowPreset.Lenient, LiveHitWindows.Parse("lenient"));
        Assert.Equal(LiveHitWindowPreset.Strict, LiveHitWindows.Parse(" STRICT "));
        Assert.Equal(LiveHitWindowPreset.Normal, LiveHitWindows.Parse(null));
        Assert.Equal(LiveHitWindowPreset.Normal, LiveHitWindows.Parse("banana"));
    }

    // ---- verdict-preserving reload -------------------------------------------------

    [Fact]
    public void Reload_KeepsJudgedHistoryAndSwapsFutureLevels()
    {
        // Phrase "riff" iterated twice (beats 0.. and 8..); level 0 has one
        // note per iteration (beat 2 / beat 10), level 1 has two (adds beat 3
        // / beat 11).
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(10), StringIndex = 0, Fret = 3 });
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 0, Fret = 5 });
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(10), StringIndex = 0, Fret = 3 });
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(11), StringIndex = 0, Fret = 5 });
        arr.Levels.Add(level0);
        arr.Levels.Add(level1);
        arr.Phrases.Add("riff");
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 0));

        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(32), _ => 0);
        Assert.Equal(2, matcher.Stats.TotalSounds);

        // Hit the first iteration's note (1000 ms), then level the phrase up
        // and reload at playhead 3000 ms.
        Assert.NotNull(matcher.OfferNote(43, 1000));
        matcher.Reload(arr, Beats(32), _ => 1, playheadMs: 3000);

        // History before the horizon survives; the FUTURE got denser and the
        // PAST did not. Iteration 0 keeps the single note it was played with;
        // only iteration 1 moves to level 1's two. This assertion read 4 until
        // 2026-09-05 — the reload rebuilt the played iteration at the new level
        // too, which put level 1's beat-3 note BEHIND the cursor where nothing
        // can ever judge it (see DynamicDifficultyReloadTests).
        Assert.Equal(1, matcher.Stats.Hits);
        Assert.Equal(3, matcher.Stats.TotalSounds);
        Assert.All(matcher.Expected.Where(s => s.PhraseIterationIndex == 0),
            sound => Assert.Equal(0, sound.LevelIndex));
        var judged = matcher.Expected.Count(s => s.Verdict != null);
        Assert.Equal(1, judged);
        Assert.Equal(43, matcher.Expected.First(s => s.Verdict != null).MidiNotes[0]);

        // Future sounds are unjudged and come from level 1.
        foreach (var sound in matcher.Expected.Where(s => s.TimeMs > 3000))
        {
            Assert.Null(sound.Verdict);
            Assert.Equal(1, sound.LevelIndex);
        }
    }

    [Fact]
    public void Reload_RecoversBestStreak()
    {
        var arr = NewArr();
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        var matcher = new LiveNoteMatcher();
        matcher.Load(arr, Beats(), _ => 0);

        Assert.NotNull(matcher.OfferNote(43, 1000));
        Assert.NotNull(matcher.OfferNote(43, 2000));
        Assert.Equal(2, matcher.Stats.BestStreak);

        // Reload right after a seek to the start: verdicts are all in the
        // future and get dropped, but the session-best streak survives.
        matcher.Reload(arr, Beats(), _ => 0, playheadMs: 0);
        Assert.Equal(0, matcher.Stats.Hits);
        Assert.Equal(2, matcher.Stats.BestStreak);
    }
}
