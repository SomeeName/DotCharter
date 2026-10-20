using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export.Sng;

/// <summary>
///     What the SNG container can spell per string. Stock Rocksmith 2014 is a
///     SIX-string game: every per-string chord structure is a fixed six-element
///     array (<c>Mask[6]</c>, <c>SlideTo[6]</c>, <c>SlideUnpitchTo[6]</c>,
///     <c>Vibrato[6]</c>, <c>Notes[6]</c>, the template's frets and fingers).
///     <para>
///         A 7-/8-string GUITAR exports through the <c>rs7string</c> layout
///         (<see cref="Rs7StringLayout" />): a single note may sit on package string 6
///         (and 7), the SNG tuning gets 7 (8) entries, and chord notes on those
///         strings leave the six-wide chord records for a sidecar
///         (<see cref="Rs7Projection" />). Everything else — bass, and a sound past
///         the arrangement's own strings — keeps the six-string ceiling, and
///         <c>ChartExportValidator.KeyStringOutOfRange</c> is an <c>Error</c> there, so
///         the mapper's clamps are a last resort, not a shipping path.
///     </para>
/// </summary>
internal static class SngStringLimits
{
    /// <summary>The highest string index a CHORD record (and stock Rocksmith) can hold.</summary>
    public const int MaxStringIndex = 5;

    /// <summary>The highest package string index a SINGLE note of this
    /// arrangement may carry, and the SNG tuning's last index.</summary>
    public static int MaxNoteStringIndex(Arrangement arrangement) =>
        Rs7StringLayout.IsExtendedGuitar(arrangement) ? arrangement.StringCount - 1 : MaxStringIndex;
}
