using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Pure geometry helpers for a sound's bend curve. Bend point positions are
///     ABSOLUTE <see cref="FractionalPosition" />s, so when a note's SUSTAIN
///     changes they must be moved with it — otherwise a shortened note leaves its
///     bend points ringing past the end (they render on the next sound), and a
///     lengthened note leaves the curve bunched at the start.
/// </summary>
public static class BendMath
{
    /// <summary>
    ///     Rescales <paramref name="bends" /> so each point keeps the same
    ///     PROPORTION of the sustain it had before, anchored at
    ///     <paramref name="notePosition" />. This preserves the bend SHAPE while
    ///     guaranteeing no point lands before the start or past the new end.
    ///     Mutates the bend positions in place (call it on the state you are about
    ///     to commit, never a shared/undo snapshot).
    ///     <para>
    ///     When the old sustain is zero (nothing to scale from) the points are
    ///     simply CLAMPED into <c>[notePosition, notePosition + newSustain]</c>.
    ///     A no-op when there are no bends or the sustain is unchanged.
    ///     </para>
    /// </summary>
    public static void RescaleToSustain(
        IReadOnlyList<BendValue> bends,
        FractionalPosition notePosition,
        FractionalPosition oldSustain,
        FractionalPosition newSustain)
    {
        if (bends.Count == 0 || oldSustain == newSustain) return;

        var newEnd = notePosition + newSustain;

        // No prior length to scale from ⇒ clamp into the new span.
        if (oldSustain.CompareTo(FractionalPosition.Zero) <= 0)
        {
            foreach (var b in bends) b.Position = Clamp(b.Position, notePosition, newEnd);
            return;
        }

        var ratio = AsFraction(newSustain) / AsFraction(oldSustain);
        foreach (var b in bends)
        {
            var scaled = notePosition + (b.Position - notePosition) * ratio;
            b.Position = Clamp(scaled, notePosition, newEnd);
        }
    }

    private static FractionalPosition Clamp(FractionalPosition value, FractionalPosition lo, FractionalPosition hi)
    {
        if (value.CompareTo(lo) < 0) return lo;
        if (value.CompareTo(hi) > 0) return hi;
        return value;
    }

    /// <summary>A <see cref="FractionalPosition" /> as a single reduced
    /// <see cref="Fraction" /> in quarter-note units (whole beats folded in).</summary>
    private static Fraction AsFraction(FractionalPosition p) => new Fraction(p.BeatId, 1) + p.Fraction;
}
