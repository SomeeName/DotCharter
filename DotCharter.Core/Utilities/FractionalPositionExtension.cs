using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Utilities;

public static class FractionalPositionExtensions
{
    /// <summary>
    ///     Extension method to seamlessly bridge legacy FractionalPosition time lookups
    ///     without violating SRP in the struct itself.
    /// </summary>
    public static double GetPositionMs(this FractionalPosition pos, IList<BeatMarker> beats) => TimelineMath.CalculateTimeMs(pos, beats);

    /// <summary>
    ///     <para>
    ///         The ONE human-readable rendering of a position for an undo label,
    ///         a log line or a dialog caption: <c>3.2.000 (1234.567 ms)</c> — the
    ///         bar/beat form the charter reads, and the resolved millisecond it
    ///         actually lands on, which is what makes two nearby edits
    ///         distinguishable in an undo list.
    ///     </para>
    ///     <para>
    ///         This is the single home of an interpolation that eight files would otherwise each carry privately,
    ///         differing only in where the beat list came from. It is
    ///         DESCRIPTIVE text, never parsed and never on a hot path — the call
    ///         sites are all discrete user gestures. Pinned by
    ///         <c>Core/FractionalPositionFormatTests</c>, which also records the
    ///         one thing NOT decided here: the MILLISECOND half follows the
    ///         ambient culture, so a German-locale run writes <c>1234,567 ms</c>.
    ///         That is what all eight copies did, and it is left alone
    ///         deliberately rather than changed under cover of a refactor.
    ///     </para>
    ///     <para>
    ///         The bar/beat half is NOT ambient:
    ///         <see cref="FractionalPosition.ToString" /> is the text
    ///         of a `.rscp` <c>p</c> attribute as well as of this label, so it is
    ///         invariant at the source. See that method for what the ambient
    ///         reading cost.
    ///     </para>
    /// </summary>
    public static string FormatWithMs(this FractionalPosition pos, IList<BeatMarker> beats) =>
        $"{pos} ({pos.GetPositionMs(beats):0.###} ms)";
}
