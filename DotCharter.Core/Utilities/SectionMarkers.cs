using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     The one answer to "which section is this position in?": the latest
///     <c>EventPoint</c> carrying a section name at or before the position. Linked-measure
///     creation, the link suggestions and the link panel all name a run by it, so they
///     cannot disagree.
/// </summary>
public static class SectionMarkers
{
    /// <summary>The section in effect at <paramref name="at" />, or "" before the first one.</summary>
    public static string At(Arrangement arrangement, FractionalPosition at)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return arrangement.EventPoints
            .Where(e => !string.IsNullOrEmpty(e.Section) && e.Position <= at)
            .MaxBy(e => e.Position)?.Section ?? "";
    }
}
