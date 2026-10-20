using DotCharter.Localization;
using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     The passive tuning-drift watchdog of a Live Play run: a periodic pitch
///     read off the live input, compared against the nearest equal-tempered
///     semitone, folded into a <see cref="TuningDriftWatchdog" />, and the HUD
///     hint its verdict produces.
///     <para>
///         Pure like the rest of this folder: no audio and no dispatcher. A check
///         is TWO calls on the detection worker, under the caller's session gate:
///         <see cref="BeginCheck" /> answers with the window to fill when a check
///         is due (or null), the caller fills it from the non-destructive capture
///         tap, and <see cref="CompleteCheck" /> takes the sample count it read and
///         answers with the new hint when the verdict flipped (or null).
///         Allocation-free after the first check at a sample rate.
///     </para>
/// </summary>
public sealed class LiveTuningDriftMonitor
{
    // ---- passive tuning-drift watchdog -------------------------------------------
    //
    // WHERE THE CENTS COME FROM. The CQT chart verifier cannot supply them: its
    // native result (`JuceVerifyResult`) is {Hit, Confidence, Level} — a binary
    // match-mask verdict, with no signed offset of the detected pitch against the
    // charted note, and its bins are quarter-tones (50 cents) wide anyway. So the
    // evidence is taken from the MANAGED tuner core instead
    // (`TunerPitchEngine` = MPM→YIN, which reports FrequencyHz + Clarity) over the
    // same non-destructive input-capture window the pre-play signal meter polls.
    //
    // Deviation is measured against the NEAREST equal-tempered semitone, not
    // against the charted note. Two things make that sound:
    //   * an instrument that has gone x cents flat plays EVERY note x cents flat,
    //     so the nearest-note residual IS the drift; and
    //   * the nth harmonic of a note that is x cents flat is itself x cents flat,
    //     so even a window where the detector locks onto a partial (or onto the
    //     root of a chord) still yields the right offset.
    // The chart's authored `Arrangement.CentOffset` (a deliberately non-A440 song)
    // is subtracted, or the whole song would read as drifted.
    //
    // The watchdog itself does the hard part — exponential decay, an evidence
    // floor, an outlier clamp and hysteresis — so a couple of bends cannot cry
    // wolf. It is pure and clock-injected; we hand it a monotonic ms.

    /// <summary>Drift is sampled at 4 Hz, not on every 30 Hz tick: a pitch
    /// window is a real cost and the verdict decays over 30 SECONDS.</summary>
    public const double DriftCheckIntervalMs = 250.0;

    /// <summary>MPM/YIN already gate on clarity; this only keeps the cleanest
    /// windows, since a noisy estimate's cents are meaningless.</summary>
    public const double DriftMinClarity = 0.9;

    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly TuningDriftWatchdog _drift = new();
    private TunerPitchEngine? _driftPitch;
    private float[]? _driftWindow;
    private double _lastDriftCheckMs = double.NegativeInfinity;
    private double _driftCentOffset;
    private double _checkNowMs;

    public LiveTuningDriftMonitor(ILocalizationService localization, IAppLogger logger)
    {
        _localization = localization;
        _logger = logger;
    }

    /// <summary>Signed cent deviation of a pitch from the nearest equal-tempered
    /// semitone (−50..+50; negative = flat). NaN for a non-positive frequency.</summary>
    public static double SignedCentsFromNearestSemitone(double frequencyHz)
    {
        if (frequencyHz <= 0 || double.IsNaN(frequencyHz))
        {
            return double.NaN;
        }

        var midi = 69.0 + 12.0 * Math.Log2(frequencyHz / 440.0);
        return (midi - Math.Round(midi)) * 100.0;
    }

    /// <summary>Drops every piece of evidence and takes the authored cent offset
    /// of the arrangement the next run plays (0 for none).</summary>
    public void Reset(double centOffset)
    {
        _drift.Reset();
        _lastDriftCheckMs = double.NegativeInfinity;
        _driftCentOffset = NearestSemitoneResidual(centOffset);
    }

    /// <summary>
    ///     The part of an authored cent offset a nearest-semitone reading can see:
    ///     the reading is always within ±50 ¢, so whole semitones of the offset —
    ///     above all the −1200 of an A220 chart, how a 5/6-string bass is charted
    /// on a guitar path — must be folded away first, or every in-tune
    ///     window reads as 1200 ¢ off.
    /// </summary>
    /// <param name="centOffset">The arrangement's <c>CentOffset</c>.</param>
    /// <returns>The offset folded into −50..+50 ¢.</returns>
    public static double NearestSemitoneResidual(double centOffset) =>
        centOffset - 100.0 * Math.Round(centOffset / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>
    ///     First half of a check. Null when no check is due at <paramref name="nowMs" />
    ///     (a monotonic millisecond clock) or there is no input; otherwise the window
    ///     the caller fills from the capture tap and hands back through
    ///     <see cref="CompleteCheck" />. A due check is consumed even when it then
    ///     finds no input, exactly like the 4 Hz cadence it paces.
    /// </summary>
    public float[]? BeginCheck(double nowMs, int sampleRate)
    {
        if (nowMs - _lastDriftCheckMs < DriftCheckIntervalMs)
        {
            return null;
        }

        _lastDriftCheckMs = nowMs;

        if (sampleRate <= 0)
        {
            return null;
        }

        if (_driftPitch == null || _driftPitch.SampleRate != sampleRate)
        {
            _driftPitch = new TunerPitchEngine(sampleRate);
            _driftWindow = new float[_driftPitch.WindowSize];
        }

        _checkNowMs = nowMs;
        return _driftWindow;
    }

    /// <summary>
    ///     Second half of a check: <paramref name="samplesRead" /> is what the capture
    ///     tap put into the window <see cref="BeginCheck" /> returned. Answers with the
    ///     hint to show when the verdict flipped — the localized flat/sharp line, or
    ///     empty for a verdict that clears it — and null when nothing changed.
    /// </summary>
    public string? CompleteCheck(int samplesRead)
    {
        if (_driftPitch == null || _driftWindow == null || samplesRead < _driftWindow.Length)
        {
            return null;
        }

        var reading = _driftPitch.Detect(_driftWindow);
        if (!reading.HasPitch || reading.Clarity < DriftMinClarity)
        {
            return null;
        }

        var cents = SignedCentsFromNearestSemitone(reading.FrequencyHz) - _driftCentOffset;
        var before = _drift.Verdict;
        var verdict = _drift.AddEvidence(cents, _checkNowMs, reading.Clarity);
        if (verdict == before)
        {
            return null;
        }

        var hint = verdict switch
        {
            TuningDriftVerdict.DriftFlat => _localization["LivePlay_TuningDrift_Flat"],
            TuningDriftVerdict.DriftSharp => _localization["LivePlay_TuningDrift_Sharp"],
            _ => ""
        };

        if (hint.Length > 0)
        {
            _logger.Info(AppLogCategory.Audio,
                $"Tuning drift: {verdict} (mean {_drift.MeanCents:+0.0;-0.0} cents over " +
                $"{_drift.EvidenceWeight:0.0} decayed evidence).");
        }

        return hint;
    }
}
