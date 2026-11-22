using System.Numerics;
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
    // The fallback grid is a multiple of the onset denominator, so subsequent
    // relative-position subtraction and integer-beat dragging remain representable.
    private const int PositionGrid = 1000000;

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

        foreach (var bend in bends) bend.Position = ScalePosition(bend.Position, notePosition, oldSustain, newSustain);
    }

    private static FractionalPosition ScalePosition(FractionalPosition point, FractionalPosition start,
        FractionalPosition oldSustain, FractionalPosition newSustain)
    {
        var startDenominator = start.Fraction.Denominator;
        var denominator = (BigInteger)point.Fraction.Denominator * startDenominator;
        var numerator = ((BigInteger)point.BeatId - start.BeatId) * denominator +
                        (BigInteger)point.Fraction.Numerator * startDenominator -
                        (BigInteger)start.Fraction.Numerator * point.Fraction.Denominator;
        var newNumerator = NumeratorOf(newSustain);
        var newDenominator = newSustain.Fraction.Denominator;
        if (oldSustain > FractionalPosition.Zero)
        {
            numerator *= newNumerator * oldSustain.Fraction.Denominator;
            denominator *= newDenominator * NumeratorOf(oldSustain);
        }

        if (numerator <= 0 || newNumerator <= 0) return start;
        if (numerator * newDenominator > newNumerator * denominator)
        {
            numerator = newNumerator;
            denominator = newDenominator;
        }

        var absoluteNumerator = NumeratorOf(start) * denominator + numerator * startDenominator;
        if (TryPosition(absoluteNumerator, startDenominator * denominator, out var exact) &&
            TryPosition(numerator, denominator, out _))
        {
            return exact;
        }

        var grid = Math.Max(1, PositionGrid / startDenominator) * startDenominator;
        var ticks = BigInteger.DivRem(numerator * grid, denominator, out var remainder);
        if (remainder * 2 > denominator || (remainder * 2 == denominator && !ticks.IsEven)) ticks++;
        ticks = BigInteger.Min(ticks, newNumerator * grid / newDenominator);
        var startTicks = NumeratorOf(start) * (grid / startDenominator);
        return TryPosition(startTicks + ticks, grid, out var sampled) ? sampled : start;
    }

    private static BigInteger NumeratorOf(FractionalPosition position) =>
        (BigInteger)position.BeatId * position.Fraction.Denominator + position.Fraction.Numerator;

    private static bool TryPosition(BigInteger numerator, BigInteger denominator, out FractionalPosition position)
    {
        position = FractionalPosition.Zero;
        var beat = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (remainder < 0)
        {
            beat--;
            remainder += denominator;
        }

        var gcd = BigInteger.GreatestCommonDivisor(remainder, denominator);
        remainder /= gcd;
        denominator /= gcd;
        if (beat < int.MinValue || beat > int.MaxValue || denominator > int.MaxValue) return false;
        position = new FractionalPosition((int)beat, new Fraction((int)remainder, (int)denominator));
        return true;
    }
}
