using JuceSharp.Dsp;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Detection v8 bridge: turns the quarter-tone CQT + Binary-Match-Mask
///     verifier (<see cref="JuceConstantQ"/> / <see cref="JuceChartVerifier"/>)
///     into a per-<see cref="ExpectedSound"/> verdict source for
///     <see cref="LiveNoteMatcher.ChartVerify"/>.
///     <para>
///     Pure and audio-free: the caller pushes capture PCM on its RAW capture-stream
///     axis and calls <see cref="Verify"/> as each sound's hit window closes. The
///     chart time is mapped into the capture axis here, via <see cref="SetAnchor"/>.
///     Single notes are one Binary-Match-Mask call; chords verify each distinct tone
///     and credit at the "half" coverage threshold, mirroring how
///     <see cref="LiveNoteMatcher"/> scores a strum.
///     </para>
///     <para>
///     <b>Do not go back to pushing "transport-aligned" PCM.</b> The CQT's position
///     axis is the sample axis of the audio you push into it, so scaling the position
///     by playback speed reset the ring on every push and left it permanently empty —
///     which judged every pitched note a Miss at any speed ≠ 1.0. See
///     <see cref="LiveTimeMapping.ToCaptureSeconds"/> for the full reasoning.
///     </para>
/// </summary>
public sealed class LiveCqtVerifier
{
    private readonly JuceConstantQ _cqt;
    private readonly JuceChartVerifier _verifier;

    // Chord tones: the same verifier with a later after-edge, because a strum is spread
    // (LiveCqtConfig.ChordPostWindowAfterSeconds).
    private readonly JuceChartVerifier _chordVerifier;

    // The EARLY check's pair: the same, plus a rise over the pre-onset level
    // (LiveCqtConfig.EarlyCreditRiseRatio). See VerifyEarly.
    private readonly JuceChartVerifier _earlyVerifier;
    private readonly JuceChartVerifier _earlyChordVerifier;

    /// <summary>Fraction of a chord's WEIGHTED tone mass that must confirm to credit it
    /// (see <see cref="ChordQuality"/>). Not a plain ⌈n/2⌉ count any more: the tones are
    /// weighted by harmonic function, so a fifth buys less than a third.
    /// 0.5 keeps roughly the old leniency for chords whose quality is unambiguous.</summary>
    public double ChordCoverage { get; init; } = 0.5;

    /// <summary>When a chord has exactly one third, require the player to have actually
    /// sounded it. This is what stops a minor chord scoring a hit against a charted major
    /// (and vice versa) — see <see cref="ChordQuality"/>. Power chords have no third and
    /// are unaffected.</summary>
    public bool RequireQualityTone { get; init; } = true;

    /// <summary>Live signal-to-noise margin: an expected pitch must exceed the
    /// frame's broadband background by this factor. Stops idle input noise/hum
    /// from phantom-confirming notes (the relative floor alone can't, since when
    /// nothing is played the "loud" reference IS the noise).</summary>
    /// <remarks>Every <c>Live*</c> constant on this type is an alias of
    /// <see cref="LiveCqtConfig"/>, which is the single source of truth the offline chart
    /// lint and the <c>CqtStreamingEvalTests</c> harness read too. Change the value THERE.</remarks>
    public const float LiveNoiseMargin = LiveCqtConfig.StreamingNoiseMargin;

    /// <summary>Wraps a CQT (streaming for live, or an <c>AnalyzeWhole</c>'d
    /// instance for offline tests / the regression harness).</summary>
    /// <summary>
    ///     How much louder-or-equal the bin an OCTAVE BELOW an expected note must be before we
    ///     refuse to confirm it (0 = off). This is what stops the player scoring a hit by
    ///     playing the charted note an octave DOWN — see <see cref="JuceChartVerifier.SubOctaveVeto"/>.
    /// </summary>
    /// <remarks>
    ///     Measured on IDMT (streaming, cap=120, 893 notes): the octave-below decoy confirmed
    ///     <b>89.7%</b> of the time — even when that octave was annotated SILENT — because the
    ///     played note's 2nd harmonic lands exactly on the charted bin, loud, and IS a real
    ///     local peak. At 0.5 the veto cuts that to <b>35.3%</b>, and also takes tritone FA
    ///     6.6% → 6.0% and fifth FA 12.9% → 11.6%, at no wrong-fret cost.
    ///     <para>
    ///     It is applied to SINGLE-note expectations only. Guitar chords are full of real
    ///     octaves (power chords, barre chords), so a chord tone whose octave-below is
    ///     genuinely fretted must NOT be vetoed away.
    ///     </para>
    /// </remarks>
    public const double LiveSubOctaveVeto = LiveCqtConfig.SubOctaveVeto;

