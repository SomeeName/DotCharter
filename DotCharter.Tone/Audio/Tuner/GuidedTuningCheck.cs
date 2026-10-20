namespace DotCharter.Tone.Audio.Tuner;

/// <summary>Lifecycle of one string inside a guided tuning check.</summary>
public enum TuningCheckStringState
{
    /// <summary>Not the current string yet (or demoted from current).</summary>
    Waiting,

    /// <summary>Current string; no reading near the target yet.</summary>
    Detecting,

    /// <summary>Within ±<see cref="GuidedTuningCheck.CloseCents"/>.</summary>
    Close,

    /// <summary>Within ±<see cref="GuidedTuningCheck.InTuneCents"/>,
    /// accumulating the stability hold.</summary>
    InTune,

    /// <summary>Held in tune long enough — done.</summary>
    Confirmed,

    /// <summary>Player skipped it (revisit via
    /// <see cref="GuidedTuningCheck.SelectString"/>).</summary>
    Skipped
}

/// <summary>One progress frame of the guided check (raised per reading).</summary>
public readonly record struct TuningCheckProgress(
    int StringIndex,
    TuningCheckStringState State,
    double CentsOff,
    double HoldProgress,
    bool WrongStringSuspected);

/// <summary>
///     Guided, game-style tuning check: ONE STRING AT A TIME, lowest first.
///     Each string walks waiting → detecting → close (±<see cref="CloseCents"/>)
///     → in-tune (±<see cref="InTuneCents"/> plus a cumulative stability hold)
///     → confirmed; strings are skippable and progress is surfaced through
///     events. Targets come from <see cref="TuningCheckTargets"/> (arrangement
///     tuning + TuningPitches + capo rules + CentOffset).
///     <para>
///     This flow is monophonic by design (one string at a time, readings
///     judged only against the CURRENT string's target) — it is the default
///     game UX. The hard patent constraint against a polyphonic
///     "strum all six" mode is LIFTED (owner decision: US
///     8,334,449 B2 was ruled ineligible when Empower Tribe's suit against
///     Boss was dismissed, and the project is Austria-based); a polyphonic
///     mode may be added as an option.
///     </para>
///     <para>
///     Pure state machine: no clocks, no audio, no dispatcher — time is
///     injected as <c>nowMs</c> on every call, pitch readings come from the
///     caller (e.g. <see cref="TunerPitchEngine"/> over the input-capture
///     tap). The stability hold is CUMULATIVE across plucks: silence between
///     plucks pauses accumulation (report it via
///     <see cref="ProcessSilence"/>) and per-reading credit is capped by
///     <see cref="MaxHoldCreditPerReadingMs"/>, while an audibly off-target
///     reading resets the hold — the same philosophy as the tuner dialog's
///     <c>ArrangementTunerLogic</c>. Managed slice of the 0.18 tuner suite;
///     reference implementation for the Phase-0 native graduation (see
///     <see cref="IPitchDetector"/>).
///     </para>
/// </summary>
public sealed class GuidedTuningCheck
{
    private readonly TuningCheckTarget[] _targets;
    private readonly TuningCheckStringState[] _states;
    private readonly double[] _holdMs;
    private double _lastInTuneMs = double.NaN;

    /// <summary>±cents that counts as "close" (almost there feedback).</summary>
    public double CloseCents { get; init; } = 10.0;

    /// <summary>±cents that counts as in tune (hold accumulates inside).</summary>
    public double InTuneCents { get; init; } = 5.0;

    /// <summary>Cumulative in-tune time required to confirm a string.</summary>
    public double StabilityHoldMs { get; init; } = 600.0;

    /// <summary>Cap on the hold credit a single reading can earn — a long
    /// gap between plucks (or ticks) must not count as held time.</summary>
    public double MaxHoldCreditPerReadingMs { get; init; } = 250.0;

    /// <summary>Beyond ±this the reading is probably a different string (or
    /// wildly off); it neither advances nor resets anything except the hold,
    /// and the progress frame flags it.</summary>
    public double WrongStringCents { get; init; } = 150.0;

    /// <summary>Capo fret to instruct AFTER the check in
    /// <see cref="TuningCheckCapoMode.TuneOpenThenPlaceCapo"/> flows
    /// (0 = none).</summary>
    public int CapoFret { get; init; }

    public IReadOnlyList<TuningCheckTarget> Targets => _targets;
    public int CurrentStringIndex { get; private set; }
    public TuningCheckTarget CurrentTarget => _targets[CurrentStringIndex];
    public bool IsComplete { get; private set; }

    /// <summary>Raised for every processed reading on the current string.</summary>
    public event Action<TuningCheckProgress>? ProgressChanged;

    /// <summary>Raised once when a string reaches Confirmed.</summary>
    public event Action<int>? StringConfirmed;

    /// <summary>Raised when every string is Confirmed or Skipped.</summary>
    public event Action? Completed;

    /// <summary>Builds a check over an arrangement (see
    /// <see cref="TuningCheckTargets.Build"/>). <see cref="CapoFret"/> is
    /// taken from the arrangement.</summary>
    public GuidedTuningCheck(Arrangement arrangement,
        TuningCheckCapoMode capoMode = TuningCheckCapoMode.TuneOpenThenPlaceCapo,
        int transposeSemitones = 0)
        : this(TuningCheckTargets.Build(arrangement, capoMode, transposeSemitones)) => CapoFret = arrangement.Capo;

