using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     What a (string, fret) pair actually SOUNDS — the pitch, not the position.
///     <para>
///         The arithmetic lived privately inside the SNG mapper
///         (<c>SngFromDomainMapper.MapToMidiNotes</c>), which is why nothing that
///         the charter can see was able to answer "what note is this?". It has
///         three parts and each is a rule this codebase states elsewhere:
///         the string's standard-tuning open pitch, the arrangement's TUNING
///         OFFSET for that string (always through
///         <see cref="TuningOffsetResolution" />), and the CAPO — fret 0 is open,
///         and a capo stops it at the capo fret.
///     </para>
///     <para>
///         A bass sounds an octave below where it is written, which is why the
///         SNG mapper subtracts 12 and so does this. That octave belongs to the
///         Bass PATH and stays keyed on <see cref="Arrangement.ArrangementType" />:
///         a bass charted on a GUITAR path gets its octave from the tuning
///         reference (A220, <c>CentOffset</c> −1200) instead, which
///         <see cref="MidiFor" /> does not add — see <see cref="ArrangementInstrument" />.
///     </para>
/// </summary>
public static class SoundingPitch
{
    /// <summary>
    ///     How many string indices the rule prices: the six of a standard neck (so a
    ///     4/5-string chart answers for indices 0..5 exactly as it always has), or
    ///     every string of a 7/8-string guitar, whose open pitches come from
    /// <see cref="StandardOpenStrings" /> — string 0 of a 7-string is B1.
    /// </summary>
    private static int StringRange(Arrangement arrangement)
        => Math.Clamp(arrangement.StringCount, StandardOpenStrings.DefaultStringCount,
            StandardOpenStrings.MaxStringCount);

    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>
    ///     The MIDI note a fretted string sounds, or <c>null</c> when the string
    ///     is outside the arrangement or the fret is behind the capo (malformed —
    ///     inspector <c>L01</c>).
    /// </summary>
    public static int? MidiFor(Arrangement? arrangement, int stringIndex, int fret)
    {
        if (arrangement == null) return null;
        if (stringIndex < 0 || stringIndex >= StringRange(arrangement)) return null;
        if (fret < 0) return null;

        var capo = Math.Max(0, arrangement.Capo);
        // Fret 0 is OPEN, and a capo stops it at the capo fret. A non-zero fret
        // BELOW the capo is malformed rather than lower-sounding.
        if (fret > 0 && fret < capo) return null;

        return OpenPitch(arrangement, stringIndex) + Math.Max(fret, capo);
    }

    /// <summary>
    ///     The fret that sounds <paramref name="midi" /> on
    ///     <paramref name="stringIndex" />, or <c>null</c> when that string
    ///     cannot reach the pitch — below its open note, or past the 24th fret.
    /// </summary>
    public static int? FretForPitch(Arrangement? arrangement, int stringIndex, int midi, int maxFret = 24)
    {
        if (arrangement == null) return null;
        if (stringIndex < 0 || stringIndex >= StringRange(arrangement)) return null;

        var capo = Math.Max(0, arrangement.Capo);
        var fret = midi - OpenPitch(arrangement, stringIndex);

        if (fret < capo) return null; // the string cannot go that low behind the capo
        if (fret > maxFret) return null;
        // At the capo the sound IS the open string — spell it 0, which is what
        // every fret consumer expects behind a capo.
        return capo > 0 && fret == capo ? 0 : fret;
    }

    /// <summary>
    ///     Snaps an arbitrary fret number onto the frets the neck actually HAS.
    ///     <para>
    ///         Behind a capo the legal set is <c>{0} ∪ [capo, maxFret]</c>, not
    ///         <c>[0, maxFret]</c>: fret 0 is the open (capo'd) string and a
    ///         non-zero fret below the capo is malformed — inspector rule
    ///         <c>L01</c> (<c>NoteFretBelowCapo</c>), which
    ///         <c>ArrangementAutoFixService.FixCapoFrets</c> silently raises to
    ///         the capo on the next auto-fix and <see cref="MidiFor" /> refuses
    ///         to price at all. Every verb that WRITES a fret has to land inside
    ///         that set or it manufactures a finding the charter never asked for.
    ///     </para>
    ///     <para>
    ///         <paramref name="direction" /> is how the caller ARRIVED at the
    ///         number, and it only matters strictly between the nut and the capo.
    ///         A descent (<c>&lt; 0</c>) that falls off the back of the capo
    ///         lands OPEN — a fret-down run stays monotone and can still reach
    ///         fret 0. An ascent or an ABSOLUTE write (<c>0</c>, the typed digit)
    ///         lands on the capo, the first fret that exists, which is the same
    ///         answer <c>FixCapoFrets</c> gives.
    ///     </para>
    ///     <para>
    ///         <b>Capo <c>0</c> is a strict no-op</b> — the whole neck is legal,
    ///         so this is exactly <c>Math.Clamp(fret, 0, maxFret)</c> and no
    ///         un-capo'd chart changes behaviour.
    ///     </para>
    /// </summary>
    public static int SnapToNeck(int fret, int capo, int direction = 0, int maxFret = 24)
    {
        var clamped = Math.Clamp(fret, 0, maxFret);
        if (capo <= 0 || clamped == 0 || clamped >= capo) return clamped;
        return direction < 0 ? 0 : Math.Min(capo, maxFret);
    }

    /// <summary>Scientific-pitch name for a MIDI note, e.g. 45 ⇒ <c>A2</c>.</summary>
    public static string NameFor(int midi)
    {
        var octave = midi / 12 - 1;
        var name = NoteNames[((midi % 12) + 12) % 12];
        return $"{name}{octave}";
    }

    /// <summary>
    ///     The name a fretted string sounds, or <c>null</c> when it has none. Unlike
    ///     <see cref="MidiFor" />, this is the note the listener HEARS: a tuning
    ///     reference an octave down (A220, how a 5/6-string bass is charted on a
    /// guitar path) moves it an octave down too
    ///     (<see cref="ArrangementInstrument.ReferenceOctaveSemitones" />), so the low
    ///     B of such a bass reads B0, not the guitar's B1.
    /// </summary>
    public static string? NameFor(Arrangement? arrangement, int stringIndex, int fret) =>
        MidiFor(arrangement, stringIndex, fret) is { } midi
            ? NameFor(midi + ArrangementInstrument.ReferenceOctaveSemitones(arrangement!.CentOffset))
            : null;

    /// <summary>
    ///     The MIDI note an OPEN string sounds before any capo — the standard open
    ///     pitch, the arrangement's tuning offset and the bass octave — or
    ///     <c>null</c> when the string is outside the ones the rule knows. For a
    ///     consumer that adds frets itself (the MIDI preview caches these per
    ///     arrangement, because <see cref="TuningOffsetResolution" /> allocates).
    /// </summary>
    public static int? OpenStringMidi(Arrangement? arrangement, int stringIndex)
    {
        if (arrangement == null) return null;
        if (stringIndex < 0 || stringIndex >= StringRange(arrangement)) return null;
        return OpenPitch(arrangement, stringIndex);
    }

    private static int OpenPitch(Arrangement arrangement, int stringIndex)
    {
        var bassDrop = arrangement.ArrangementType == ArrangementType.Bass ? -12 : 0;
        return StandardOpenStrings.GuitarLowEMidi
               + StandardOpenStrings.SemitonesAboveLowE(arrangement.StringCount, stringIndex)
               + TuningOffsetResolution.For(arrangement, stringIndex)
               + bassDrop;
    }
}