    /// <summary>
    ///     The FORGIVING detection mode (the Lenient setting): a note a little out of tune counts
    ///     — the quarter-tone bin either side, when it is itself the local peak — and a chord no
    ///     longer needs its lone third. Measured on the Guitar-TECHS songs: +1.4 points of
    ///     correctly played notes for +2.7 points of semitone-off notes accepted.
    /// </summary>
    public bool Forgiving { get; init; }

    public LiveCqtVerifier(JuceConstantQ cqt, double chordCoverage = 0.5, float noiseMargin = LiveNoiseMargin)
    {
        _cqt = cqt ?? throw new ArgumentNullException(nameof(cqt));
        _verifier = new JuceChartVerifier(cqt)
        {
            NoiseMargin = noiseMargin,
            SubOctaveVeto = LiveSubOctaveVeto,
        };
        _chordVerifier = new JuceChartVerifier(cqt)
        {
            NoiseMargin = noiseMargin,
            SubOctaveVeto = LiveSubOctaveVeto,
            PostWindow = (_verifier.PostWindow.Before, LiveCqtConfig.ChordPostWindowAfterSeconds),
        };
        _earlyVerifier = new JuceChartVerifier(cqt)
        {
            NoiseMargin = noiseMargin,
            SubOctaveVeto = LiveSubOctaveVeto,
            PreRatio = LiveCqtConfig.EarlyCreditRiseRatio,
        };
        _earlyChordVerifier = new JuceChartVerifier(cqt)
        {
            NoiseMargin = noiseMargin,
            SubOctaveVeto = LiveSubOctaveVeto,
            PostWindow = _chordVerifier.PostWindow,
            PreRatio = LiveCqtConfig.EarlyCreditRiseRatio,
        };
        ChordCoverage = chordCoverage;
    }

    /// <summary>Streaming CQT kernel cap: 8192 (~170 ms at 48 kHz), so a low
    /// note beside a ringing open string resolves; see <see cref="LiveCqtConfig.MaxKernelSamples"/>
    /// for the measurement and the cost.</summary>
    public const int LiveMaxKernelSamples = LiveCqtConfig.MaxKernelSamples;

    /// <summary>
    ///     Lowest note the verifier can see: <b>MIDI 21 (A0)</b> — a 5-string bass's low B
    ///     and its drop-A, and every bass tuned down to B standard.
    /// </summary>
    /// <remarks>
    ///     <c>JuceConstantQ</c> defaults to MIDI 40 (E2, a standard-tuned guitar's low E),
    ///     and <c>BinForMidi</c> returns a NEGATIVE bin below that, which
    ///     <c>JuceChartVerifier.Verify</c> answers with a hard <c>false</c> before it ever
    ///     looks at the audio. So EVERY note below E2 was an automatic Miss no matter how
    ///     perfectly it was played: Eb standard (39), Drop D (38), 7-string low B (35),
    ///     bass (28) — and the drop-tune pedal, whose whole purpose is to put the player
    ///     below E2.
    ///     <para>
    ///     A floor of E1 (28) would leave the 5-string low B and a
    ///     B-standard bass's whole bottom string an automatic Miss (12.4 % on the chart eval
    ///     for a perfectly played B-standard bass part).
    ///     </para>
    /// </remarks>
    public const int LiveMinMidi = LiveCqtConfig.MinMidi;

    /// <summary>Bins from <see cref="LiveMinMidi"/> up: 6 octaves at quarter-tone
    /// resolution (A0..E7), which reaches past the 24th fret of the high E.</summary>
    public const int LiveBinCount = LiveCqtConfig.BinCount;

