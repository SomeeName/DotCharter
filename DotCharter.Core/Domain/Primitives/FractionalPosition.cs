using System.Globalization;

namespace DotCharter.Core.Domain.Primitives;

public readonly struct FractionalPosition : IComparable<FractionalPosition>, IEquatable<FractionalPosition>
{
    public static readonly FractionalPosition Zero = new(0, Fraction.Zero);

    public int BeatId { get; }
    public Fraction Fraction { get; }

    public FractionalPosition(int beatId, Fraction fraction)
    {
        var fullBeats = fraction.Numerator / fraction.Denominator;
        var remainderNumerator = fraction.Numerator % fraction.Denominator;

        if (remainderNumerator < 0)
        {
            fullBeats--;
            remainderNumerator += fraction.Denominator;
        }

        BeatId = beatId + fullBeats;
        Fraction = new Fraction(remainderNumerator, fraction.Denominator);
    }

    public FractionalPosition(int beatId) : this(beatId, Fraction.Zero)
    {
    }

    public FractionalPosition(Fraction fraction) : this(0, fraction)
    {
    }

    /// <summary>
    ///     Inside the half-open span <c>[start, end)</c>: the start belongs to the
    ///     span and the end does not, so two spans that meet never both claim the
    ///     position where they meet. A null <paramref name="end" /> runs to the end
    ///     of the chart.
    /// </summary>
    public bool IsWithin(FractionalPosition start, FractionalPosition? end) => this >= start && (end == null || this < end.Value);

    public static bool TryParse(string? value, out FractionalPosition result)
    {
        result = Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var split = value.IndexOf(' ');
        if (split < 0 || split == value.Length - 1) return false;

        // INVARIANT, never the ambient culture — see ToString below.
        if (int.TryParse(value.Substring(0, split), CultureInfo.InvariantCulture, out var beatId))
        {
            if (Fraction.TryParse(value.Substring(split + 1), out var fraction))
            {
                result = new FractionalPosition(beatId, fraction);
                return true;
            }
        }

        return false;
    }

    public static FractionalPosition Parse(string value)
    {
        if (!TryParse(value, out var result))
            throw new FormatException($"String '{value}' was not recognized as a valid FractionalPosition.");
        return result;
    }

    public double DoubleValue => BeatId + Fraction.DoubleValue;

    public FractionalPosition Negate() => new(-BeatId, -Fraction);

    public FractionalPosition Absolute() => BeatId < 0 ? Negate() : this;

    public static FractionalPosition Max(FractionalPosition a, FractionalPosition b) => a > b ? a : b;

    public static FractionalPosition Min(FractionalPosition a, FractionalPosition b) => a < b ? a : b;

    public static FractionalPosition operator +(FractionalPosition a, FractionalPosition b)
    {
        return new FractionalPosition(a.BeatId + b.BeatId, a.Fraction + b.Fraction);
    }

    public static FractionalPosition operator +(FractionalPosition a, Fraction b)
    {
        return new FractionalPosition(a.BeatId, a.Fraction + b);
    }

    public static FractionalPosition operator +(FractionalPosition a, int b)
    {
        return new FractionalPosition(a.BeatId + b, a.Fraction);
    }

    public static FractionalPosition operator -(FractionalPosition a, FractionalPosition b)
    {
        return a + b.Negate();
    }

    public static FractionalPosition operator *(FractionalPosition a, int b)
    {
        return new FractionalPosition(a.BeatId * b, a.Fraction * b);
    }

    public static FractionalPosition operator *(FractionalPosition a, Fraction b)
    {
        return new FractionalPosition(a.Fraction * b + b * a.BeatId);
    }

    public static bool operator ==(FractionalPosition a, FractionalPosition b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(FractionalPosition a, FractionalPosition b)
    {
        return !a.Equals(b);
    }

    public static bool operator <(FractionalPosition a, FractionalPosition b)
    {
        return a.CompareTo(b) < 0;
    }

    public static bool operator >(FractionalPosition a, FractionalPosition b)
    {
        return a.CompareTo(b) > 0;
    }

    public static bool operator <=(FractionalPosition a, FractionalPosition b)
    {
        return a.CompareTo(b) <= 0;
    }

    public static bool operator >=(FractionalPosition a, FractionalPosition b)
    {
        return a.CompareTo(b) >= 0;
    }

    /// <summary>
    ///     <para>
    ///         INVARIANT. Every <c>p</c> and <c>ep</c> attribute
    ///         <c>RscpWriter</c> emits is this string, and <see cref="TryParse" />
    ///         is what reads it back — so it is a wire format, not a label.
    ///     </para>
    ///     <para>
    ///         Under the ambient culture a
    ///         NEGATIVE <see cref="BeatId" /> — a note left of the first beat —
    ///         would be written with U+2212 on a Swedish, Finnish, Norwegian or
    ///         Lithuanian machine and read back as
    ///         <see cref="Zero" /> on every other one, moving the note to beat 0
    ///         with nothing logged. <c>FormatWithMs</c> composes this string and
    ///         is therefore invariant in its bar/beat half; its millisecond half
    ///         still follows the ambient culture, deliberately, because that half
    ///         is only ever read by a human (see
    ///         <c>Core/FractionalPositionFormatTests</c>).
    ///     </para>
    /// </summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{BeatId} {Fraction}");

    public int CompareTo(FractionalPosition other)
    {
        var beatDifference = BeatId.CompareTo(other.BeatId);
        if (beatDifference != 0) return beatDifference;
        return Fraction.CompareTo(other.Fraction);
    }

    public bool Equals(FractionalPosition other) => BeatId == other.BeatId && Fraction.Equals(other.Fraction);

    public override bool Equals(object? obj) => obj is FractionalPosition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(BeatId, Fraction);

    public FractionalPosition Floor() => new(BeatId);

    public FractionalPosition Ceil() => new(Fraction.Numerator > 0 ? BeatId + 1 : BeatId);
}
