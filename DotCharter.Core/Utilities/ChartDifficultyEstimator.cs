using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Deterministic 1–10 difficulty rating for an arrangement, from a single
///     pure pass over its hardest level: note density, chord-change rate,
///     technique mix and tempo. No I/O, no randomness, no clocks — the same
///     chart always produces the exact same double, so ratings are safe to
///     cache, sort by and ship in export manifests.
///     <para>
///     Each factor is normalized through a saturating curve
///     (<c>x / (x + k)</c>, strictly monotonic) so one extreme factor cannot
///     blow past the scale, then the factors are weighted and mapped onto
///     1..10. Ratings are comparable BETWEEN charts, not an absolute promise —
///     the calibration constants make a sparse 90 BPM strum land low and a
///     dense technique-heavy 200 BPM chart land high.
///     </para>
/// </summary>
public static class ChartDifficultyEstimator
{
    // Factor weights (sum = 1).
    private const double DensityWeight = 0.45;
    private const double ChordWeight = 0.20;
    private const double TechniqueWeight = 0.20;
    private const double TempoWeight = 0.15;

    // Saturation half-points: the factor value at which its subscore is 0.5.
    private const double DensityHalfPoint = 5.0; // sounds per second
    private const double ChordChangeHalfPoint = 0.8; // shape changes per second
    private const double TempoHalfPointBpmAbove60 = 90.0; // 150 BPM ⇒ 0.5

    /// <summary>Number of distinct technique kinds the mix factor recognizes
    /// (bend, slide, legato, harmonic, mute, tremolo, vibrato, slap/pop).</summary>
    private const double TechniqueKinds = 8.0;

    /// <summary>Rates the arrangement's HARDEST level (highest
    /// <see cref="ChartLevel.DifficultyIndex"/>; later index wins a tie) —
    /// the chart as a top-difficulty player meets it. 1.0 for an empty
    /// arrangement.</summary>
    public static double Estimate(Arrangement arrangement, IList<BeatMarker> beats)
    {
        ChartLevel? hardest = null;
        foreach (var level in arrangement.Levels)
        {
            if (hardest == null || level.DifficultyIndex >= hardest.DifficultyIndex)
                hardest = level;
        }

        return hardest == null ? 1.0 : Estimate(hardest, beats);
    }

    /// <summary>Rates one level. 1.0 when it has no sounds.</summary>
    public static double Estimate(ChartLevel level, IList<BeatMarker> beats)
    {
        var totalSounds = level.Notes.Count + level.Chords.Count;
        if (totalSounds == 0) return 1.0;

        // ---- one pass over the sounds ------------------------------------
        var firstMs = double.PositiveInfinity;
        var lastMs = double.NegativeInfinity;
        var techniqueSounds = 0;
        var techniqueKindsSeen = TechniqueFlags.None;

        foreach (var note in level.Notes)
        {
            var ms = note.GetTimeMs(beats);
            if (ms < firstMs) firstMs = ms;
            if (ms > lastMs) lastMs = ms;
            var kinds = KindsOf(note);
            if (kinds != TechniqueFlags.None) techniqueSounds++;
            techniqueKindsSeen |= kinds;
        }

        // Chords, sorted by time so shape transitions count deterministically
        // regardless of collection order.
        var chords = new List<(double Ms, int Signature)>(level.Chords.Count);
        foreach (var chord in level.Chords)
        {
            var ms = chord.GetTimeMs(beats);
            if (ms < firstMs) firstMs = ms;
            if (ms > lastMs) lastMs = ms;
            chords.Add((ms, ShapeSignature(chord)));

            var kinds = TechniqueFlags.None;
            foreach (var cn in chord.ChordNotes) kinds |= KindsOf(cn);
            if (kinds != TechniqueFlags.None) techniqueSounds++;
            techniqueKindsSeen |= kinds;
        }

        chords.Sort(static (a, b) =>
        {
            var byTime = a.Ms.CompareTo(b.Ms);
            return byTime != 0 ? byTime : a.Signature.CompareTo(b.Signature);
        });

        var shapeChanges = 0;
        for (var i = 1; i < chords.Count; i++)
        {
            if (chords[i].Signature != chords[i - 1].Signature)
                shapeChanges++;
        }

        // ---- factors ------------------------------------------------------
        var activeSeconds = Math.Max(1.0, (lastMs - firstMs) / 1000.0);

        var densityScore = Saturate(totalSounds / activeSeconds, DensityHalfPoint);

        var chordShare = level.Chords.Count / (double)totalSounds;
        var chordScore = 0.7 * Saturate(shapeChanges / activeSeconds, ChordChangeHalfPoint)
                         + 0.3 * chordShare;

        var techniqueFraction = techniqueSounds / (double)totalSounds;
        var techniqueDiversity = CountBits((int)techniqueKindsSeen) / TechniqueKinds;
        var techniqueScore = 0.7 * techniqueFraction + 0.3 * techniqueDiversity;

        var tempoScore = Saturate(Math.Max(0.0, AverageBpm(beats) - 60.0), TempoHalfPointBpmAbove60);

        var total = DensityWeight * densityScore
                    + ChordWeight * chordScore
                    + TechniqueWeight * techniqueScore
                    + TempoWeight * tempoScore;

        return Math.Clamp(1.0 + 9.0 * total, 1.0, 10.0);
    }