    /// <summary>CQT hop (samples). Mirrors <c>JuceConstantQ</c>'s default; named here
    /// because <see cref="LiveNoiseHistorySeconds"/> has to be converted into frames.</summary>
    public const int LiveHop = LiveCqtConfig.Hop;

    /// <summary>
    ///     Horizon of the streaming broadband noise-floor estimate: <b>10 seconds</b>.
    /// </summary>
    /// <remarks>
    ///     The estimate is a low (5th) percentile of per-frame energy — "how loud is it when
    ///     nothing is being played". It is NOT taken over the CQT's 384-frame ring, i.e.
    ///     the last <b>2.23 s</b>, and that is simply too short: during continuous playing
    ///     those 2.23 s contain no idle moment, so the 5th percentile lands on "the quietest
    ///     moment while playing" instead of on the noise floor. The presence gate then demands
    ///     <c>peak &gt; 3 × near-playing-level</c>, which real notes FAIL — a sustained legato
    ///     line or a compressed high-gain tone worst of all.
    ///     <para>
    ///     Measured on IDMT (cap=120, 893 notes): widening the horizon to 10 s took the
    ///     STREAMING path from 89.5% → 91.9% per-note and 88.0% → 90.9% per-event, which
    ///     exactly closes the gap to the offline (<c>AnalyzeWhole</c>) path — at no
    ///     false-accept cost (wrong-fret 3.2% → 3.4%, tritone 6.6% → 6.7%). Idle
    ///     phantom-confirms stay at 0%.
    ///     </para>
    /// </remarks>
    public const double LiveNoiseHistorySeconds = LiveCqtConfig.NoiseHistorySeconds;

    /// <summary>Builds a live-play verifier over a fresh streaming CQT.</summary>
    public static LiveCqtVerifier CreateStreaming(int sampleRate = 44100, double chordCoverage = 0.5, bool forgiving = false)
        => new(new JuceConstantQ(sampleRate,
                                 hop: LiveHop,
                                 fMinMidi: LiveMinMidi,
                                 binCount: LiveBinCount,
                                 maxKernelSamples: LiveMaxKernelSamples,
                                 noiseHistoryFrames: LiveCqtConfig.NoiseHistoryFrames(sampleRate),
                                 subOctaveBins: LiveCqtConfig.SubOctaveBins,
                                 idleSteadyFrames: LiveCqtConfig.IdleSteadyFrames(sampleRate),
                                 idleSteadyRatio: LiveCqtConfig.IdleSteadyRatio,
                                 idleFloorRiseDbPerSecond: LiveCqtConfig.IdleFloorRiseDbPerSecond),
               chordCoverage)
        { Forgiving = forgiving, RequireQualityTone = !forgiving };

    /// <summary>The live verifier for a hit-window preset: <see cref="LiveHitWindowPreset.Lenient" />
    /// is also the FORGIVING detection mode (<see cref="Forgiving" />); Normal and Strict are not.</summary>
    public static LiveCqtVerifier ForPreset(int sampleRate, LiveHitWindowPreset preset)
        => CreateStreaming(sampleRate, forgiving: preset == LiveHitWindowPreset.Lenient);

    /// <summary>Re-anchor the streaming CQT to a capture-stream position (seek/skip).</summary>
    public void Reset(long streamPosition) => _cqt.Reset(streamPosition);

    /// <summary>Feed mono capture samples. <paramref name="streamPosition"/> is the RAW
    /// capture-stream index of the first sample — pass it straight through, unscaled.
    /// Successive pushes must be contiguous (see <see cref="JuceConstantQ.Push"/>); a
    /// genuine gap (ring overrun) re-anchors, which is exactly what it is for.</summary>
    public void Push(ReadOnlySpan<float> samples, long streamPosition) => _cqt.Push(samples, streamPosition);

