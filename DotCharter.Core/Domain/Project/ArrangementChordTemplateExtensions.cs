using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Domain.Project;

/// <summary>
///     The one chord-template-by-id lookup. Ids are not list positions, so there is
///     no index fallback: a dangling id is dangling.
/// </summary>
public static class ArrangementChordTemplateExtensions
{
    /// <summary>
    ///     The FIRST template whose <c>Id</c> equals <paramref name="templateId" />, or
    ///     null. A negative id (the untemplated <c>-1</c>) is never a match.
    /// </summary>
    public static ChordTemplate? FindChordTemplate(this Arrangement arrangement, int templateId)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (templateId < 0) return null;
        var templates = arrangement.ChordTemplates;
        for (var i = 0; i < templates.Count; i++)
        {
            if (templates[i].Id == templateId)
                return templates[i];
        }

        return null;
    }
}
