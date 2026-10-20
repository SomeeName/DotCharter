namespace DotCharter.UI.Layout;

/// <summary>
///     Which of the editor's two ROW splitters is on screen. Outside a run: the dock splitter
///     (<c>DockRowSplitter</c>, timeline area / bottom dock) while the dock shows, and the
///     sheet splitter (sheet strip / timeline) while the sheet strip shows.
///     <para>
///         During the gameplay presentation the timeline area is collapsed and the run owns the
///         dock row, so the ONLY thing that can sit above the highway is the notation band the
///         HUD toggles. Its seam is then the DOCK splitter — the one that trades height between
///         the band and the run — and the sheet splitter stays hidden, because the row it resizes
///         against (the timeline) is 0 px. With the band off there is nothing above the run and
///         no splitter at all — a splitter there sits over an empty band the user can drag.
///     </para>
/// </summary>
public static class EditorRowSplitterPolicy
{
    /// <summary>The dock splitter (<c>MainSplitGrid</c> row 1).</summary>
    public static bool DockSplitterVisible(bool bottomDockVisible, bool gameplayPresentation, bool sheetVisible) =>
        bottomDockVisible && (!gameplayPresentation || sheetVisible);

    /// <summary>The sheet splitter (<c>SheetSplitGrid</c> row 1).</summary>
    public static bool SheetSplitterVisible(bool sheetVisible, bool gameplayPresentation) =>
        sheetVisible && !gameplayPresentation;
}