    /// <summary>
    ///     Pair a capture-stream sample with the transport time the player was hearing
    ///     when it was captured, so <see cref="Verify"/> can find a chart time in the
    ///     capture axis. Refresh it every tick: then there is no projection to drift, and
    ///     seek / pause / speed-change handling falls out for free.
    /// </summary>
    public void SetAnchor(long streamPos, double transportMs, double speed)
    {
        _anchorStreamPos = streamPos;
        _anchorTransportMs = transportMs;
        _speed = speed;
    }

    // Defaults to the IDENTITY mapping: capture-stream sample 0 == transport 0 ms at
    // speed 1. That is exactly what an offline (AnalyzeWhole) CQT means — chart time is
    // capture time — so the offline/regression path needs no anchor at all. A live
    // session overwrites it every tick.
    private long _anchorStreamPos;
    private double _anchorTransportMs;
    private double _speed = 1.0;

    /// <summary>Verify one expected sound against the CQT at its chart time — the FINAL check,
    /// asked once, when the sound's hit window closes.</summary>
    public ChartVerifyOutcome Verify(ExpectedSound sound) => VerifyWith(sound, _verifier, _chordVerifier);

    /// <summary>
    ///     The EARLY check, asked on every tick while a due sound is still open so a hit is
    ///     credited as it is played. Stricter than <see cref="Verify" />: the
    ///     charted pitch must have RISEN over its pre-onset level
    ///     (<see cref="LiveCqtConfig.EarlyCreditRiseRatio" />), because in the first ticks after a
    ///     note is due its bin can still hold the previous note's ring, and several chances at a
    ///     presence-only check accept a wrong note far more often than one (17.9 → 27.1 % of
    ///     semitone-off notes). A sound this does not confirm waits for the final check.
    /// </summary>
    public ChartVerifyOutcome VerifyEarly(ExpectedSound sound) => VerifyWith(sound, _earlyVerifier, _earlyChordVerifier);

    private ChartVerifyOutcome VerifyWith(ExpectedSound sound, JuceChartVerifier single, JuceChartVerifier chord)
    {
        var notes = sound.MidiNotes;
        if (notes.Length == 0) return new ChartVerifyOutcome(false, 0f);

        var t = LiveTimeMapping.ToCaptureSeconds(
            sound.TimeMs, _anchorStreamPos, _anchorTransportMs, _cqt.SampleRate, _speed);

        // Palm muting strips the upper partials, so a rule that leans on a harmonic must
        // not fire on a muted note (see JuceChartVerifier.HarmonicPeakBelowHz).
        var allowHarmonic = !sound.PalmMute && !sound.FullMute;

        var harmonic = sound.Harmonic || sound.PinchHarmonic;

        if (notes.Length == 1)
        {
            // Single note ⇒ the sub-octave veto is safe: a played string has no subharmonic,
            // so energy an octave below means the player is playing THAT note, not this one —
            // UNLESS that note was just played and credited, in which case it is still ringing.
            var veto = !OctaveBelowIsStillRinging(notes[0], sound.TimeMs);
            var r = single.Verify(notes[0], t, allowHarmonic, allowSubOctaveVeto: veto);
            if (!r.Hit && harmonic && TryOvertone(single, notes[0], t, out var overtone)) r = overtone;
            // A bend or a slide sounds its charted pitch only for a moment: every semitone it
            // travels through counts, the range the attack path has always accepted.
            if (!r.Hit && TryTravel(single, sound, t, allowHarmonic, veto, out var travelled)) r = travelled;
            if (!r.Hit && Forgiving && IsDetuned(single, notes[0], t, allowHarmonic, veto)) r = new JuceVerifyResult(true, 0.5f, 0f);
            if (r.Hit) Remember(notes[0], sound.TimeMs);
            return new ChartVerifyOutcome(r.Hit, r.Confidence);
        }

        // Chord: distinct tones, credited by INTERVAL-WEIGHTED coverage (see
        // ChordQuality). The dedupe set is reused per call (chords carry ≤6 notes;
        // Verify runs per closing chord window on the 30 Hz tick path).
        var distinct = _chordTones;
        distinct.Clear();
        for (var i = 0; i < notes.Length; i++) distinct.Add(notes[i]);

        var confirmed = _chordConfirmed;
        confirmed.Clear();
        // NO sub-octave veto inside a chord: guitar chords contain real octaves (power
        // chords, barre chords), and the veto would reject the tone whose octave-below is
        // genuinely fretted right next to it.
        foreach (var m in distinct)
        {
            if (chord.Verify(m, t, allowHarmonic, allowSubOctaveVeto: false).Hit ||
                (harmonic && TryOvertone(chord, m, t, out _)) ||
                (Forgiving && IsDetuned(chord, m, t, allowHarmonic, false)))
            {
                confirmed.Add(m);
            }
        }

        // A dense chord (4+ distinct tones) hides its middle strings: a lower bar, and no
        // lone-third rule (LiveCqtConfig.DenseChordTones).
        var dense = distinct.Count >= LiveCqtConfig.DenseChordTones;
        var outcome = ChordQuality.Judge(distinct, confirmed,
            dense ? Math.Min(ChordCoverage, LiveCqtConfig.DenseChordCoverage) : ChordCoverage,
            RequireQualityTone && !dense);
        if (outcome.Confirmed)
            foreach (var m in confirmed) Remember(m, sound.TimeMs);
        return outcome;
    }

