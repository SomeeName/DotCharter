namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     Whether two chord shapes are the SAME grip moved along the neck — an A5 at
///     fret 5 and the identical grip at fret 7.
///     <para>
///         <b>The point is fingering.</b> A transposed grip is played with exactly
///         the same fingers, so a charter who fingers one has already fingered them
///         all; without this they finger each position separately. The owner
///         reported it directly: "some of the edited chords are also very similar,
///         only all moved 1 fret up or down".
///     </para>
///     <para>
///         The existing propagation (<c>FindMatchingTemplatesNeedingFingering</c>)
///         cannot serve this. It matches templates with an IDENTICAL fret pattern
///         and only fills ones still MISSING a fingering — two constraints this
///         deliberately relaxes, because a transposition has different frets by
///         definition, and re-fingering something the suggester already guessed
///         wrong is the whole reason to ask.
///     </para>
/// </summary>
public static class ChordShapeTransposition
{
    /// <summary>
    ///     True when <paramref name="candidate" /> is <paramref name="source" />
    ///     shifted along the neck, reporting the shift in
    ///     <paramref name="semitones" /> (0 when the shapes are identical).
    ///     <para>
    ///         <b>An OPEN string never transposes.</b> A capo-less open string is
    ///         stopped by the nut, not the hand, so it cannot move with the grip —
    ///         fret 0 matches only fret 0, and a shape whose open strings differ is
    ///         a different grip however well its fretted notes line up. This is the
    ///         same rule <c>FretRule.Transposable</c> already applies in the pattern
    ///         matcher, and it must stay identical or "select alike" and "finger
    ///         alike" would disagree about what alike means.
    ///     </para>
    ///     <para>
    ///         The two shapes must use exactly the same STRINGS. A grip moved to
    ///         other strings is a different hand shape even at the same intervals.
    ///     </para>
    /// </summary>
    public static bool IsTransposition(
        ChordTemplate? source, ChordTemplate? candidate, int capo, out int semitones)
    {
        semitones = 0;
        if (source == null || candidate == null) return false;
        if (ReferenceEquals(source, candidate)) return false;
        if (source.Frets.Count == 0 || source.Frets.Count != candidate.Frets.Count) return false;

        // An arpeggio is a different KIND of shape, not a different position of
        // one — the flag is load-bearing on export (ChordMask.Arpeggio, the .eof
        // section type, purple brackets instead of a hand-shape bar). TWO flags
        // spell it and each import path sets only one, so this is IsArpeggio and
        // not a single-flag read: reading `Arpeggio` alone made every arpeggio
        // on a DLC-Builder / EOF chart "alike" to the plain shape beside it.
        if (source.IsArpeggio != candidate.IsArpeggio) return false;

        int? shift = null;

        foreach (var (stringIndex, sourceFret) in source.Frets)
        {
            if (!candidate.Frets.TryGetValue(stringIndex, out var candidateFret)) return false;

            var sourceOpen = sourceFret <= capo;
            var candidateOpen = candidateFret <= capo;

            // Open on one side and fretted on the other is a different grip, and
            // two opens simply agree without constraining the shift.
            if (sourceOpen != candidateOpen) return false;
            if (sourceOpen) continue;

            var delta = candidateFret - sourceFret;
            if (shift == null) shift = delta;
            else if (shift != delta) return false;
        }

        // Every string open: nothing moved, and nothing to move.
        if (shift == null) return false;

        semitones = shift.Value;
        return true;
    }

    /// <summary>
    ///     Every template in <paramref name="arrangement" /> that is
    ///     <paramref name="source" /> moved along the neck, nearest position first.
    ///     <para>
    ///         Includes shift 0 — a same-fret shape that is a SEPARATE template
    ///         (the chart minted one when a chord was re-fingered privately) is
    ///         exactly the case the charter wants unified, and excluding it would
    ///         make "finger these alike" quietly skip the closest matches.
    ///         <paramref name="source" /> itself is never returned.
    ///     </para>
    /// </summary>
    public static List<ChordTemplate> FindTransposed(
        Arrangement arrangement, ChordTemplate source, int capo = 0)
    {
        var found = new List<(ChordTemplate Template, int Distance)>();
        foreach (var candidate in arrangement.ChordTemplates)
        {
            if (IsTransposition(source, candidate, capo, out var shift))
                found.Add((candidate, Math.Abs(shift)));
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found.Select(f => f.Template).ToList();
    }
}
