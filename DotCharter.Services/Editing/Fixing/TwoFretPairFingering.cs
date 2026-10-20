namespace DotCharter.Services.Editing.Fixing;

/// <summary>The two-string grips whose fingering is a per-song choice.</summary>
public enum TwoFretPair
{
    /// <summary>
    ///     Root and fifth on ADJACENT strings, the fifth two frets up (E/A/D strings
    ///     to the next, or B to high e, in standard tuning). The suggester gives
    ///     <c>1·3</c>.
    /// </summary>
    PowerChord,

    /// <summary>
    ///     Root and octave with one string between them, the octave two frets up
    ///     (low E to D, A to G in standard tuning). The suggester gives <c>1·4</c>.
    /// </summary>
    TwoFretOctave
}

/// <summary>
///     Finds the two-string grips whose fingering the SONG chooses — the power
///     chord and the two-fret octave — and switches every one of them in an
/// arrangement between <c>1·4</c> and <c>1·3</c>.
///     <para>
///         <b>Why the song chooses.</b> In the owner's 86 verified charts the
///         two-string power chord is <c>1·3</c> in 3,552 chords (33 songs) and
///         <c>1·4</c> in 1,231 (9 songs); the two-fret octave is <c>1·4</c> in
///         1,828 chords (8 songs) and <c>1·3</c> in 635 (14 songs), and an
///         arrangement almost never mixes them. The shapes around them do NOT
///         vary: root·fifth·octave is <c>1·3·4</c> in all 2,940 chords, the octave
///         across the G–B third (three frets) <c>1·4</c> in all 1,455, and the
///         G–B power chord (also three frets) <c>1·4</c> in 258 of 260. Those are
///         never targets.
///     </para>
///     <para>
///         <b>A pair is found by its SOUNDING interval</b>, not by string names:
///         the upper note two frets higher and a fifth (seven semitones, adjacent
///         strings) or an octave (twelve, one string skipped) above the root
///         through <see cref="SoundingPitch.MidiFor" />, tuning offsets and capo
///         included. In drop D the low pair two frets apart is a sixth, not a
///         power chord.
///     </para>
///     <para>
///         <b>Editing the template in place is correct here</b>, for the reason
///         <see cref="DoubleStopChordNames" /> gives: no fret moves, so no chord
///         changes shape, and every chord on a template plays the same grip. The
///         index finger stays on the root, so FHP anchors and E04 are untouched.
///         A fingering the charter set to something else (a thumb, a <c>2·4</c>)
///         is not one of the two this command chooses between and is left alone.
///     </para>
/// </summary>
public static class TwoFretPairFingering
{
    /// <summary>What <c>ChordTemplateFingerSetter</c> gives the pair with no style hint.</summary>
    public static PairFingering SuggesterDefault(TwoFretPair pair) =>
        pair == TwoFretPair.PowerChord ? PairFingering.IndexRing : PairFingering.IndexPinky;

    /// <summary>
    ///     Which pair <paramref name="template" /> is in <paramref name="arrangement" />'s
    ///     tuning, or null; returns its lower and upper string.
    /// </summary>
    public static TwoFretPair? Classify(Arrangement arrangement, ChordTemplate template, out int low, out int high)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(template);
        low = high = -1;
        if (ChordTemplateFingerSetter.TwoFretPairStrings(template) is not var (lowString, highString)) return null;

        TwoFretPair? pair = (highString - lowString) switch
        {
            1 => TwoFretPair.PowerChord,
            2 => TwoFretPair.TwoFretOctave,
            _ => null
        };
        if (pair == null) return null;

        var lowPitch = SoundingPitch.MidiFor(arrangement, lowString, template.Frets[lowString]);
        var highPitch = SoundingPitch.MidiFor(arrangement, highString, template.Frets[highString]);
        var interval = pair == TwoFretPair.PowerChord ? 7 : 12;
        if (lowPitch == null || highPitch == null || highPitch - lowPitch != interval) return null;

        low = lowString;
        high = highString;
        return pair;
    }

    /// <summary>True when <paramref name="template" /> is a two-fret octave; returns its lower and upper string.</summary>
    public static bool IsTwoFretOctave(Arrangement arrangement, ChordTemplate template, out int low, out int high) =>
        Classify(arrangement, template, out low, out high) == TwoFretPair.TwoFretOctave;

    /// <summary>
    ///     The <paramref name="pair" /> templates this command may rewrite:
    ///     unfingered, <c>1·3</c> or <c>1·4</c>. Anything else was set on purpose.
    /// </summary>
    public static IReadOnlyList<ChordTemplate> Targets(Arrangement arrangement, TwoFretPair pair)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var targets = new List<ChordTemplate>();
        foreach (var template in arrangement.ChordTemplates)
        {
            if (Classify(arrangement, template, out var low, out var high) != pair) continue;
            if (Current(template, low, high) == null && HasAnyFinger(template, low, high)) continue;
            targets.Add(template);
        }

        return targets;
    }

    /// <summary>
    ///     What one press sets: the fingering the suggester does NOT give
    ///     (<c>1·3</c> for an octave, <c>1·4</c> for a power chord) unless every
    ///     target already has it, in which case it goes back. A mixed arrangement
    ///     therefore becomes consistent on the first press, which is what the
    ///     corpus says a song wants.
    /// </summary>
    public static PairFingering Next(Arrangement arrangement, TwoFretPair pair, IReadOnlyList<ChordTemplate> targets)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var away = SuggesterDefault(pair) == PairFingering.IndexRing ? PairFingering.IndexPinky : PairFingering.IndexRing;
        foreach (var template in targets)
        {
            if (Classify(arrangement, template, out var low, out var high) != pair) continue;
            if (Current(template, low, high) != away) return away;
        }

        return SuggesterDefault(pair);
    }

    /// <summary>The finger map for <paramref name="fingering" /> on the two strings.</summary>
    public static Dictionary<int, int> FingersFor(PairFingering fingering, int low, int high) =>
        new() { [low] = 1, [high] = fingering == PairFingering.IndexRing ? 3 : 4 };

    /// <summary>Which of the two fingerings a template has, or null for anything else.</summary>
    public static PairFingering? Current(ChordTemplate template, int low, int high)
    {
        if (!template.Fingers.TryGetValue(low, out var lowFinger) || lowFinger != 1) return null;
        if (!template.Fingers.TryGetValue(high, out var highFinger)) return null;
        return highFinger switch
        {
            3 => PairFingering.IndexRing,
            4 => PairFingering.IndexPinky,
            _ => null
        };
    }

    private static bool HasAnyFinger(ChordTemplate template, int low, int high) =>
        template.Fingers.TryGetValue(low, out var a) && a >= 0 || template.Fingers.TryGetValue(high, out var b) && b >= 0;
}
