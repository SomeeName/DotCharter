using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tone.Audio.Tuner;

/// <summary>How a capoed arrangement is checked.</summary>
public enum TuningCheckCapoMode
{
    /// <summary>Rocksmith flow: tune the OPEN strings to the arrangement
    /// tuning first, then place the capo (the UI instructs the capo step
    /// after the check completes). Capo does not shift the targets.</summary>
    TuneOpenThenPlaceCapo,

    /// <summary>Verify with the capo already on: every target is raised by
    /// the capo fret (mirrors <c>LivePitchAcceptance.SoundingMidi</c> — a capo
    /// raises the open strings).</summary>
    VerifyWithCapoOn
}

/// <summary>One per-string tuning-check target, low string first.
/// <see cref="FrequencyHz"/> includes the arrangement's
/// <c>CentOffset</c> (charts referenced off A440 must be tuned off A440,
/// or live detection will disagree with the player's tuner).</summary>
public readonly record struct TuningCheckTarget(
    int StringIndex, int Midi, string NoteName, double FrequencyHz);

/// <summary>
///     Derives the per-string target table for the guided tuning check from
///     an <see cref="Arrangement"/>: Rocksmith semantics — per-string
///     semitone offsets against standard tuning, resolved by
///     <see cref="ArrangementTunerLogic.TryResolveTuningSteps"/> (the
///     <c>TuningPitches</c>, else the decoded <c>TuningType</c> — the same
///     resolver Tools ▸ Tuner loads through), bass anchored one octave down, plus capo rules
///     (<see cref="TuningCheckCapoMode"/>) and the chart's
///     <c>CentOffset</c>. The base open-string math is REUSED from
///     <see cref="ArrangementTunerLogic"/> so the tuner dialog and the guided
///     check can never disagree about what a string should sound like.
///     <para>
///     Managed slice of the 0.18 tuner suite (see
///     <see cref="IPitchDetector"/> for the native-graduation framing).
///     Built once per check — not a per-block path.
///     </para>
/// </summary>
public static class TuningCheckTargets
{
    /// <summary>
    ///     Builds the target table, low string first — <b>assuming E standard when
    ///     the arrangement's tuning cannot be resolved at all</b> (no offsets and a
    ///     tuning NAME the table does not know). The guided check needs something
    ///     to aim at, so that default is deliberate here; anything that DISPLAYS a
    ///     string's name must call <see cref="TryBuild" /> and say it does not
    ///     know.
    /// </summary>
    /// <param name="arrangement">The path to tune for.</param>
    /// <param name="capoMode">How a capo is checked.</param>
    /// <param name="transposeSemitones">Tuning-match transpose: the song is shifted this
    /// far to the player's tuning, so every string is tuned that much higher than charted.</param>
    public static TuningCheckTarget[] Build(Arrangement arrangement,
        TuningCheckCapoMode capoMode = TuningCheckCapoMode.TuneOpenThenPlaceCapo,
        int transposeSemitones = 0)
    {
        if (TryBuild(arrangement, capoMode, out var targets, transposeSemitones)) return targets;

        // The deliberate default: all-zero offsets, i.e. standard for this
        // instrument. Written out rather than reached through the tuning table's
        // own zeros fallback, so the assumption is visible where it is made.
        return BuildFrom(arrangement, capoMode, new int[ArrangementTunerLogic.ResolvedStringCount(arrangement)],
            transposeSemitones);
    }

    /// <summary>
    ///     The honest half: <c>false</c> when the arrangement states no per-string
    ///     offsets AND its <c>TuningType</c> is a name
    ///     <see cref="TuningUtilities.TryGetOffsetsForTuningType" /> does not know
    ///     — i.e. when nothing in the chart says what any string is tuned to. The
    ///     timeline gutter renders that as <c>?</c> rather than inventing an "E".
    /// </summary>
    public static bool TryBuild(Arrangement arrangement, TuningCheckCapoMode capoMode,
        out TuningCheckTarget[] targets, int transposeSemitones = 0)
    {
        // Charts that carry only a named tuning are fine — until the name is one
        // nothing can decode.
        if (!ArrangementTunerLogic.TryResolveTuningSteps(arrangement, out var steps))
        {
            targets = [];
            return false;
        }

        targets = BuildFrom(arrangement, capoMode, steps, transposeSemitones);
        return true;
    }

    private static TuningCheckTarget[] BuildFrom(
        Arrangement arrangement, TuningCheckCapoMode capoMode, int[] steps, int transposeSemitones)
    {
        var stringCount = arrangement.StringCount;
        var logic = new ArrangementTunerLogic();
        logic.Load(arrangement.ArrangementType, stringCount, steps);

        var capoShift = capoMode == TuningCheckCapoMode.VerifyWithCapoOn && arrangement.Capo > 0
            ? arrangement.Capo
            : 0;

        // An A220 reference (a 5/6-string bass on a guitar path) is an octave
        // of the NOTE, not 1200 cents of detune: B0, not B1 tuned an octave flat.
        var octave = ArrangementInstrument.ReferenceOctaveSemitones(arrangement.CentOffset);
        var residualCents = ArrangementInstrument.ResidualCents(arrangement.CentOffset);

        var targets = new TuningCheckTarget[logic.Targets.Count];
        for (var i = 0; i < targets.Length; i++)
        {
            var midi = logic.Targets[i].Midi + octave + capoShift + transposeSemitones;
            var frequency = PitchMath.ShiftByCents(
                ArrangementTunerLogic.FrequencyOf(midi), residualCents);
            targets[i] = new TuningCheckTarget(i, midi, ArrangementTunerLogic.NoteNameOf(midi), frequency);
        }

        return targets;
    }
}