    /// <summary>
    ///     How long a credited note is assumed to still ring into the notes after it: 3 s,
    ///     a guitar string's audible decay.
    /// </summary>
    public const double RingOnMs = 3000;

    // Chart time each MIDI pitch was last credited; -inf = never. Indexed by MIDI (0..127),
    // so remembering is allocation-free on the tick path.
    private readonly double[] _lastCreditedMs = CreateNeverCredited();

    private static double[] CreateNeverCredited()
    {
        var a = new double[128];
        Array.Fill(a, double.NegativeInfinity);
        return a;
    }

    private void Remember(int midi, double chartMs)
    {
        if ((uint)midi < (uint)_lastCreditedMs.Length) _lastCreditedMs[midi] = chartMs;
    }

    /// <summary>
    ///     The sub-octave veto cannot tell "the player is playing this note an octave DOWN"
    ///     from "the note an octave down was the LAST note, and it is still ringing" — both put
    ///     a loud fundamental an octave below. Measured on real DI with ground truth
    ///     (Guitar-TECHS, <c>tools/detection-eval</c>): a correctly played note whose lower octave
    ///     had been played in the previous 1.5 s was refused <b>72 %</b> of the time (57 of 79),
    ///     against 3.3 % for every other note — every octave leap in a riff. When that lower
    ///     note was charted AND credited within <see cref="RingOnMs" /> before this one, its
    ///     ring explains the energy, so the veto stands aside. An octave-down performance with
    ///     no such note before it is still refused. A jump backwards in chart time (a loop,
    ///     a seek) never counts: the remembered note must lie BEFORE this one.
    /// </summary>
    private bool OctaveBelowIsStillRinging(int midi, double chartMs)
    {
        var below = midi - 12;
        if ((uint)below >= (uint)_lastCreditedMs.Length) return false;
        var dt = chartMs - _lastCreditedMs[below];
        return dt is > 0 and <= RingOnMs;
    }