    /// <summary>Builds a check over explicit targets (testable seam).</summary>
    public GuidedTuningCheck(IReadOnlyList<TuningCheckTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("At least one target string is required.", nameof(targets));

        _targets = new TuningCheckTarget[targets.Count];
        for (var i = 0; i < targets.Count; i++) _targets[i] = targets[i];
        _states = new TuningCheckStringState[targets.Count];
        _holdMs = new double[targets.Count];
        _states[0] = TuningCheckStringState.Detecting;
    }

    public TuningCheckStringState StateOf(int stringIndex) => _states[stringIndex];

    /// <summary>Feeds one detected pitch reading at injected time
    /// <paramref name="nowMs"/>. Judged ONLY against the current string.</summary>
    public void Process(double frequencyHz, double nowMs)
    {
        if (IsComplete || frequencyHz <= 0) return;

        var current = CurrentStringIndex;
        var cents = PitchMath.CentsBetween(frequencyHz, _targets[current].FrequencyHz);
        var magnitude = Math.Abs(cents);

        if (magnitude > WrongStringCents)
        {
            // Probably a different string — don't punish the current one
            // beyond pausing its hold accumulation.
            _lastInTuneMs = double.NaN;
            SetState(current, TuningCheckStringState.Detecting);
            RaiseProgress(current, cents, wrongString: true);
            return;
        }

        if (magnitude <= InTuneCents)
        {
            if (!double.IsNaN(_lastInTuneMs))
            {
                var credit = nowMs - _lastInTuneMs;
                if (credit > 0)
                    _holdMs[current] += Math.Min(credit, MaxHoldCreditPerReadingMs);
            }

            _lastInTuneMs = nowMs;

            if (_holdMs[current] >= StabilityHoldMs)
            {
                SetState(current, TuningCheckStringState.Confirmed);
                RaiseProgress(current, cents, wrongString: false);
                StringConfirmed?.Invoke(current);
                AdvancePastCurrent();
                return;
            }

            SetState(current, TuningCheckStringState.InTune);
            RaiseProgress(current, cents, wrongString: false);
            return;
        }

        // Audibly off the target: whatever hold it had is stale.
        _holdMs[current] = 0;
        _lastInTuneMs = double.NaN;
        SetState(current, magnitude <= CloseCents
            ? TuningCheckStringState.Close
            : TuningCheckStringState.Detecting);
        RaiseProgress(current, cents, wrongString: false);
    }

    /// <summary>Feeds one silent tick: accumulation pauses (the hold is
    /// cumulative across plucks and must survive the decay between them),
    /// nothing else changes.</summary>
    public void ProcessSilence() => _lastInTuneMs = double.NaN;

    /// <summary>Skips the current string (revisit later via
    /// <see cref="SelectString"/>).</summary>
    public void SkipCurrentString()
    {
        if (IsComplete) return;

        var current = CurrentStringIndex;
        _holdMs[current] = 0;
        _lastInTuneMs = double.NaN;
        SetState(current, TuningCheckStringState.Skipped);
        RaiseProgress(current, 0, wrongString: false);
        AdvancePastCurrent();
    }

    /// <summary>Makes <paramref name="stringIndex"/> the current string and
    /// (re)checks it — also the way back to a Skipped or Confirmed string.</summary>
    public void SelectString(int stringIndex)
    {
        if (stringIndex < 0 || stringIndex >= _targets.Length)
            throw new ArgumentOutOfRangeException(nameof(stringIndex));

        var previous = CurrentStringIndex;
        if (previous != stringIndex && IsUnsettled(_states[previous]))
        {
            _states[previous] = TuningCheckStringState.Waiting;
            _holdMs[previous] = 0;
        }

        CurrentStringIndex = stringIndex;
        _states[stringIndex] = TuningCheckStringState.Detecting;
        _holdMs[stringIndex] = 0;
        _lastInTuneMs = double.NaN;
        IsComplete = false;
    }

    /// <summary>Resets every string and starts over at the lowest.</summary>
    public void Restart()
    {
        Array.Clear(_states);
        Array.Clear(_holdMs);
        _lastInTuneMs = double.NaN;
        CurrentStringIndex = 0;
        _states[0] = TuningCheckStringState.Detecting;
        IsComplete = false;
    }

    private static bool IsUnsettled(TuningCheckStringState state)
        => state is TuningCheckStringState.Detecting
            or TuningCheckStringState.Close
            or TuningCheckStringState.InTune;

    private void SetState(int stringIndex, TuningCheckStringState state)
        => _states[stringIndex] = state;

    private void RaiseProgress(int stringIndex, double cents, bool wrongString)
    {
        var progress = Math.Clamp(_holdMs[stringIndex] / StabilityHoldMs, 0.0, 1.0);
        if (_states[stringIndex] == TuningCheckStringState.Confirmed) progress = 1.0;
        ProgressChanged?.Invoke(new TuningCheckProgress(
            stringIndex, _states[stringIndex], cents, progress, wrongString));
    }

    /// <summary>Moves to the next string that still needs attention
    /// (wrapping); completes the check when none is left.</summary>
    private void AdvancePastCurrent()
    {
        _lastInTuneMs = double.NaN;

        for (var offset = 1; offset <= _targets.Length; offset++)
        {
            var candidate = (CurrentStringIndex + offset) % _targets.Length;
            if (_states[candidate] is TuningCheckStringState.Confirmed
                or TuningCheckStringState.Skipped)
            {
                continue;
            }

            CurrentStringIndex = candidate;
            _states[candidate] = TuningCheckStringState.Detecting;
            _holdMs[candidate] = 0;
            return;
        }

        IsComplete = true;
        Completed?.Invoke();
    }
}