    /// <summary>Strictly monotonic 0..1 squash: 0 at 0, 0.5 at
    /// <paramref name="halfPoint"/>, asymptotically 1.</summary>
    private static double Saturate(double value, double halfPoint)
        => value <= 0 ? 0.0 : value / (value + halfPoint);

    private static double AverageBpm(IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return 120.0;
        var sum = 0.0;
        for (var i = 0; i < beats.Count; i++) sum += beats[i].Bpm;
        var avg = sum / beats.Count;
        return avg > 0 ? avg : 120.0;
    }

    [Flags]
    private enum TechniqueFlags
    {
        None = 0,
        Bend = 1 << 0,
        Slide = 1 << 1,
        Legato = 1 << 2,
        Harmonic = 1 << 3,
        Mute = 1 << 4,
        Tremolo = 1 << 5,
        Vibrato = 1 << 6,
        SlapOrPop = 1 << 7
    }

    private static TechniqueFlags KindsOf(IGuitarSound note)
    {
        var kinds = TechniqueFlags.None;
        if (note.BendValues.Count > 0) kinds |= TechniqueFlags.Bend;
        if (note.SlideTo is not null || note.UnpitchedSlide) kinds |= TechniqueFlags.Slide;
        if (note.Hopo != HopoType.None) kinds |= TechniqueFlags.Legato;
        if (note.Harmonic != HarmonicType.None) kinds |= TechniqueFlags.Harmonic;
        if (note.Mute != MuteType.None) kinds |= TechniqueFlags.Mute;
        if (note.Tremolo) kinds |= TechniqueFlags.Tremolo;
        if (note.Vibrato) kinds |= TechniqueFlags.Vibrato;
        if (note.BassPicking != BassPickingType.None) kinds |= TechniqueFlags.SlapOrPop;
        return kinds;
    }

    /// <summary>Order-independent fingering signature: the template id when
    /// templated, else a commutative-but-position-sensitive fold of the
    /// (string, fret) pairs. Two strums of the same shape share a signature.</summary>
    private static int ShapeSignature(Chord chord)
    {
        if (chord.TemplateId >= 0) return chord.TemplateId;
        var hash = int.MinValue; // disjoint from non-negative template ids in practice
        foreach (var cn in chord.ChordNotes)
            hash ^= (cn.StringIndex * 397) ^ (cn.Fret + 1) * 31;
        return hash;
    }

    private static int CountBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }

        return count;
    }
}
