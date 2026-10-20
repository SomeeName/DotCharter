namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
/// The fret map of a power chord: the 5th (and the octave) of a root note, spelled on the strings
///     above it. Pure arithmetic over an <see cref="Arrangement" />'s tuning and capo, so the
///     Power chord verbs and their tests share one answer.
/// </summary>
internal static class PowerChordFrets
{
    /// <summary>
    ///     The OPEN-string semitone of a string, tuning folded in — no capo,
    ///     because this answers "where does this string start", not "what does
    ///     this fret sound". Capo belongs at the fret, in
    ///     <see cref="SoundingSemitone" />.
    /// </summary>
    private static int GetStringSemitone(Arrangement arrangement, int stringIndex)
    {
        // Relative to E2, lowest string first; a 7-string's string 0 is B1.
        var baseSemitone = StandardOpenStrings.SemitonesAboveLowE(arrangement.StringCount, stringIndex);
        var offset = stringIndex >= 0 && stringIndex < arrangement.TuningPitches.Count
            ? arrangement.TuningPitches[stringIndex].Step
            : 0;
        return baseSemitone + offset;
    }

    /// <summary>
    ///     What a fret on a string actually SOUNDS. Frets are absolute on the
    ///     un-capoed neck and fret <c>0</c> means "open", which a capo stops at
    ///     the capo fret — so the sounding offset is <c>max(fret, capo)</c>, the
    ///     same derivation as <c>ChordNameSuggester.GetSortedSounds</c> /
    ///     <c>LivePitchAcceptance.SoundingMidi</c>. Capo <c>0</c> is a strict no-op.
    /// </summary>
    private static int SoundingSemitone(Arrangement arrangement, int stringIndex, int fret)
        => GetStringSemitone(arrangement, stringIndex) + Math.Max(fret, Math.Max(0, arrangement.Capo));

    /// <summary>
    ///     The 5th (and, when <paramref name="big" />, the octave) of the note at
    ///     <paramref name="stringIndex" />/<paramref name="fret" />, as the
    ///     string-to-fret map <see cref="ChordTemplateResolver.Resolve" /> keys
    ///     on. Null when the shape cannot be spelled on this neck.
    ///     <para>
    ///         <b>Capo-aware.</b> The root's pitch is its SOUNDING pitch (an open
    ///         string under a capo sounds at the capo, not at the nut), and a
    ///         derived fret below the capo is REFUSED rather than written: the
    ///         capo already stops that string higher up, so the interval is
    ///         unreachable there and writing it would mint the malformed sub-capo
    ///         fret inspector rule <c>L01</c> /
    ///         <c>ChartExportValidator.KeyNoteBelowCapo</c> exists to flag. It is
    ///         reachable with nothing exotic: on standard tuning the octave of a
    ///         BIG power chord rooted on the G string lands at <c>fret - 2</c>.
    ///         At capo <c>0</c> the whole thing is byte-identical to the
    ///         capo-blind version it replaced (<c>max(f,0) == f</c> for the
    ///         non-negative frets that reach here, and <c>f &lt; 0</c> is
    ///         <c>f &lt; capo</c>).
    ///     </para>
    /// </summary>
    public static Dictionary<int, int>? Build(Arrangement arrangement, int stringIndex, int fret,
        bool big)
    {
        if (stringIndex < 0 || fret < 0) return null;
        var stringCount = arrangement.StringCount;
        var maxFret = 24;
        var capo = Math.Max(0, arrangement.Capo);

        var rootSemitone = SoundingSemitone(arrangement, stringIndex, fret);
        var frets = new Dictionary<int, int> { [stringIndex] = fret };

        // Perfect 5th on string+1
        if (stringIndex + 1 >= stringCount) return null;
        var fifthFret = rootSemitone + 7 - GetStringSemitone(arrangement, stringIndex + 1);
        if (fifthFret < capo || fifthFret > maxFret) return null;
        frets[stringIndex + 1] = fifthFret;

        if (big)
        {
            if (stringIndex + 2 >= stringCount) return null;
            var octaveFret = rootSemitone + 12 - GetStringSemitone(arrangement, stringIndex + 2);
            if (octaveFret < capo || octaveFret > maxFret) return null;
            frets[stringIndex + 2] = octaveFret;
        }

        return frets;
    }
}
