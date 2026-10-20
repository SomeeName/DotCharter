namespace DotCharter.Tone.Audio.Tuner;

/// <summary>Verdict of the passive tuning-drift watchdog.</summary>
public enum TuningDriftVerdict
{
    /// <summary>Not enough (decayed) evidence to say anything yet.</summary>
    Insufficient,

    /// <summary>Enough evidence, mean offset inside the thresholds.</summary>
    Stable,

    /// <summary>The instrument has drifted FLAT — nudge "check your tuning".</summary>
    DriftFlat,

    /// <summary>The instrument has drifted SHARP — nudge "check your tuning".</summary>
    DriftSharp
}

/// <summary>
///     Passive tuning-drift watchdog (0.18 Additions): a pure
///     accumulator of per-note SIGNED cent evidence gathered while the player
///     plays, raising a "check your tuning" verdict once the exponentially
///     decayed, weighted mean offset crosses
///     <see cref="DriftThresholdCents"/> — with hysteresis (the verdict only
///     clears back below <see cref="ClearThresholdCents"/>), an evidence
///     floor (<see cref="MinEvidenceWeight"/>) so a couple of bent notes
///     can't cry wolf, and an outlier clamp
///     (<see cref="MaxEvidenceMagnitudeCents"/>) so one octave-error reading
///     can't skew the mean.
///     <para>
///     EVIDENCE SOURCE (0.19). The CQT chart verifier still cannot supply it:
///     `JuceChartVerifier.Verify` returns {Hit, Confidence, Level} — a binary
///     match-mask verdict with no signed cent offset — over quarter-tone
///     (50 ¢) bins. The wiring therefore comes from the MANAGED tuner core
///     instead: <c>LiveTuningDriftMonitor</c> (Services, Audio/LivePlay) runs
///     <see cref="TunerPitchEngine"/> (MPM→YIN) at 4 Hz over the
///     non-destructive input-capture window and feeds the deviation from the
///     NEAREST equal-tempered semitone, weighted by clarity, with the
///     arrangement's authored <c>CentOffset</c> subtracted. That is sound
///     because an instrument x cents flat plays EVERY note x cents flat — and
///     so do that note's harmonics, so a reading that locked onto a partial
///     (or a chord's root) still carries the right offset. If the verifier
///     ever grows a real per-note cents output, feed it here instead (weight =
///     verifier confidence); nothing in this class has to change.
///     </para>
///     <para>
///     Pure and clock-free: time is injected as <c>nowMs</c>; no audio, no
///     dispatcher, allocation-free per call. Managed slice of the 0.18 tone
///     rig — reference implementation for the native graduation (see
///     <see cref="IPitchDetector"/>).
///     </para>
/// </summary>
public sealed class TuningDriftWatchdog
{
    private double _weightSum;
    private double _centsSum;
    private double _lastEvidenceMs = double.NaN;

    /// <summary>|mean| that raises the drift verdict.</summary>
    public double DriftThresholdCents { get; init; } = 8.0;

    /// <summary>|mean| below which a raised verdict clears (hysteresis —
    /// must be less than <see cref="DriftThresholdCents"/>).</summary>
    public double ClearThresholdCents { get; init; } = 4.0;

    /// <summary>Decayed evidence weight required before any verdict beyond
    /// <see cref="TuningDriftVerdict.Insufficient"/>.</summary>
    public double MinEvidenceWeight { get; init; } = 8.0;

    /// <summary>Half-life of evidence: a note played this long ago counts
    /// half. Keeps the verdict about the CURRENT tuning, not the whole set.</summary>
    public double EvidenceHalfLifeMs { get; init; } = 30_000.0;

    /// <summary>Per-evidence clamp on |cents| — octave/harmonic mistakes in
    /// the source must not dominate the mean.</summary>
    public double MaxEvidenceMagnitudeCents { get; init; } = 50.0;

    /// <summary>Current verdict (re-evaluated on every evidence).</summary>
    public TuningDriftVerdict Verdict { get; private set; } = TuningDriftVerdict.Insufficient;

    /// <summary>Decayed weighted mean offset in cents (0 while empty).</summary>
    public double MeanCents => _weightSum > 0 ? _centsSum / _weightSum : 0.0;

    /// <summary>Decayed total evidence weight.</summary>
    public double EvidenceWeight => _weightSum;

    /// <summary>Raised on every verdict transition.</summary>
    public event Action<TuningDriftVerdict>? VerdictChanged;

    /// <summary>Clears all evidence (new song, retune, profile switch).</summary>
    public void Reset()
    {
        _weightSum = 0;
        _centsSum = 0;
        _lastEvidenceMs = double.NaN;
        SetVerdict(TuningDriftVerdict.Insufficient);
    }

    /// <summary>
    ///     Feeds one piece of per-note evidence: the SIGNED cent offset of
    ///     the detected pitch against the charted target (negative = flat),
    ///     at injected time <paramref name="nowMs"/>, optionally weighted
    ///     (e.g. by verifier confidence). Returns the updated verdict.
    /// </summary>
    public TuningDriftVerdict AddEvidence(double signedCents, double nowMs, double weight = 1.0)
    {
        if (weight <= 0 || double.IsNaN(signedCents)) return Verdict;

        if (!double.IsNaN(_lastEvidenceMs))
        {
            var elapsed = nowMs - _lastEvidenceMs;
            if (elapsed > 0)
            {
                var decay = Math.Pow(2.0, -elapsed / EvidenceHalfLifeMs);
                _weightSum *= decay;
                _centsSum *= decay;
            }
        }

        _lastEvidenceMs = nowMs;

        var clamped = Math.Clamp(signedCents, -MaxEvidenceMagnitudeCents, MaxEvidenceMagnitudeCents);
        _weightSum += weight;
        _centsSum += weight * clamped;

        Reevaluate();
        return Verdict;
    }

    private void Reevaluate()
    {
        var mean = MeanCents;
        var drifting = Verdict is TuningDriftVerdict.DriftFlat or TuningDriftVerdict.DriftSharp;

        if (drifting)
        {
            // Hysteresis: stay raised until the mean is convincingly back.
            if (Math.Abs(mean) <= ClearThresholdCents)
            {
                SetVerdict(TuningDriftVerdict.Stable);
                return;
            }

            // Direction can flip (overshot retune) without passing the clear band.
            if (Math.Abs(mean) >= DriftThresholdCents)
                SetVerdict(mean < 0 ? TuningDriftVerdict.DriftFlat : TuningDriftVerdict.DriftSharp);
            return;
        }

        if (_weightSum < MinEvidenceWeight)
        {
            SetVerdict(TuningDriftVerdict.Insufficient);
            return;
        }

        SetVerdict(Math.Abs(mean) >= DriftThresholdCents
            ? mean < 0 ? TuningDriftVerdict.DriftFlat : TuningDriftVerdict.DriftSharp
            : TuningDriftVerdict.Stable);
    }

    private void SetVerdict(TuningDriftVerdict verdict)
    {
        if (Verdict == verdict) return;
        Verdict = verdict;
        VerdictChanged?.Invoke(verdict);
    }
}
