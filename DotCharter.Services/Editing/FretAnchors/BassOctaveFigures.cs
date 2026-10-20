namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
/// Bass octave figures: a fretted note, then the same note an octave up — two strings higher, two frets
///     higher (the slap/pop octave) — within the next <see cref="Reach" /> notes. A bassist sets the index on the ROOT
///     when the figure starts and takes the octave with the ring or pinky, even when the root alone was reachable from
///     where the hand already was. The owner's finished charts do that at 98.4% of 2,078 starts (tools/chart-eval
///     <c>--octaves</c>).
///     <para>
///         The shape is only a candidate: the pair must SOUND an octave apart through the arrangement's tuning
///         (<see cref="SoundingPitch.MidiFor" />, as <c>TwoFretPairFingering.IsTwoFretOctave</c> asks). In drop D the
///         low string is a tone down, so low-E fret 3 + D fret 5 is a ninth, not an octave.
///     </para>
/// </summary>
internal static class BassOctaveFigures
{
    /// <summary>How many notes after the root the octave may come (1 = the very next note).</summary>
    internal const int Reach = 2;

    /// <summary>
    ///     The positions of every octave-figure START: a single fretted note above the capo whose octave follows within
    ///     <see cref="Reach" /> notes, and whose previous note was neither that root nor its octave (a repeated figure
    ///     starts once). Only single notes count; a position with a second sound is a double stop, not a figure.
    /// </summary>
    /// <param name="notes">The level's single notes.</param>
    /// <param name="capo">The arrangement's capo; a root at or below it is open.</param>
    /// <param name="tuning">The owning arrangement, read for its tuning; null = the fret shape alone.</param>
    public static HashSet<FractionalPosition> FindStarts(IEnumerable<GuitarNote> notes, int capo,
        Arrangement? tuning = null) => Find(notes, capo, tuning).Starts;

    /// <summary>
    ///     <see cref="FindStarts" />, plus the position of the OCTAVE note of every figure — a repeat included — so a
    ///     pass that moves anchors can keep a root and its octave under one hand.
    /// </summary>
    public static (HashSet<FractionalPosition> Starts, HashSet<FractionalPosition> Octaves) Find(
        IEnumerable<GuitarNote> notes, int capo, Arrangement? tuning = null)
    {
        var ordered = notes.OrderBy(n => n.Position).ToList();
        var starts = new HashSet<FractionalPosition>();
        var octaves = new HashSet<FractionalPosition>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var root = ordered[i];
            if (root.Fret <= capo || SharesPosition(ordered, i)) continue;

            var octave = -1;
            for (var j = i + 1; j <= i + Reach && j < ordered.Count && octave < 0; j++)
            {
                if (IsOctaveOf(root, ordered[j], tuning)) octave = j;
            }

            if (octave < 0) continue;
            octaves.Add(ordered[octave].Position);

            if (i > 0 && (IsSameNote(ordered[i - 1], root) || IsOctaveOf(root, ordered[i - 1], tuning))) continue;
            starts.Add(root.Position);
        }

        return (starts, octaves);
    }

    private static bool SharesPosition(List<GuitarNote> ordered, int i) =>
        i > 0 && ordered[i - 1].Position == ordered[i].Position ||
        i + 1 < ordered.Count && ordered[i + 1].Position == ordered[i].Position;

    private static bool IsSameNote(GuitarNote a, GuitarNote b) => a.StringIndex == b.StringIndex && a.Fret == b.Fret;

    private static bool IsOctaveOf(GuitarNote root, GuitarNote other, Arrangement? tuning)
    {
        if (other.StringIndex != root.StringIndex + 2 || other.Fret != root.Fret + 2) return false;
        if (tuning == null) return true;
        var low = SoundingPitch.MidiFor(tuning, root.StringIndex, root.Fret);
        var high = SoundingPitch.MidiFor(tuning, other.StringIndex, other.Fret);
        return low != null && high != null && high - low == 12;
    }
}
