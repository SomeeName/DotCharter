using System.Globalization;

namespace DotCharter.Core.Domain.Primitives;

public readonly struct Fraction : IComparable<Fraction>, IEquatable<Fraction>
{
    public static readonly Fraction Zero = new(0, 1);
    public static readonly Fraction One = new(1, 1);

    public int Numerator { get; }

    /// <summary>
    ///     Always positive, and always at least 1.
    ///     <para>
    ///         Reads through a backing field so <c>default(Fraction)</c> is a
    ///         well-formed 0/1 rather than 0/0. As a plain auto-property it was
    ///         0 for the default value — which the CONSTRUCTOR forbids but
    ///         <c>default</c> bypasses — and that leaked everywhere: adding to a
    ///         <c>default(FractionalPosition)</c> threw
    ///         <see cref="DivideByZeroException" />, <see cref="DoubleValue" />
    ///         was NaN, and <see cref="CompareTo" /> reported "equal to
    ///         everything" while <see cref="Equals(Fraction)" /> disagreed — so
    ///         a default-valued struct sorted randomly and deduped wrongly.
    ///         <c>GpLegacyImporter</c> already hand-defended against this, which
    ///         is how we know it was reachable in practice.
    ///     </para>
    /// </summary>
    public int Denominator => _denominator == 0 ? 1 : _denominator;

    private readonly int _denominator;

    /// <summary>
    ///     Takes <see cref="long" />s — <c>new Fraction(1, 4)</c> still compiles
    ///     unchanged — and that is the reason the operators below multiply in
    ///     <see cref="long" /> too.
    ///     <para>
    ///         The range check at the end only ever saw values that had ALREADY
    ///         been through int arithmetic: <c>operator+</c> computed
    ///         <c>a.Denominator * b.Denominator</c> in unchecked int and handed the
    ///         wrapped result in, so the widening happened one step too late to
    ///         see the overflow. <c>536870912 * 8</c> is 2^32, which wraps to
    ///         <b>0</b> — so adding <c>1/536870912</c> to <c>1/8</c> threw
    ///         <see cref="DivideByZeroException" /> out of a fraction with no zero
    ///         anywhere in it, and a product that wrapped to a merely-wrong value
    ///         produced a silently wrong position instead. Both are reachable from
    ///         a single <c>.rscp</c> attribute, which arrives from disk AND over
    ///         the unauthenticated LAN session transfer.
    ///     </para>
    ///     <para>
    ///         Widening cannot itself overflow: numerators and denominators are
    ///         ints, so every product fits a long with room to spare, and the one
    ///         SUM (<c>operator+</c>) of two such products is bounded by
    ///         <c>2 * (2^31-1)^2 &lt; long.MaxValue</c>.
    ///     </para>
    /// </summary>
    public Fraction(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException("Denominator cannot be zero.");

        if (!TryReduce(numerator, denominator, out var n, out var d))
        {
            throw new ArgumentOutOfRangeException(nameof(numerator),
                $"The reduced fraction {n}/{d} does not fit in a Fraction. " +
                "Musical positions never approach this; it means an arithmetic overflow upstream.");
        }

        Numerator = (int)n;
        _denominator = (int)d;
    }

    /// <summary>
    ///     The non-throwing counterpart of the constructor: <c>false</c> for a
    ///     zero denominator or a reduced value that does not fit, instead of
    ///     <see cref="DivideByZeroException" /> / <see cref="ArgumentOutOfRangeException" />.
    ///     <para>
    ///         This exists because <see cref="TryParse" /> must honour the
    ///         <c>Try*</c> contract — <b>never throw</b>. Constructing
    ///         directly would let <c>"1/-2147483648"</c> and <c>"-2147483648/-1"</c>
    ///         (both of which reduce out of int range) throw straight out of a
    ///         <c>bool</c>-returning method. Those strings are reachable from a
    ///         hostile <c>.rscp</c> <c>p=</c> attribute — off disk AND over the
    ///         unauthenticated LAN session transfer — and aborted the whole load.
    ///     </para>
    /// </summary>
    public static bool TryCreate(long numerator, long denominator, out Fraction result)
    {
        result = Zero;
        if (denominator == 0) return false;
        if (!TryReduce(numerator, denominator, out var n, out var d)) return false;

        // Already reduced and in range, so this construction cannot throw.
        result = new Fraction(n, d);
        return true;
    }

    /// <summary>Reduces to the canonical positive-denominator form and reports
    /// whether the result fits in two <see cref="int" />s. <paramref name="n" />
    /// / <paramref name="d" /> are written either way so the caller can name the
    /// offending value.</summary>
    private static bool TryReduce(long numerator, long denominator, out long n, out long d)
    {
        // long throughout: negating or Math.Abs-ing int.MinValue overflows.
        // That threw OverflowException for a MinValue numerator and — worse —
        // silently left a NEGATIVE denominator for a MinValue denominator,
        // breaking the always-positive invariant every comparison depends on.
        n = numerator;
        d = denominator;
        if (d < 0)
        {
            n = -n;
            d = -d;
        }

        var gcd = GetGcd(Math.Abs(n), d);
        n /= gcd;
        d /= gcd;

        return n is >= int.MinValue and <= int.MaxValue && d <= int.MaxValue;
    }

    private static long GetGcd(long a, long b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }

        return a == 0 ? 1 : a;
    }

    public static bool TryParse(string? value, out Fraction result)
    {
        result = Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('/');
        if (parts.Length != 2) return false;

        // TryCreate, not the constructor: `1/-2147483648` and `-2147483648/-1`
        // parse as ints but do not FIT once reduced, and throwing out of a
        // Try* method aborted an otherwise-loadable .rscp.
        //
        // INVARIANT, never the ambient culture: this text goes to and comes from
        // a file. See ToString below for what a hyphen costs under sv-SE.
        if (int.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var num) &&
            int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var den))
        {
            return TryCreate(num, den, out result);
        }

        return false;
    }

    public static Fraction Parse(string value)
    {
        if (!TryParse(value, out var result))
            throw new FormatException($"String '{value}' was not recognized as a valid Fraction.");
        return result;
    }

    public double DoubleValue => (double)Numerator / Denominator;
    public int IntValue => Numerator / Denominator;
    public bool IsNegative => Numerator < 0;

    public Fraction Negate() => new(-Numerator, Denominator);

    public Fraction Absolute() => IsNegative ? Negate() : this;

    public static implicit operator Fraction(int value)
    {
        return new Fraction(value, 1);
    }

    public static Fraction operator +(Fraction a, Fraction b)
    {
        return new Fraction(
            (long)a.Numerator * b.Denominator + (long)b.Numerator * a.Denominator,
            (long)a.Denominator * b.Denominator);
    }

    public static Fraction operator -(Fraction a, Fraction b)
    {
        return a + b.Negate();
    }

    public static Fraction operator *(Fraction a, Fraction b)
    {
        return new Fraction((long)a.Numerator * b.Numerator, (long)a.Denominator * b.Denominator);
    }

    public static Fraction operator *(Fraction a, int b)
    {
        return new Fraction((long)a.Numerator * b, a.Denominator);
    }

    public static Fraction operator *(int a, Fraction b)
    {
        return b * a;
    }

    public static Fraction operator /(Fraction a, Fraction b)
    {
        return new Fraction((long)a.Numerator * b.Denominator, (long)a.Denominator * b.Numerator);
    }

    public static Fraction operator -(Fraction a)
    {
        return a.Negate();
    }

    public static bool operator ==(Fraction a, Fraction b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Fraction a, Fraction b)
    {
        return !a.Equals(b);
    }

    public static bool operator <(Fraction a, Fraction b)
    {
        return a.CompareTo(b) < 0;
    }

    public static bool operator >(Fraction a, Fraction b)
    {
        return a.CompareTo(b) > 0;
    }

    public static bool operator <=(Fraction a, Fraction b)
    {
        return a.CompareTo(b) <= 0;
    }

    public static bool operator >=(Fraction a, Fraction b)
    {
        return a.CompareTo(b) >= 0;
    }

    public static Fraction Max(Fraction a, Fraction b) => a > b ? a : b;

    public static Fraction Min(Fraction a, Fraction b) => a < b ? a : b;

    public int CompareTo(Fraction other)
    {
        var thisAdjusted = (long)Numerator * other.Denominator;
        var otherAdjusted = (long)other.Numerator * Denominator;
        return thisAdjusted.CompareTo(otherAdjusted);
    }

    public bool Equals(Fraction other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Fraction other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    /// <summary>
    ///     <para>
    ///         INVARIANT. This is not a display string — it is the text of a
    ///         <c>p</c> / <c>ep</c> attribute in a `.rscp` and of every
    ///         <see cref="FractionalPosition" /> written beside it, so it must
    ///         read the same on every machine.
    ///     </para>
    ///     <para>
    ///         It follows the invariant culture, not the ambient one, and the part
    ///         that matters is the SIGN, not the separator: sv-SE, fi-FI, nb-NO,
    ///         lt-LT and sv-FI render a minus as U+2212 under their own culture, which
    ///         <see cref="TryParse" /> then rejects everywhere else — and
    ///         <c>RscpParsingUtilities.ParseFraction</c> answers a rejection
    ///         with <see cref="FractionalPosition.Zero" />, silently.
    ///     </para>
    /// </summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
}
