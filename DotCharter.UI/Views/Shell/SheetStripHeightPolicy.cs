namespace DotCharter.UI.Views.Shell;

/// <summary>
///     The sheet-music strip's pixel height: what it OPENS at, and what may be
///     written back to <c>AppConfig.LayoutSheetHeight</c> when it closes.
///     <para>
///         <b>The bug this exists to end.</b> A game run is allowed
///         to keep the notation band, but at a compact fixed height — the
///         persisted editor height is sized for reading full scores, not for an
///         in-run tab strip — so <see cref="OpenHeightPx" /> caps it at
///         <see cref="GameplayMaxPx" />. That cap is a PRESENTATION override, and
///         the close path did not know it: hiding the strip during a run read the
///         row's <c>ActualHeight</c> (now the clamped ≤240) and wrote it to the
///         config as the user's editor preference. A 480 px notation strip came
///         back from one run permanently 240 px, silently, with no undo — the
///         same shape as every other gameplay override that has to be SAVED and
///         RESTORED rather than absorbed (<c>ApplyGameplayLayout</c>'s row
///         heights, the pre-run panel snapshot, the minimap preference).
///     </para>
///     Pure, so the rule is unit-tested rather than reasoned about from a Grid.
/// </summary>
internal static class SheetStripHeightPolicy
{
    /// <summary>Below this the strip is not a usable score view, so a saved
    /// value under it is ignored and a measured one is never written back.</summary>
    public const double MinPx = 120;

    /// <summary>What an unset (or nonsense) saved height opens at.</summary>
    public const double DefaultPx = 320;

    /// <summary>The in-run cap: a compact band above the highway, not a
    /// reading view.</summary>
    public const double GameplayMaxPx = 240;

    /// <summary>The pixel height the strip should open at.</summary>
    public static double OpenHeightPx(double? savedPx, bool gameplayPresentation)
    {
        var height = savedPx is { } saved && saved >= MinPx ? saved : DefaultPx;
        return gameplayPresentation ? Math.Min(height, GameplayMaxPx) : height;
    }

    /// <summary>
    ///     The height to persist as the user's preference when the strip closes,
    ///     or null to leave the saved value alone. During a game run the measured
    ///     height is the presentation CAP, not a preference, so nothing is
    ///     written.
    /// </summary>
    public static double? HeightToPersistPx(double measuredPx, bool gameplayPresentation)
        => gameplayPresentation || measuredPx < MinPx ? null : measuredPx;
}
