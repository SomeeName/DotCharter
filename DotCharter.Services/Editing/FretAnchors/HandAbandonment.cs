namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     The "hand abandonment" re-anchor (walk rule P6): the fret hand re-plants UP
///     onto a phrase that has left the low fret, even though the anchor in force
///     still reaches every note. Without it the anchor sticks at the low-water mark
///     of a phrase that has climbed and stabilised — the dominant bass FHP error,
///     +18 pp on the hand-corrected EOF corpus.
///     <para>
///         Bass carries two more conditions, learned from the owner's finished
/// charts (<c>tools/chart-eval</c>, train split; bass FHP train /
///         holdout): the hand moves only onto the note that plays the new
///         position's lowest fret (<see cref="Root" />), and it stays where it is
///         while the excursion comes back within the fingers it already has
///         (<see cref="StaysWithinReach" />).
///     </para>
/// </summary>
internal static class HandAbandonment
{
    // How many struck positions ahead must ALL play above the anchor before the
    // hand re-plants. Lower = follows the hand up more eagerly (more anchors),
    // higher = stickier. Bass: 6, validated against the hand-corrected EOF corpus
    // (bass anchor agreement 63% -> 81%) and still the peak on the owner's
    // finished charts (4: 83.6 / 80.7, 6: 84.1 / 81.7, 8: 83.2 / 81.0 % train /
    // holdout; with the two extra conditions 4: 86.0 / 85.6, 6: 85.9 / 85.6, 3:
    // 86.2 / 84.8); an oscillating riff that dips back to the low fret within 6
    // notes correctly HOLDS its anchor.
    internal const int BassHold = 6;

    // Guitar was gated OFF when the rule was measured on the EOF corpus (at 6 it
    // fragmented arpeggios a human anchors once). On the owner's finished charts
    // (tools/chart-eval) a much stickier hold wins on both splits — guitar
    // FHP agreement, train / holdout, with the bounce collapse on:
    //     off  83.0 / 90.5      6  83.7 / 89.4      10  84.2 / 90.5
    //     14   84.6 / 90.8      18  84.3 / 90.6
    // 14 is the top of a broad 10..18 plateau, and with the bounce collapse the
    // anchor CHANGE count still falls below the plain walk's.
    internal const int GuitarHold = 14;

    // Bass: the frets above the anchor a bassist keeps playing from where
    // the hand is — index, middle and pinky over three frets. When an excursion
    // above the anchor comes back down to it and never reached past anchor + 2,
    // the charters HOLD (they re-planted on 8-33% of such runs, against 72-78%
    // on runs that leave the three frets). Measured 84.7 / 82.7 -> 85.2 / 84.8 %
    // bass FHP; a reach of 3 (the four-fret box) was -2.9 / -3.0 pp against it. Guitar: -0.2 pp
    // train, rejected.
    internal const int BassStayReach = 2;

    // How far ahead (struck positions) the return to the anchor is looked for.
    // Flat on train from 16 up (85.8 / 85.8 / 85.9 % at 16 / 24 / unbounded);
    // 24 keeps the scan bounded, and the Guitar Pro answer key (whose judge
    // re-plants on a long 2 -> 4 bass figure) loses 0.9 pp instead of 1.5.
    internal const int BassStayScan = 24;

    /// <summary>
    ///     The fret to re-plant on at struck position <paramref name="ordinal" />,
    ///     or null to hold <paramref name="currentFret" />. The fret is the lowest
    ///     one played over the next <see cref="BassHold" /> /
    ///     <see cref="GuitarHold" /> positions, and the hand moves only when ALL
    ///     of them play strictly above <paramref name="currentFret" /> (so the
    ///     result is always above it: the caller only ever moves the hand up).
    ///     On bass it also holds when this position does not play that lowest fret
    ///     itself — the charters move one note later, onto the note the index
    ///     finger takes (+0.4 pp holdout, ±0 train, fewer anchor changes) — and
    ///     when <see cref="StaysWithinReach" />.
    /// </summary>
    public static int? Root(IStruckSounds struck, int ordinal, int currentFret, bool bass, int minFret, int maxFret)
    {
        var hold = bass ? BassHold : GuitarHold;
        if (ordinal < 0 || ordinal + hold > struck.Count) return null;

        var min = int.MaxValue;
        for (var i = ordinal; i < ordinal + hold; i++)
        {
            var low = struck.MinFretAt(i);
            if (low <= currentFret) return null;
            if (low < min) min = low;
        }

        if (bass && (struck.MinFretAt(ordinal) != min || StaysWithinReach(struck, ordinal, currentFret))) return null;
        return Math.Clamp(min, minFret, maxFret);
    }

    /// <summary>
    ///     True when the sounds from <paramref name="ordinal" /> on come back down
    ///     to <paramref name="anchor" /> within <see cref="BassStayScan" /> struck
    ///     positions without ever playing above anchor + <see cref="BassStayReach" />.
    /// </summary>
    public static bool StaysWithinReach(IStruckSounds struck, int ordinal, int anchor)
    {
        var end = Math.Min(struck.Count, ordinal + BassStayScan);
        for (var i = ordinal; i < end; i++)
        {
            if (struck.MinFretAt(i) <= anchor) return true;
            if (struck.MaxFretAt(i) > anchor + BassStayReach) return false;
        }

        return false;
    }
}
