using DotCharter.Presentation.Contracts.Shell;

namespace DotCharter.Presentation.Shared;

public enum ShellContext
{
    Charting,
    Playing
}

/// <summary>
///     Which chrome a top-level surface wears (wave 5): Play Mode and a run are full-bleed, the chart
///     editor carries the rail and title bar, and Home, the Library and Settings take the context they
///     were reached from — one set of view models, two presentations.
/// </summary>
public static class ShellPresentation
{
    public static ShellContext ContextAfter(ShellDestination surface, ShellContext current, bool inRun) =>
        inRun || surface == ShellDestination.Play ? ShellContext.Playing
        : surface == ShellDestination.Chart ? ShellContext.Charting
        : current;

    public static bool HasChrome(ShellDestination surface, ShellContext context, bool inRun) =>
        !inRun && surface switch
        {
            ShellDestination.Home or ShellDestination.Play => false,
            ShellDestination.Chart => true,
            _ => context == ShellContext.Charting
        };

    /// <summary>A full-bleed Library or Settings has no rail to leave by, so it gets a back bar.</summary>
    public static bool NeedsBackBar(ShellDestination surface, ShellContext context, bool inRun) =>
        IsShared(surface) && !HasChrome(surface, context, inRun) && !inRun;

    public static bool IsShared(ShellDestination surface) => surface is ShellDestination.Library or ShellDestination.Settings;

    /// <summary>A full-bleed surface in a window keeps a slim caption with the window buttons; fullscreen does not.</summary>
    public static bool ShowsWindowCaption(bool hasChrome, bool needsBackBar, bool fullScreen) =>
        !hasChrome && !needsBackBar && !fullScreen;

    /// <summary>The top row's height in DIPs: the title bar, the back bar, the window caption, or nothing.</summary>
    public static double TopRowHeight(bool hasChrome, bool needsBackBar, bool fullScreen) =>
        hasChrome ? TitleBarHeight
        : needsBackBar ? BackBarHeight
        : ShowsWindowCaption(hasChrome, needsBackBar, fullScreen) ? WindowCaptionHeight
        : 0;

    public const double TitleBarHeight = 38;

    public const double BackBarHeight = 44;

    public const double WindowCaptionHeight = 32;
}
