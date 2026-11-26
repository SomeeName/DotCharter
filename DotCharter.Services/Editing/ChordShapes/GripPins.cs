using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     "Pin all grips": every chord shape of an arrangement that has a fingering and is not pinned yet, pinned in
///     ONE undo step through <see cref="SetTemplatePinnedAction" />. The pin is a template flag, so nothing else
///     about a shape changes. A fingerless shape has no fingering to keep and a ghost grip is pinned already.
/// </summary>
public static class GripPins
{
    public const string HistoryLabelKey = "History_PinAllGrips";

    /// <summary>True when "Pin all grips" would pin <paramref name="template" />.</summary>
    public static bool IsUnpinnedFingered(ChordTemplate template) =>
        !template.IsPinned && !template.Fingerless && template.Fingers.Count > 0;

    /// <summary>The shapes "Pin all grips" pins, in the arrangement's order.</summary>
    public static IReadOnlyList<ChordTemplate> Unpinned(Arrangement arrangement) =>
        [.. arrangement.ChordTemplates.Where(IsUnpinnedFingered)];

    /// <summary>The one undo step that pins them all, or null when none is left.</summary>
    public static IChartAction? PinAll(Arrangement arrangement)
    {
        var templates = Unpinned(arrangement);
        return templates.Count == 0
            ? null
            : new CompositeAction(templates.Select(t => new SetTemplatePinnedAction(t, true))) { LabelKey = HistoryLabelKey };
    }
}
