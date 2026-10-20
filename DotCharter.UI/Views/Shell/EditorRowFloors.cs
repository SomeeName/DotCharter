namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The minimum heights of the chart editor's two stacked areas — the top area (sheet strip,
///     timeline, song map: row 0 of <c>MainSplitGrid</c>) and the bottom dock (Inspector + 3D
///     preview: row 2) — for the height the split actually has.
///     <para>
/// <b>The bug this ends.</b> The top area's floor was
///         <see cref="TimelineAreaMinHeight" /> (412 px with the song map) and the dock's a flat
///         80, so on a short window the timeline won outright and the dock got the remainder: 190 px
///         at 1280x720, where the Inspector spends ~200 px on its pane header, tab strip, selection
///         header, work-queue bar and footer, and its body measured 0 px (9 px at 1280x800). The
///         default 2:1 split does the same thing on any window under ~1000 px.
///     </para>
///     <para>
///         <b>The priority.</b> The dock is guaranteed <see cref="DockFloorPx" /> — enough for the
///         Inspector to show a body — and the timeline row gives way to pay for it, from its
///         comfortable 360 px down to <see cref="TimelineCompactPx" /> (the lanes get shorter; the
///         ribbon and the bottom bands keep their size). Only when even that does not fit does the
///         dock shrink again, never under its old 80. On a tall window nothing binds: at 1920x1080
///         and 2560x1440 the default split already gives the dock 321 / 316 px, so the pixels there
///         are unchanged. A splitter drag cannot take the dock under the floor — that strip
///         was an Inspector with no body.
///     </para>
///     Pure, so the rule is unit-tested (<c>UI/EditorRowFloorsTests</c>); applied by
///     <c>MainWindowShellLayout.ApplyEditorRowFloors</c> on every size change of the split.
/// </summary>
internal readonly record struct EditorRowFloors(double TopAreaMinPx, double TimelineRowMinPx, double DockMinPx)
{
    /// <summary>The dock row's floor on a window too short for anything better, and the floor it
    /// had everywhere before the adaptive floors (<c>MainWindow.axaml</c>).</summary>
    public const double DockLegacyMinPx = 80;

    /// <summary>What the dock is guaranteed while the timeline can pay for it: the Inspector's
    /// chrome (~200 px at a 1280 px window, ~180 at 1920) plus a body of a few rows.</summary>
    public const double DockFloorPx = 300;

    /// <summary>The lowest the timeline row goes to pay for the dock: the default four ribbon rows
    /// (~96 px) and the SHAPES/FHP/TIME bands (~60) keep their size, and six string lanes get
    /// ~19 px each instead of ~34.</summary>
    public const double TimelineCompactPx = 270;

    /// <summary>The splitter row between the two areas (<c>MainSplitGrid</c> row 1).</summary>
    public const double SplitterPx = 6;

    /// <summary>
    ///     The floors for a split <paramref name="splitHeightPx" /> tall.
    ///     <paramref name="comfortableTopAreaPx" /> is <see cref="TimelineAreaMinHeight.For" />
    ///     (the timeline at its full 360 px plus the song map and the open sheet strip).
    /// </summary>
    public static EditorRowFloors For(double splitHeightPx, double comfortableTopAreaPx, bool dockVisible)
    {
        const double timelineFull = TimelineAreaMinHeight.TimelineMinPx;
        if (!dockVisible)
            return new EditorRowFloors(comfortableTopAreaPx, timelineFull, 0);

        // Not measured yet (the first pass), or nonsense: the markup's own values.
        if (!double.IsFinite(splitHeightPx) || splitHeightPx <= 0)
            return new EditorRowFloors(comfortableTopAreaPx, timelineFull, DockLegacyMinPx);

        var shared = splitHeightPx - SplitterPx;
        var shortfall = DockFloorPx - (shared - comfortableTopAreaPx);
        if (shortfall <= 0)
            return new EditorRowFloors(comfortableTopAreaPx, timelineFull, DockFloorPx);

        // The timeline row gives way first, down to its compact floor…
        var timelineRow = Math.Max(TimelineCompactPx, timelineFull - shortfall);
        var topArea = comfortableTopAreaPx - (timelineFull - timelineRow);

        // …and the dock takes what that frees, never more than its floor, never under the old 80.
        var dock = Math.Clamp(shared - topArea, DockLegacyMinPx, DockFloorPx);
        return new EditorRowFloors(topArea, timelineRow, dock);
    }
}
