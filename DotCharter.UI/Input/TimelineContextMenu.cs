namespace DotCharter.UI.Input;

/// <summary>
///     One item of the timeline's right-click menu, as pure data: a resx key
///     (or a literal for language-free headers like fret numbers), the action,
///     and optional children for a submenu. Built by
///     <see cref="TimelineContextMenuBuilder" />, realized into Avalonia
///     <c>MenuItem</c>s by <c>ChartRenderer.ShowContextMenu</c> — the split is
///     what keeps the menu's CONTENT unit-testable without a visual tree.
/// </summary>
public sealed record TimelineMenuItem(
    string? LocKey,
    Action? Invoke = null,
    string? LiteralHeader = null,
    IReadOnlyList<TimelineMenuItem>? Children = null)
{
    public static readonly TimelineMenuItem Separator = new((string?)null);

    public bool IsSeparator => LocKey is null && LiteralHeader is null && Invoke is null && Children is null;
}

/// <summary>The verbs the timeline context menu can invoke — every one an
/// EXISTING command/service call supplied by the caller; the menu invents no
/// edit verbs of its own and therefore rides the same undo actions the
/// keybinds use.</summary>
public sealed record TimelineContextMenuActions(
    Action DeleteSelection,
    Action DuplicateSelection,
    Action OpenInInspector,
    Action<int> SetFret,
    Action AddHandShape,
    Action AddFhpAnchor,
    Action AddNoteHere,
    Action Paste,
    Action SelectAllInView,
    Action AddEventAtTime,
    Action ApplyDlcBuilderFixes);

/// <summary>
///     Builds the timeline context menu for the two right-click contexts:
///     a note/chord under the pointer (near-miss tolerant, the same
///     <c>ChartHitTester</c> pass every click uses — the menu targets what the
///     user visibly clicked) and empty highway. A bare right-click asks, because
///     deleting the note under the pointer or placing one over empty lanes with
///     no confirmation would be too easy to do by accident; those instant verbs live on
///     Alt+right-click (charting muscle memory).
/// </summary>
public static class TimelineContextMenuBuilder
{
    /// <summary>Frets 0..24 — the same clamp every fret verb uses.</summary>
    public const int MaxFret = 24;

    public static IReadOnlyList<TimelineMenuItem> ForSound(TimelineContextMenuActions a)
    {
        var frets = new TimelineMenuItem[MaxFret + 1];
        for (var f = 0; f <= MaxFret; f++)
        {
            var fret = f;
            frets[f] = new TimelineMenuItem(null, () => a.SetFret(fret),
                LiteralHeader: fret.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return
        [
            new TimelineMenuItem("Timeline_Ctx_Delete", a.DeleteSelection),
            new TimelineMenuItem("Timeline_Ctx_Duplicate", a.DuplicateSelection),
            new TimelineMenuItem("Timeline_Ctx_OpenInspector", a.OpenInInspector),
            new TimelineMenuItem("Timeline_Ctx_SetFret", Children: frets),
            TimelineMenuItem.Separator,
            new TimelineMenuItem("Timeline_Ctx_AddHandShape", a.AddHandShape),
            new TimelineMenuItem("Timeline_Ctx_AddFhpAnchor", a.AddFhpAnchor)
        ];
    }

    public static IReadOnlyList<TimelineMenuItem> ForEmptyHighway(TimelineContextMenuActions a) =>
    [
        new TimelineMenuItem("Timeline_Ctx_AddNoteHere", a.AddNoteHere),
        new TimelineMenuItem("Timeline_Ctx_Paste", a.Paste),
        new TimelineMenuItem("Timeline_Ctx_SelectAllInView", a.SelectAllInView),
        TimelineMenuItem.Separator,
        new TimelineMenuItem("Timeline_Ctx_AddEventAtTime", a.AddEventAtTime),
        TimelineMenuItem.Separator,
        // The one way DLC Builder's fixes and generated levels get INTO the chart (every export
        // applies them to its copy anyway): asked for explicitly, one undo step.
        new TimelineMenuItem("Timeline_Ctx_ApplyDlcBuilderFixes", a.ApplyDlcBuilderFixes)
    ];
}
