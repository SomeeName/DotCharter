using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export.Sng;

/// <summary>
///     String limits of the internal slot-order SNG model. Its stock chord records
///     have six columns. Extra guitar columns and techniques live in Extension;
///     an extended bass uses all its stock slots. RsxrSngWriter combines and permutes
///     these fields into positional N-column records. A sound past the arrangement's
///     own string count is an export error; stock content keeps the stock ceiling.
/// </summary>
internal static class SngStringLimits
{
    /// <summary>The highest string index a CHORD record (and stock Rocksmith) can hold.</summary>
    public const int MaxStringIndex = 5;

    /// <summary>The highest package string index a SINGLE note of this
    /// arrangement may carry, and the SNG tuning's last index.</summary>
    public static int MaxNoteStringIndex(Arrangement arrangement) =>
        Rs7StringLayout.IsExtended(arrangement) ? arrangement.StringCount - 1 : MaxStringIndex;
}
