namespace DotCharter.Tone.Audio.Midi;

/// <summary>
///     The pitch the MIDI preview plays for a (string, fret) of an arrangement. The RULE is
///     <see cref="SoundingPitch" />'s — standard open pitch, the tuning offset (through
///     <see cref="TuningOffsetResolution" />, so a letters-only arrangement is read too) and the
///     bass octave — so the preview cannot disagree with the SNG export or the inspector. It used
/// to keep a guitar-only copy and played every bass an octave high.
///     <para>
///         <b>Cached, because it runs per 4 ms tick.</b> <see cref="TuningOffsetResolution" />
///         allocates, so the six open pitches are resolved only when something they depend on
///         changed: the arrangement instance, its type, its tuning reference (an A220
/// reference moves every note an octave down), its letters array (or one of its
///         letters) or one of its offsets. The check itself reads those in place and allocates
///         nothing, so a live retune is still picked up on the next tick.
///     </para>
/// </summary>
internal sealed class MidiStringPitches
{
    private const int MaxStrings = StandardOpenStrings.MaxStringCount;

    private readonly int[] _open = new int[MaxStrings];
    private readonly int[] _steps = new int[MaxStrings];
    private readonly string?[] _letters = new string?[MaxStrings];
    private Arrangement? _arrangement;
    private ArrangementType _type;
    private int _stringCount;
    private double _centOffset;
    private string[]? _tuning;
    private int _stepCount = -1;

    /// <summary>Re-reads the open pitches when the arrangement's tuning changed; otherwise free.</summary>
    public void Refresh(Arrangement arrangement)
    {
        if (!Changed(arrangement)) return;

        _arrangement = arrangement;
        _type = arrangement.ArrangementType;
        _stringCount = arrangement.StringCount;
        _centOffset = arrangement.CentOffset;
        _tuning = arrangement.Tuning;
        var steps = arrangement.TuningPitches;
        _stepCount = steps.Count;
        for (var i = 0; i < MaxStrings; i++)
        {
            _steps[i] = i < steps.Count ? steps[i].Step : 0;
            _letters[i] = _tuning != null && i < _tuning.Length ? _tuning[i] : null;
            // An A220 reference (a 5/6-string bass on a guitar path) is an
            // octave of the NOTE: a MIDI note has no cents to carry it in.
            // -1 = a string the rule does not price (past the sixth of a <=6-string chart).
            _open[i] = SoundingPitch.OpenStringMidi(arrangement, i) is { } open
                ? open + ArrangementInstrument.ReferenceOctaveSemitones(arrangement.CentOffset)
                : -1;
        }
    }

    /// <summary>
    ///     The MIDI note to play, or 0 for "nothing" (a muted/negative fret or a string the chart
    ///     does not have — past the sixth, or past the eighth of an extended-range guitar). A capo stops the string at the capo fret — <c>max(fret, capo)</c>, the same as
    ///     <see cref="SoundingPitch.MidiFor" /> for every well-formed note. Unlike it, a fret
    ///     BELOW the capo (inspector L01) still plays, at the capo: a preview should not go silent
    ///     on a chart that is being fixed.
    /// </summary>
    public int MidiFor(int stringIndex, int fret, int capo)
    {
        if (fret < 0 || stringIndex < 0 || stringIndex >= MaxStrings || _arrangement == null) return 0;
        if (_open[stringIndex] < 0) return 0;
        return Math.Clamp(_open[stringIndex] + Math.Max(fret, capo), 0, 127);
    }

    private bool Changed(Arrangement arrangement)
    {
        if (!ReferenceEquals(arrangement, _arrangement) || arrangement.ArrangementType != _type
            || arrangement.StringCount != _stringCount
            || !arrangement.CentOffset.Equals(_centOffset) || !ReferenceEquals(arrangement.Tuning, _tuning))
        {
            return true;
        }

        var steps = arrangement.TuningPitches;
        if (steps.Count != _stepCount) return true;
        for (var i = 0; i < MaxStrings; i++)
        {
            if ((i < steps.Count ? steps[i].Step : 0) != _steps[i]) return true;
            if (!ReferenceEquals(_tuning != null && i < _tuning.Length ? _tuning[i] : null, _letters[i])) return true;
        }

        return false;
    }
}
