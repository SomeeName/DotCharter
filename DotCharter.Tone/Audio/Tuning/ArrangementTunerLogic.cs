namespace DotCharter.Tone.Audio.Tuning;

/// <summary>One open-string tuning target, low string first.</summary>
public sealed record TunerStringTarget(int StringIndex, int Midi, string NoteName, double FrequencyHz);

/// <summary>
///     Result of feeding one pitch reading to the tuner logic. <c>InRange</c>: the
///     reading lies within <see cref="ArrangementTunerLogic.MaxStringDistanceCents" />
///     of <c>NearestString</c>'s target, so it counted toward that string's
///     hold/confirm state; <c>false</c> = a stray note or an octave-off reading,
///     shown for guidance but neither confirming nor resetting anything.
/// </summary>
public readonly record struct TunerFrame(
    int NearestString,
    double CentsOff,
    bool InTune,
    bool StringJustConfirmed,
    bool InRange);

/// <summary>
///     Pure, UI-free core of the arrangement tuner: builds the per-string
///     target table from an arrangement's tuning (Rocksmith semantics:
///     <c>TuningPitches</c> are semitone offsets against standard tuning),
///     picks the nearest target for a detected pitch, computes the cent
///     deviation against that target and runs the hold-to-confirm state
///     machine (a string counts as tuned after staying within
///     <see cref="InTuneCents"/> for <see cref="HoldToConfirmMs"/> cumulative;
///     an out-of-tune reading on that string resets it — but only one within
///     <see cref="MaxStringDistanceCents"/> of it).
///     <para>
///     Standard open strings low-to-high: guitar E2 A2 D3 G3 B3 E4
///     (MIDI 40 45 50 55 59 64), bass E1 A1 D2 G2 (MIDI 28 33 38 43); a
///     7-string adds a LOW B1 and an 8-string a low F#1 below them — the
///     one <see cref="StandardOpenStrings" /> table live judging uses too,
///     anchored one octave lower for bass arrangements.
///     </para>
/// </summary>
public sealed class ArrangementTunerLogic
{
    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private TunerStringTarget[] _targets = [];

    /// <summary>The chart's reference-pitch offset from A440, in cents. Every
    /// target frequency and every reported deviation is measured against it.</summary>
    private double _centOffsetCents;
    private double[] _holdMs = [];
    private bool[] _tuned = [];

    /// <summary>Absolute cent deviation that still counts as in tune.</summary>
    public double InTuneCents { get; init; } = 5.0;

    /// <summary>Cumulative in-tune time before a string is confirmed.</summary>
    public double HoldToConfirmMs { get; init; } = 500.0;

    /// <summary>
    ///     Beyond ±this from the nearest string's target a reading is a stray note
    ///     (or a detector octave error), not that string being tuned: it is still
    ///     reported, but it never touches any string's hold or confirmation. The
    ///     same 150 ¢ as <see cref="Tuner.GuidedTuningCheck.WrongStringCents" />, the
    ///     guard the Get Ready check applies to the same kind of reading.
    ///     <para>
    ///         <b>No octave folding, on purpose.</b> Folding a reading an octave off
    ///         onto a string would credit a string the player never plucked: open
    ///         strings stand in octaves of each other (E2 and E4 in standard, D2 and
    ///         D3 in Drop D, E1 and E2 across a bass and a guitar path), so a folded
    ///         reading is ambiguous exactly where it matters. The pitch engines
    ///         already reject octave errors upstream; one that slips through is
    ///         ignored here rather than guessed at.
    ///     </para>
    /// </summary>
    public double MaxStringDistanceCents { get; init; } = 150.0;

    public IReadOnlyList<TunerStringTarget> Targets => _targets;

    public bool AllTuned => _targets.Length > 0 && Array.TrueForAll(_tuned, t => t);

    public bool IsStringTuned(int stringIndex)
        => stringIndex >= 0 && stringIndex < _tuned.Length && _tuned[stringIndex];

