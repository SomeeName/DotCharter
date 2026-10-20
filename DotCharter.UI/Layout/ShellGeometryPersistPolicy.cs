namespace DotCharter.UI.Layout;

/// <summary>
///     What the shell's close path may write back to <c>AppConfig</c> as the
///     user's EDITOR geometry.
///     <para>
///         <b>The bug this exists to end.</b> The gameplay
///         presentation is an OVERRIDE, not a preference:
///         <c>MainWindowShellLayout.ApplyGameplayLayout</c> collapses the
///         timeline row and the tool dock so the 3D panel owns the window, and
///         <c>MainWindowViewModel.OnIsGameplayPresentationChanged</c> closes the
///         Inspector and the Sheet Music pane — all of it snapshotted into
///         fields and restored when the run ends. Quitting the app DURING a run
///         never reaches that restore: <c>MainWindow.OnClosing</c> goes straight
///         to <c>PersistDockLayout</c> and <c>PersistOnClose</c>, which measured
///         the presentation and stored it as the editor layout. One quit from
///         the pause menu came back to an editor with no Inspector, no notation
///         strip and a bottom dock at half the window — permanently, with no
///         undo.
///     </para>
///     <para>
///         <see cref="Views.Shell.SheetStripHeightPolicy" /> already ended the
///         same bug for the notation strip's height on the CLOSE-THE-STRIP path;
///         the close-the-APP path is the second writer it never covered. Same
///         rule, stated once: a run's geometry is saved and restored, never
///         absorbed.
///     </para>
///     Pure, so every arm is assertable without a Window.
/// </summary>
internal static class ShellGeometryPersistPolicy
{
    /// <summary>
    ///     The bottom-dock row height to persist, or null to leave the saved
    ///     value alone. During a run the row owns the whole window (row 0 is
    ///     <c>Auto</c>, the splitter row is 0), and <c>IsBottomDockVisible</c> is
    ///     forced true because the presentation turns the 3D preview on — so the
    ///     measurement is the highway's height, not a split the user chose.
    /// </summary>
    public static double? DockHeightToPersistPx(
        double measuredPx, bool bottomDockVisible, bool gameplayPresentation) =>
        gameplayPresentation || !bottomDockVisible || measuredPx <= 0 ? null : measuredPx;

    /// <summary>
    ///     The Inspector column width to persist, or null. The run sets that
    ///     column to a hard 0, which the <c>&gt; 0</c> guard already rejected —
    ///     but only by accident of the collapse being total. Stated as a rule so
    ///     a future presentation that merely NARROWS the column cannot smuggle
    ///     itself in as a preference.
    /// </summary>
    public static double? InspectorWidthToPersistPx(double measuredPx, bool gameplayPresentation) =>
        gameplayPresentation || measuredPx <= 0 ? null : measuredPx;

    /// <summary>
    ///     Whether the dock-pane SNAPSHOT (<c>DockLayoutState</c>: which panes
    ///     are open, floating, pinned) may be overwritten. False during a run:
    ///     the presentation closed the Inspector and the Sheet Music pane itself,
    ///     so capturing would record "the user closed them". The
    ///     persisted snapshot is the last one the user actually arranged, and
    ///     leaving it untouched is the whole fix.
    /// </summary>
    public static bool MayPersistDockPanes(bool gameplayPresentation) => !gameplayPresentation;
}
