namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The words around the inspector's problem COUNTS, so each one says what
/// it counts.
///     <para>
///         The pane shows three numbers that cannot be reconciled by reading
///         them: the Issues tab badge counts lint FINDINGS over every level of
///         the arrangement, the Sound tab's "needs work" tile counts SOUNDS on
///         the level being edited whose findings would stop the export, and the
///         Issues tab's export readiness is a separate validator that has not
///         run until asked. The tile must not read "190 export blockers", which
///         is both the wrong unit and a verdict only the export check can give.
///         None of these strings uses an <c>Inspector_Export_*</c> key: the
///         export verdict has one home, the Issues tab.
///     </para>
/// </summary>
public static class ProblemCountText
{
    /// <summary>The tile's tooltip: how many sounds, how many findings, where
    /// they came from, and where the real export verdict lives.</summary>
    public static string BlockingSoundsTip(ILocalizationService localization, int sounds, int findings) =>
        localization.Format("Inspector_Empty_Tile_BlockersTipFmt", sounds, findings);

    /// <summary>The Issues tab badge's tooltip: what its number counts.</summary>
    public static string IssueBadgeTip(ILocalizationService localization, int findings) =>
        localization.Format("Inspector_Issues_BadgeTipFmt", findings);
}