    /// <summary>
    ///     Builds the target table from an arrangement and resets all
    ///     hold/confirm state.
    ///     <para>
    ///         <b>The arrangement's <c>CentOffset</c> is part of the target</b>, and
    ///         ignoring it would be wrong: a chart referenced off A440 has to
    ///         be TUNED off A440 or live judging disagrees with the player's tuner
    ///         — which is exactly what <see cref="Tuner.TuningCheckTargets" /> says
    ///         in its own doc, and it folds the offset into every target frequency.
    ///         So Tools &gt; Tuner told the player to tune to A440 while the Get
    ///         Ready guided check, over the same arrangement, told them to tune
    ///         (say) 50 ¢ flat: two screens, one chart, two answers.
    ///     </para>
    ///     <para>
    ///         <b>The open-string offsets come from <see cref="ResolveTuningSteps" /></b>,
    ///         the one resolver <see cref="Tuner.TuningCheckTargets" /> uses too: a chart
    ///         that names its tuning (<c>TuningType</c>) without per-string
    ///         <c>TuningPitches</c> is decoded, not read as E standard. The capo never
    ///         moves these targets — the player tunes the OPEN strings and then places
    ///         it, the guided check's default <c>TuneOpenThenPlaceCapo</c> flow.
    ///     </para>
    /// </summary>
    public void Load(Arrangement arrangement)
        => Load(arrangement.ArrangementType, arrangement.StringCount,
            ResolveTuningSteps(arrangement), arrangement.CentOffset);

    /// <summary>
    ///     THE resolver for an arrangement's per-string semitone offsets against
    ///     standard (low string first): its <c>TuningPitches</c> when it has any,
    ///     otherwise its <c>TuningType</c> name decoded by
    ///     <see cref="TuningUtilities.TryGetOffsetsForTuningType" />. <c>false</c>
    ///     when neither says anything (no offsets and a name the table does not
    ///     know). Every tuner screen builds its targets through here, so they cannot
    ///     disagree about what a string is tuned to.
    /// </summary>
    public static bool TryResolveTuningSteps(Arrangement arrangement, out int[] steps)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (arrangement.TuningPitches.Count > 0)
        {
            steps = new int[arrangement.TuningPitches.Count];
            for (var i = 0; i < steps.Length; i++) steps[i] = arrangement.TuningPitches[i].Step;
            return true;
        }