    /// <summary>
    ///     A charted harmonic carries its FRETTED pitch, but it rings at an overtone of it:
    ///     a natural harmonic over fret 7 sounds an octave above the fretted note, over fret
    ///     5 an octave and a fifth, over fret 4 two octaves; a pinch harmonic rings wherever
    ///     the pick caught the node. So the fretted pitch's bin is often empty and the note
    ///     read as a Miss however well it was played. The classic path has always accepted
    ///     these overtones (<see cref="LivePitchAcceptance.HarmonicOvertoneSemis"/>); this gives
    ///     the CQT path the same tolerance.
    ///     <para>No sub-octave veto on an overtone: the bin an octave below it is the fretted
    ///     note, which a pinch harmonic legitimately still sounds.</para>
    /// </summary>
    /// <summary>Forgiving mode: the note is played a little out of tune — the quarter-tone bin
    /// either side confirms AND is a strict local maximum against its immediate quarter-tone
    /// neighbours at its loudest frame. A note a whole semitone off peaks on its own bin, which
    /// leaves the quarter-tone bin on its slope, so it is not accepted this way.</summary>
    private bool IsDetuned(JuceChartVerifier verifier, int midi, double t, bool allowHarmonic, bool veto)
    {
        foreach (var q in stackalloc double[] { midi - 0.5, midi + 0.5 })
        {
            if (!verifier.Verify(q, t, allowHarmonic, veto).Hit) continue;
            // Mains hum sits between the semitone bins (100 Hz is G2 + 35 cents), so a quarter-
            // tone bin is where an idle input's own peaks are: demand twice the presence margin.
            if (!Present(verifier, t, 2f * verifier.NoiseMargin)) continue;
            var b = _cqt.BinForMidi(q);
            long f0 = _cqt.FrameOfSeconds(t - verifier.PostWindow.Before), f1 = _cqt.FrameOfSeconds(t + verifier.PostWindow.After);
            long fi = -1;
            var best = 0f;
            for (var f = f0; f <= f1; f++)
            {
                var m = _cqt.Magnitude(b, f);
                if (m > best || fi < 0) { best = m; fi = f; }
            }
            if (fi >= 0 && best > _cqt.Magnitude(b - 1, fi) && best > _cqt.Magnitude(b + 1, fi)) return true;
        }
        return false;
    }

    private bool Present(JuceChartVerifier verifier, double t, float margin)
    {
        var presence = 0f;
        for (var f = _cqt.FrameOfSeconds(t - verifier.PostWindow.Before); f <= _cqt.FrameOfSeconds(t + verifier.PostWindow.After); f++)
            presence = MathF.Max(presence, _cqt.FrameEnergy(f));
        return presence > margin * _cqt.NoiseFloorEnergy();
    }

