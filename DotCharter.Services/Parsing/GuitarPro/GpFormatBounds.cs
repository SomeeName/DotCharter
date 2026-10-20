namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Bounds on the numbers a Guitar Pro file supplies that size a
///     <see cref="Fraction" />, a bar span or an emit loop.
///     <para>
///         None of them is ours — a <c>.gp</c> is a file a user is handed — and
///         each unbounded one has been a crash or a hang rather than a wrong
///         chart. They live here rather than on a parser because the SAME field
///         is read by several of them (<c>GpArrangementParser</c> and
///         <c>GpMetadataParser</c> both read <c>&lt;Time&gt;</c>; the gpif and
///         GP3-5 binary paths both read a tuplet ratio), and a per-parser copy is
///         exactly how one reader ends up clamping while its sibling does not.
///     </para>
/// </summary>
public static class GpFormatBounds
{
    /// <summary>
    ///     Bound on either side of a tuplet ratio — gpif's <c>PrimaryTuplet</c>
    ///     and the GP3-5 binary beat's <c>nTuplet</c>.
    ///     <para>
    ///         Guitar Pro authors 3/2, 5/4, 6/4, 7/4, 9/8 and the like; 64 is far
    ///         past every real one and keeps a rhythm's duration finite. On the
    ///         gpif side an unbounded ratio produced a note four million quarters
    ///         long, which <c>ExpandTrill</c> then turned into millions of emitted
    ///         notes. On the binary side <c>nTuplet</c> is a raw
    ///         <c>ReadInt32</c>, and a value at or above 2^30 made
    ///         <see cref="GpLegacyNoteEffectsHelpers.PreviousPowerOfTwo" /> spin
    ///         forever.
    ///     </para>
    /// </summary>
    public const int MaxTupletValue = 64;

    /// <summary>
    ///     Bound on a time signature's NUMERATOR (<c>&lt;Time&gt;n/d&lt;/Time&gt;</c>
    ///     in gpif, the measure header's two bytes in the binary formats).
    ///     <para>
    ///         It sizes the bar span AND drives an emit loop — <c>GpBeatMapBuilder</c>
    ///         allocates one <c>BeatMarker</c> (and runs a LINQ pass) per beat in the
    ///         bar — so <c>&lt;Time&gt;2000000000/4&lt;/Time&gt;</c> hung and then
    ///         ran out of memory. It also puts the bar end astronomically far away,
    ///         which <c>ExpandTrill</c> turns into that many emitted notes.
    ///     </para>
    /// </summary>
    public const int MaxBeatsInMeasure = 32;

    /// <summary>
    ///     Bound on a time signature's DENOMINATOR. <c>0</c> is the
    ///     <see cref="DivideByZeroException" /> out of
    ///     <c>new Fraction(x, 0)</c>; the ceiling keeps the bar span finite.
    /// </summary>
    public const int MaxNoteDenominator = 64;
}