        return TuningUtilities.TryGetOffsetsForTuningType(
            arrangement.TuningType, ResolvedStringCount(arrangement), out steps);
    }

    /// <summary>
    ///     <see cref="TryResolveTuningSteps" />, <b>assuming standard (all zeros)
    ///     when the tuning cannot be resolved at all</b> — a tuner needs something
    ///     to aim at. Anything that DISPLAYS a string's name must use the Try form
    ///     and say it does not know.
    /// </summary>
    public static int[] ResolveTuningSteps(Arrangement arrangement)
        => TryResolveTuningSteps(arrangement, out var steps) ? steps : new int[ResolvedStringCount(arrangement)];

    /// <summary>The arrangement's string count, or the instrument's default
    /// (4 for bass, 6 otherwise) when it states none.</summary>
    public static int ResolvedStringCount(Arrangement arrangement)
        => arrangement.StringCount > 0
            ? arrangement.StringCount
            : arrangement.ArrangementType == ArrangementType.Bass
                ? 4
                : 6;

    /// <summary>Builds the target table from raw parts (testable seam).
    /// <paramref name="tuningSteps"/> entries missing for a string default
    /// to 0 (standard). <paramref name="centOffsetCents"/> is the chart's
    /// reference-pitch offset from A440: every target frequency moves with it,
    /// and so does the deviation <see cref="Process"/> reports. A reference an
    /// OCTAVE down (A220 — a 5/6-string bass charted on a guitar path) is
    /// folded into the NOTE (<see cref="ArrangementInstrument.ReferenceOctaveSemitones"/>),
    /// so its low string is named B0 rather than the guitar's B1; only the
    /// residual cents stay an offset. <paramref name="type"/> is the PATH and only
    /// picks which open strings the offsets are measured from.</summary>
    public void Load(ArrangementType type, int stringCount, IReadOnlyList<int> tuningSteps,
        double centOffsetCents = 0.0)
    {
        var isBass = type == ArrangementType.Bass;
        if (stringCount <= 0) stringCount = isBass ? 4 : 6;
        stringCount = Math.Clamp(stringCount, 1, StandardOpenStrings.MaxStringCount);

        _centOffsetCents = ArrangementInstrument.ResidualCents(centOffsetCents);
        var baseMidi = StandardOpenStrings.LowEMidi(isBass)
                       + ArrangementInstrument.ReferenceOctaveSemitones(centOffsetCents);
        _targets = new TunerStringTarget[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            var step = i < tuningSteps.Count ? tuningSteps[i] : 0;
            var midi = baseMidi + StandardOpenStrings.SemitonesAboveLowE(stringCount, i) + step;
            // The NAME is the note; the FREQUENCY is where that note actually
            // sits for this chart. TuningCheckTargets makes the same split.
            _targets[i] = new TunerStringTarget(i, midi, NoteNameOf(midi),
                FrequencyOf(midi + _centOffsetCents / 100.0));
        }

        _holdMs = new double[stringCount];
        _tuned = new bool[stringCount];
    }

    /// <summary>Standard MIDI note name with octave (E2, A2, D3, …).</summary>
    public static string NoteNameOf(int midi)
        => $"{NoteNames[((midi % 12) + 12) % 12]}{midi / 12 - 1}";

    /// <summary>Equal-tempered frequency of a (fractional) MIDI note.</summary>
    public static double FrequencyOf(double midi)
        => 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);

    /// <summary>Fractional MIDI value of a frequency (A4 = 440 Hz = 69).</summary>
    public static double MidiOf(double frequencyHz)
        => 69.0 + 12.0 * Math.Log2(frequencyHz / 440.0);

    /// <summary>Signed cent deviation of a frequency against a target note.</summary>
    public static double CentsFrom(double frequencyHz, int targetMidi)
        => (MidiOf(frequencyHz) - targetMidi) * 100.0;

    /// <summary>Index of the target string nearest (in pitch) to the detected
    /// frequency; -1 while no targets are loaded. Unbounded on purpose — it is
    /// the DISPLAY pick (which chip to light, which way to turn a peg that is
    /// several semitones off); whether a reading may change a string's state is
    /// <see cref="IsInRange" />.</summary>
    public int NearestString(double frequencyHz)
    {
        if (_targets.Length == 0) return -1;
        // Measured in the chart's own reference, so a whole-chart CentOffset
        // cannot tip a reading onto the neighbouring string.
        var midi = MidiOf(frequencyHz) - _centOffsetCents / 100.0;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _targets.Length; i++)
        {
            var distance = Math.Abs(midi - _targets[i].Midi);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }

    /// <summary>The reading is within <see cref="MaxStringDistanceCents" /> of
    /// string <paramref name="stringIndex" />'s target, in the chart's own
    /// reference.</summary>
    public bool IsInRange(double frequencyHz, int stringIndex)
        => stringIndex >= 0 && stringIndex < _targets.Length &&
           Math.Abs(CentsFrom(frequencyHz, _targets[stringIndex].Midi) - _centOffsetCents) <= MaxStringDistanceCents;

    /// <summary>
    ///     Feeds one detected pitch reading. Call only for real readings —
    ///     silence must NOT be fed (the hold time is cumulative across
    ///     plucks and must survive the decay between them).
    /// </summary>
    public TunerFrame Process(double frequencyHz, double deltaMs)
    {
        var nearest = NearestString(frequencyHz);
        if (nearest < 0) return new TunerFrame(-1, 0, false, false, false);

        var cents = CentsFrom(frequencyHz, _targets[nearest].Midi) - _centOffsetCents;

        // A stray note or an octave-off reading is not this string being tuned:
        // report it, but never let it restart a string the player already tuned.
        if (Math.Abs(cents) > MaxStringDistanceCents) return new TunerFrame(nearest, cents, false, false, false);

        var inTune = Math.Abs(cents) <= InTuneCents;
        var justConfirmed = false;

        if (inTune)
        {
            _holdMs[nearest] += Math.Max(0, deltaMs);
            if (!_tuned[nearest] && _holdMs[nearest] >= HoldToConfirmMs)
            {
                _tuned[nearest] = true;
                justConfirmed = true;
            }
        }
        else
        {
            // The string audibly sounds off target: whatever confirmation it
            // had is stale (the player detuned it, or never got there).
            _holdMs[nearest] = 0;
            _tuned[nearest] = false;
        }

        return new TunerFrame(nearest, cents, inTune, justConfirmed, true);
    }
}
