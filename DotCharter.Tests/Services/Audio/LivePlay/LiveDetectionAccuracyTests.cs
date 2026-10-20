using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The Live Play CQT pipeline, end to end on audio: <see cref="LiveCqtVerifier.CreateStreaming" />
///     pushed in 1,600-sample ticks exactly as <c>LivePlayViewModel.TickCore</c> does, judged
///     by <see cref="LivePlaySessionService" /> with the app's session options. Each case pins
///     one gap the 2026-09-29 detection eval measured on real DI and on rendered charts (the
///     harness and its numbers are in <c>docs/agents/services/live-play.md</c>):
///     <list type="bullet">
///         <item><description>a riff that never pauses scored ~61 % because the presence gate's
///         noise floor rose to the playing level;</description></item>
///         <item><description>a B-standard bass's low string was below the CQT's range (12.4 %);</description></item>
///         <item><description>a charted harmonic was looked for at its FRETTED pitch, where a
///         natural harmonic has no energy at all.</description></item>
///     </list>
///     And the other side of the same gate: an idle input must score nothing.
/// </summary>
public sealed class LiveDetectionAccuracyTests
{
    private const int Sr = 48000;
    private const int TickSamples = 1600;
    private const double MsPerBeat = 500;

    private static List<BeatMarker> Beats(double lastMs) =>
        [.. Enumerable.Range(0, (int)(lastMs / MsPerBeat) + 16).Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = 120 })];

    private static FractionalPosition At(double ms)
    {
        var beat = (int)(ms / MsPerBeat);
        return new FractionalPosition(beat, new Fraction((long)Math.Round(ms - beat * MsPerBeat), (long)MsPerBeat));
    }

    // Pickup hiss plus mains hum — an idle guitar input.
    private static float[] Idle(int samples, int seed)
    {
        var rng = new Random(seed);
        var buf = new float[samples];
        double lp = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var t = i / (double)Sr;
            lp += 0.5 * ((rng.NextDouble() * 2 - 1) - lp);
            var hum = 0.0;
            for (var h = 1; h <= 6; h++) hum += Math.Sin(2 * Math.PI * 50 * h * t + h) / h;
            buf[i] = (float)(0.0008 * lp + 0.0012 * hum);
        }
        return buf;
    }

    // A plucked string's partials with a fast attack; it rings for durSec.
    private static void Pluck(float[] buf, double atMs, double midi, double durSec, double decay = 1.5)
    {
        var f0 = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        var start = (int)(atMs / 1000 * Sr);
        var len = (int)(durSec * Sr);
        for (var i = 0; i < len && start + i < buf.Length; i++)
        {
            var t = i / (double)Sr;
            double s = 0;
            for (var h = 1; h <= 6 && f0 * h < Sr / 2.0; h++) s += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            buf[start + i] += (float)(0.2 * s * (1 - Math.Exp(-t * 400)) * Math.Exp(-t * decay));
        }
    }

    private static (int Hits, int Total) Judge(Arrangement arrangement, ChartLevel level, float[] audio, double lastMs,
        LiveHitWindowPreset preset = LiveHitWindowPreset.Normal)
    {
        var cqt = LiveCqtVerifier.ForPreset(Sr, preset);                // as LivePlayViewModel builds it
        var session = new LivePlaySessionService();
        session.Start(arrangement, level, Beats(lastMs), new LivePlaySessionOptions
        {
            HitWindow = preset,
            ChartVerify = cqt.Verify,
            ChartVerifyEarly = cqt.VerifyEarly,                    // as LivePlayViewModel wires it
            CountInBeats = 0,
        });
        session.SyncTo(0);

        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            cqt.Push(audio.AsSpan(off, n), off);
            cqt.SetAnchor(off, off * 1000.0 / Sr, 1.0);
            session.HandleTick((off + n) * 1000.0 / Sr, isPlaying: true);
        }

        var stats = session.Stats!;
        return (stats.Hits, stats.TotalSounds);
    }

    // Standard tuning, string 0 = low E (MIDI 40).
    private static readonly int[] Open = [40, 45, 50, 55, 59, 64];

    /// <summary>18 s of eighth notes at 200 BPM, legato: each note rings until the next is
    /// picked, so the input is never quieter than the line for longer than the 10 s horizon the
    /// old noise floor was the 5th percentile of.</summary>
    [Fact]
    public void ARiffThatNeverPauses_IsScoredAsPlayed()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        var rng = new Random(7);
        var audio = Idle(21 * Sr, 1);
        var lastMs = 0.0;
        for (var ms = 1500.0; ms < 19500; ms += 150)
        {
            var str = rng.Next(0, 4);
            var fret = rng.Next(0, 8);
            level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = str, Fret = fret });
            Pluck(audio, ms, Open[str] + fret, 0.15);
            lastMs = ms;
        }

        var (hits, total) = Judge(arrangement, level, audio, lastMs);

        Assert.True(total > 100, $"only {total} sounds were judged");
        Assert.True(hits >= total * 0.95, $"a perfectly played non-stop riff scored {hits}/{total}");
    }

    /// <summary>Nothing played: hiss and hum under a full chart. The presence gate is the only
    /// thing that tells this apart from playing (measured without it: 37.5 % "hits").</summary>
    [Fact]
    public void AnIdleInput_ScoresNothing()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var rng = new Random(8);
        var lastMs = 0.0;
        for (var ms = 1500.0; ms < 19500; ms += 300)
        {
            level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = rng.Next(0, 6), Fret = rng.Next(0, 12) });
            lastMs = ms;
        }

        var (hits, total) = Judge(arrangement, level, Idle(21 * Sr, 2), lastMs);

        Assert.True(total > 50);
        Assert.Equal(0, hits);
    }

    /// <summary>A bass tuned to B standard: its open low string is B0, MIDI 23 — below the
    /// E1 (28) the verifier's range started at, so every note on it was a Miss unheard.</summary>
    [Fact]
    public void ABStandardBass_LowString_IsHeard()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = 4 };
        for (var s = 0; s < 4; s++) arrangement.TuningPitches.Add(new TuningOffset(-5));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        var audio = Idle(14 * Sr, 3);
        var lastMs = 0.0;
        var fret = 0;
        for (var ms = 1500.0; ms < 12000; ms += 750, fret = (fret + 1) % 5)
        {
            level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 0, Fret = fret });
            var midi = LivePitchAcceptance.SoundingMidi(arrangement, 0, fret)!.Value;
            Assert.InRange(midi, 23, 27);                    // B0..D#1 — all below E1
            Pluck(audio, ms, midi, 0.7, decay: 0.8);
            lastMs = ms;
        }

        var (hits, total) = Judge(arrangement, level, audio, lastMs);

        Assert.True(total >= 12);
        Assert.True(hits >= total * 0.9, $"B-standard bass low string: {hits}/{total}");
    }

    /// <summary>A slow downstroke: C major (C3 G3 C4 E4) strummed low to high, 60 ms a string,
    /// so the third — the top string — sounds 180 ms after the charted time. The single-note
    /// window (140 ms) never saw it, and a chord whose only third never sounded fails the
    /// quality rule outright; the chord window (<c>LiveCqtConfig.ChordPostWindowAfterSeconds</c>)
    /// does see it.</summary>
    [Fact]
    public void ASlowStrum_IsCreditedOnceItsTopStringSounds()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        (int Str, int Fret)[] shape = [(1, 3), (3, 0), (4, 1), (5, 0)];   // C3 G3 C4 E4
        var audio = Idle(14 * Sr, 6);
        var lastMs = 0.0;
        for (var ms = 1500.0; ms < 12000; ms += 1200)
        {
            var chord = new Chord { Position = At(ms) };
            for (var k = 0; k < shape.Length; k++)
            {
                chord.ChordNotes.Add(new ChordNote { StringIndex = shape[k].Str, Fret = shape[k].Fret });
                // 0.8 s: every string has stopped well before the next chord's window opens, so
                // no earlier E4 can stand in for this one.
                Pluck(audio, ms + k * 60, Open[shape[k].Str] + shape[k].Fret, 0.8, decay: 1.2);
            }
            level.Chords.Add(chord);
            lastMs = ms;
        }

        var (hits, total) = Judge(arrangement, level, audio, lastMs);

        Assert.True(total >= 8);
        Assert.True(hits >= total - 1, $"slow strums: {hits}/{total} credited");
    }

    /// <summary>An open A string left ringing under a melody on the B two frets above it —
    /// the shape of a real Guitar-TECHS song that scored 69 %. Down here every CQT bin is
    /// capped, and at a 4096-sample kernel the loud A2 smeared across the B2 beside it, so the
    /// B2 was never a peak of its own. <c>LiveCqtConfig.MaxKernelSamples</c> = 8192 resolves it.</summary>
    [Fact]
    public void ANoteBesideARingingOpenString_IsHeard()
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var audio = Idle(14 * Sr, 7);
        Pluck(audio, 500, 45, 12.5, decay: 0.08);                   // the A2 drone, re-struck never
        var lastMs = 0.0;
        for (var ms = 1500.0; ms < 12000; ms += 700)
        {
            level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 1, Fret = 2 });   // B2
            Pluck(audio, ms, 47, 0.5, decay: 2.0);
            lastMs = ms;
        }

        var (hits, total) = Judge(arrangement, level, audio, lastMs);

        Assert.True(total >= 14);
        Assert.True(hits >= total - 1, $"B2 over a ringing A2: {hits}/{total} credited");
    }

    /// <summary>A bend: the string leaves its charted pitch almost at once, so the note is heard
    /// mostly at the pitches it travels through. Charted G3 bent a whole tone; the audio is the
    /// bent A3 after a 15 ms pick. The control is the same audio charted WITHOUT the bend — the
    /// travelled range is what credits it, not a looser pitch test.</summary>
    [Fact]
    public void ABend_IsCreditedAlongTheWayItTravels()
    {
        static (int, int) Run(bool bent)
        {
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var audio = Idle(12 * Sr, 8);
            var lastMs = 0.0;
            for (var ms = 1500.0; ms < 10000; ms += 1000)
            {
                var note = new GuitarNote { Position = At(ms), StringIndex = 3, Fret = 0 };           // G3
                if (bent) note.BendValues.Add(new BendValue(At(ms + 100), 2));
                level.Notes.Add(note);
                Pluck(audio, ms, 55, 0.015);
                Pluck(audio, ms + 15, 57, 0.7, decay: 2.5);                                    // A3
                lastMs = ms;
            }
            return Judge(arrangement, level, audio, lastMs);
        }

        var (hits, total) = Run(bent: true);
        var (plainHits, _) = Run(bent: false);

        Assert.True(hits >= total - 1, $"bends: {hits}/{total} credited");
        Assert.Equal(0, plainHits);
    }

    /// <summary>A six-string E major barre with its only third (G#3) not ringing: a dense
    /// chord is credited without the lone-third rule (<c>LiveCqtConfig.DenseChordTones</c>),
    /// because a full strum hides its middle strings. The control is a TRIAD without its third
    /// — still refused, which is what tells major from minor.</summary>
    [Fact]
    public void ADenseChord_CountsWithAMissingMiddleString_ButATriadStillNeedsItsThird()
    {
        static (int, int) Run((int Str, int Fret)[] shape, int silentMidi)
        {
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var audio = Idle(12 * Sr, 9);
            var lastMs = 0.0;
            for (var ms = 1500.0; ms < 10000; ms += 1200)
            {
                var chord = new Chord { Position = At(ms) };
                foreach (var (str, fret) in shape)
                {
                    chord.ChordNotes.Add(new ChordNote { StringIndex = str, Fret = fret });
                    var midi = Open[str] + fret;
                    if (midi != silentMidi) Pluck(audio, ms, midi, 0.9, decay: 1.2);
                }
                level.Chords.Add(chord);
                lastMs = ms;
            }
            return Judge(arrangement, level, audio, lastMs);
        }

        (int, int)[] barre = [(0, 0), (1, 2), (2, 2), (3, 1), (4, 0), (5, 0)];                // E B E G# B E
        var (denseHits, denseTotal) = Run(barre, silentMidi: 56);
        (int, int)[] triad = [(1, 7), (2, 6), (3, 4)];                                         // E3 G#3 B3 — root position
        var (triadHits, triadTotal) = Run(triad, silentMidi: 56);

        Assert.True(denseHits >= denseTotal - 1, $"dense chord without its third: {denseHits}/{denseTotal}");
        Assert.True(triadTotal > 0);
        Assert.Equal(0, triadHits);
    }

    /// <summary>The Lenient setting is also a FORGIVING detection mode: a note played a quarter
    /// tone sharp (a guitar a little out of tune) counts there and not in Normal, while a note a
    /// whole semitone off counts in neither.</summary>
    [Fact]
    public void Lenient_ForgivesANoteALittleOutOfTune_ButNotAWrongOne()
    {
        static (int, int) Run(double playedOffset, LiveHitWindowPreset preset)
        {
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var audio = Idle(12 * Sr, 10);
            var lastMs = 0.0;
            for (var ms = 1500.0; ms < 10000; ms += 1000)
            {
                level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 4, Fret = 5 });   // E4
                Pluck(audio, ms, 64 + playedOffset, 0.8, decay: 2);
                lastMs = ms;
            }
            return Judge(arrangement, level, audio, lastMs, preset);
        }

        var (lenientHits, lenientTotal) = Run(0.5, LiveHitWindowPreset.Lenient);
        var (normalHits, _) = Run(0.5, LiveHitWindowPreset.Normal);
        var (wrongHits, _) = Run(1.0, LiveHitWindowPreset.Lenient);

        Assert.True(lenientHits >= lenientTotal - 1, $"quarter-tone sharp, Lenient: {lenientHits}/{lenientTotal}");
        Assert.True(normalHits < lenientHits, $"Normal forgave the detuned note as well ({normalHits})");
        Assert.Equal(0, wrongHits);
    }

    /// <summary>An octave leap: E2 picked, then E3 while the E2 still rings. The sub-octave
    /// veto used to read that ringing E2 as "the player is playing E3 an octave down" and
    /// refuse the E3 — 72 % of such notes on real DI. The control is the exploit the veto
    /// exists for: E3 charted, only E2 played, nothing before it — still a Miss.</summary>
    [Fact]
    public void AnOctaveLeap_IsHeard_ButPlayingAnOctaveDownStillIsNot()
    {
        static (int Hits, int Total) Run(bool leap)
        {
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var audio = Idle(14 * Sr, 5);
            var lastMs = 0.0;
            for (var ms = 1500.0; ms < 12000; ms += 1200)
            {
                if (leap)
                {
                    level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 0, Fret = 0 });       // E2
                    level.Notes.Add(new GuitarNote { Position = At(ms + 300), StringIndex = 2, Fret = 2 }); // E3
                    Pluck(audio, ms, 40, 1.1, decay: 1.0);
                    Pluck(audio, ms + 300, 52, 0.8, decay: 1.0);
                }
                else
                {
                    level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 2, Fret = 2 });       // E3 charted…
                    Pluck(audio, ms, 40, 1.1, decay: 1.0);                                              // …E2 played
                }
                lastMs = ms + 300;
            }

            return Judge(arrangement, level, audio, lastMs);
        }

        var (leapHits, leapTotal) = Run(leap: true);
        var (cheatHits, cheatTotal) = Run(leap: false);

        Assert.True(leapTotal >= 16);
        Assert.True(leapHits >= leapTotal - 1, $"octave leaps: {leapHits}/{leapTotal} credited");
        Assert.True(cheatTotal >= 8);
        Assert.Equal(0, cheatHits);
    }

    /// <summary>A natural harmonic over the 7th fret of the G string is charted at the fretted
    /// D4 (62) but rings an octave above it (D5, 74) — the fretted bin is silent. The same
    /// audio charted as a PLAIN fretted note is the control: it must stay a Miss, which is what
    /// proves the harmonic's overtone tolerance, and nothing looser, is what credits it.</summary>
    [Fact]
    public void ANaturalHarmonic_IsHeardAtTheOvertoneItRings()
    {
        static (int, int) Run(HarmonicType harmonic)
        {
            var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
            var level = new ChartLevel();
            arrangement.Levels.Add(level);
            var audio = Idle(12 * Sr, 4);
            var lastMs = 0.0;
            for (var ms = 1500.0; ms < 10000; ms += 1000)
            {
                level.Notes.Add(new GuitarNote { Position = At(ms), StringIndex = 3, Fret = 7, Harmonic = harmonic });
                Pluck(audio, ms, Open[3] + 7 + 12, 0.9, decay: 3);
                lastMs = ms;
            }
            return Judge(arrangement, level, audio, lastMs);
        }

        var (hits, total) = Run(HarmonicType.Normal);
        var (plainHits, plainTotal) = Run(HarmonicType.None);

        Assert.Equal(total, hits);
        Assert.True(plainTotal > 0);
        Assert.Equal(0, plainHits);
    }
}