    /// <summary>
    ///     A charted BEND or SLIDE: the string sits on its charted pitch only for the moment
    ///     before it moves, so a check at that one pitch missed most of them (bends 78 % on the
    ///     rendered charts, bass slides 80 %). Try every semitone the note travels through —
    ///     up to the bend's peak, or to the slide's target — the same range
    ///     <see cref="LiveNoteMatcher" />'s attack path has always accepted for these notes.
    /// </summary>
    private static bool TryTravel(JuceChartVerifier verifier, ExpectedSound sound, double t,
        bool allowHarmonic, bool veto, out JuceVerifyResult result)
    {
        var m = sound.MidiNotes[0];
        var lo = m;
        var hi = m + Math.Max(0, sound.MaxBendSemis);
        if (sound.SlideToMidi is { } to)
        {
            lo = Math.Min(lo, to);
            hi = Math.Max(hi, to);
        }

        for (var p = lo; p <= hi; p++)
        {
            if (p == m) continue;
            var r = verifier.Verify(p, t, allowHarmonic, veto);
            if (r.Hit)
            {
                result = r;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool TryOvertone(JuceChartVerifier verifier, int frettedMidi, double t, out JuceVerifyResult result)
    {
        foreach (var semis in LivePitchAcceptance.HarmonicOvertoneSemis)
        {
            var r = verifier.Verify(frettedMidi + semis, t, allowHarmonicPeak: false, allowSubOctaveVeto: false);
            if (r.Hit)
            {
                result = r;
                return true;
            }
        }

        result = default;
        return false;
    }

    private readonly HashSet<int> _chordTones = [];
    private readonly HashSet<int> _chordConfirmed = [];

    // One CQT ring's worth of contour, reused: a longer window has left the ring anyway.
    private readonly float[] _contour = new float[LiveCqtConfig.RingFrames];

    /// <summary>
    ///     The contour judge for a credited single note's bend, slide or vibrato
    ///     (<see cref="LiveNoteMatcher.JudgeContour" />): its pitch, hop by hop from its chart time to
    ///     <see cref="TechniqueContourExpectation.EvidenceAfterMs" />, read off the same rolling CQT
    ///     that confirmed it (<see cref="ReadContour" />), handed to <see cref="TechniqueContourJudge" />.
    ///     <b>Null while the window's end has not reached the CQT yet</b> — the matcher asks again.
    ///     A window that left the ring, or a sound with nothing to judge, is Unknown.
    /// </summary>
    public TechniqueContourVerdict? JudgeTechnique(ExpectedSound sound)
    {
        var expect = sound.TechniqueContour;
        if (expect.IsEmpty || sound.MidiNotes.Length != 1) return TechniqueContourVerdict.Unknown;

        var evidenceMs = expect.EvidenceAfterMs;
        var f0 = _cqt.FrameOfSeconds(LiveTimeMapping.ToCaptureSeconds(
            sound.TimeMs, _anchorStreamPos, _anchorTransportMs, _cqt.SampleRate, _speed));
        var f1 = _cqt.FrameOfSeconds(LiveTimeMapping.ToCaptureSeconds(
            sound.TimeMs + evidenceMs, _anchorStreamPos, _anchorTransportMs, _cqt.SampleRate, _speed));
        if (f1 > _cqt.NewestFrame) return null;

        var hops = f1 - f0 + 1;
        if (hops < 2 || hops > _contour.Length || f0 < _cqt.OldestFrame) return TechniqueContourVerdict.Unknown;

        var cents = _contour.AsSpan(0, (int)hops);
        ReadContour(sound, f0, cents);
        // Chart milliseconds per hop: the expectation's times are chart times.
        return TechniqueContourJudge.Judge(cents, evidenceMs / (hops - 1), expect);
    }

    /// <summary>
    ///     The played note's pitch per CQT hop from <paramref name="firstFrame" />, in cents from
    ///     its charted (unbent) pitch: the loudest quarter-tone bin in the band the note may
    ///     legitimately travel (a semitone either side of the slide's range and the bend's peak),
    ///     refined by a parabola through its neighbours. <see cref="float.NaN" /> where the hop
    ///     has no pitch there: the peak is not a local maximum, does not clear its bin's floor,
    ///     or the frame's energy does not clear the presence gate — the same two gates
    ///     <see cref="JuceChartVerifier.Verify" /> confirms with.
    /// </summary>
    internal void ReadContour(ExpectedSound sound, long firstFrame, Span<float> cents)
    {
        var midi = sound.MidiNotes[0];
        var expect = sound.TechniqueContour;
        var lo = midi - 1 + Math.Min(0, expect.SlideSemis);
        var hi = midi + 1 + Math.Max((int)Math.Ceiling(expect.BendSemis), expect.SlideSemis);
        var bLo = Math.Max(1, _cqt.BinForMidi(lo));
        var bHi = Math.Min(_cqt.BinCount - 2, _cqt.BinForMidi(hi));
        var presence = _verifier.NoiseMargin * _cqt.NoiseFloorEnergy();

        for (var i = 0; i < cents.Length; i++)
        {
            var f = firstFrame + i;
            cents[i] = float.NaN;
            if (bHi < bLo || _cqt.FrameEnergy(f) <= presence) continue;

            var best = bLo;
            var level = _cqt.Magnitude(bLo, f);
            for (var b = bLo + 1; b <= bHi; b++)
            {
                var m = _cqt.Magnitude(b, f);
                if (m > level)
                {
                    level = m;
                    best = b;
                }
            }

            var below = _cqt.Magnitude(best - 1, f);
            var above = _cqt.Magnitude(best + 1, f);
            if (level <= 0 || level < below || level < above || level <= _cqt.Floor(best)) continue;

            // Parabolic interpolation in the log domain (a window's main lobe is near-Gaussian).
            double a = MathF.Log(MathF.Max(below, 1e-12f)), b0 = MathF.Log(level), c = MathF.Log(MathF.Max(above, 1e-12f));
            var curve = a - 2 * b0 + c;
            var offset = curve < 0 ? Math.Clamp(0.5 * (a - c) / curve, -0.5, 0.5) : 0;
            var bin = best + offset;
            cents[i] = (float)((bin / _cqt.Step + _cqt.FMinMidi - midi) * 100);
        }
    }
}

/// <summary>
///     How a chord is credited from the tones that actually confirmed.
///     <para>
///     <b>Why this is not a flat ⌈n/2⌉ count.</b> Uniform coverage cannot tell a major
///     chord from a minor one. A charted C major is C–E–G; with ⌈3/2⌉ = 2 tones needed,
///     <b>C + G alone passes</b> — root and fifth — and the third, the ONLY note that
///     carries the chord's quality, is never required. So a player who fingers C minor
///     scores a clean HIT against a charted C major.
///     </para>
///     <para>
///     That is not a hypothetical: it is the defect Ubisoft says they shipped in
///     Rocksmith 2014 and fixed afterwards. Their lead audio designer, on RS+:
///     <i>"if we ask you to play a major chord and you play it minor; we know. This was
///     something we struggled with in 2014… a six-string major bar chord has three root
///     notes, two fifths, and only one third, but it is that single third that really
///     defines the quality of the chord."</i> We had independently reproduced it.
///     </para>
///     <para>
///     So tones are weighted by harmonic function rather than counted, and a chord that
///     has exactly one quality-defining tone must actually sound it. Power chords — which
///     guitar charts are full of — have NO third, so the rule simply does not apply to
///     them and they behave exactly as before.
///     </para>
/// </summary>
public static class ChordQuality
{
    /// <summary>Weight of a tone by its interval above the chord's lowest note.
    /// Root and third carry the chord; the fifth is nearly redundant (it is implied by
    /// the root's own harmonic series, which is also why it is the tone most likely to
    /// confirm from bleed alone).</summary>
    public static double WeightOf(int semitonesAboveRoot) => (semitonesAboveRoot % 12) switch
    {
        0 => 1.0,   // root
        3 or 4 => 1.0,   // minor / major third — the quality
        6 => 0.8,   // tritone (dim5 / sharp 11) — also quality-defining
        10
        or 11 => 0.8,   // seventh — "taken seriously"
        7 => 0.3,   // fifth — nearly free; implied by the root's partials
        _ => 0.6,   // sus2/4, 6th, 9th, …
    };

    /// <summary>True for a tone that defines the chord's quality (a third).</summary>
    public static bool IsQualityTone(int semitonesAboveRoot) =>
        (semitonesAboveRoot % 12) is 3 or 4;

    /// <summary>
    ///     Judge a chord from the tones that confirmed.
    /// </summary>
    /// <param name="expected">The chord's expected tones as MIDI note numbers; the
    /// lowest is treated as the root.</param>
    /// <param name="confirmed">The tones that were confirmed as sounded (matched
    /// against <paramref name="expected" /> by exact note number).</param>
    /// <param name="coverage">Minimum weighted fraction of expected tones (0–1) that
    /// must have sounded for the chord to pass.</param>
    /// <param name="requireQualityTone">
    ///     When the chord has EXACTLY ONE third, require it to have sounded. A chord with
    ///     no third (a power chord) or with several is unaffected — there is nothing
    ///     unambiguous to insist on.
    /// </param>
    public static ChartVerifyOutcome Judge(
        IReadOnlyCollection<int> expected,
        IReadOnlyCollection<int> confirmed,
        double coverage,
        bool requireQualityTone)
    {
        if (expected.Count == 0) return new ChartVerifyOutcome(false, 0f);

        var root = int.MaxValue;
        foreach (var m in expected) if (m < root) root = m;

        double total = 0, got = 0;
        var qualityTones = 0;
        var qualityConfirmed = 0;

        foreach (var m in expected)
        {
            var interval = m - root;
            var w = WeightOf(interval);
            total += w;

            var hit = confirmed.Contains(m);
            if (hit) got += w;

            if (IsQualityTone(interval))
            {
                qualityTones++;
                if (hit) qualityConfirmed++;
            }
        }

        if (total <= 0) return new ChartVerifyOutcome(false, 0f);

        // A lone third is what separates major from minor. If the chart says the chord
        // has one and the player never sounded it, they did not play THIS chord —
        // whatever else they hit. (Several thirds = ambiguous, no single tone to demand.)
        if (requireQualityTone && qualityTones == 1 && qualityConfirmed == 0)
            return new ChartVerifyOutcome(false, (float)(got / total));

        var ratio = got / total;
        return new ChartVerifyOutcome(ratio >= coverage, (float)ratio);
    }
}
