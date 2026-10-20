using Avalonia;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.UI.Controls;

public interface IChartViewport
{
    double ViewportWidth { get; }
    double ViewportHeight { get; }

    /// <summary>Effective top margin (event-label band) in pixels. Stacked
    /// override rows shrink this when rows like the lyric band are hidden.</summary>
    double TopMargin { get; }

    /// <summary>Effective bottom margin (FHP / hand-shape band) in pixels.</summary>
    double BottomMargin { get; }

    /// <summary>v0.34 lefty flip: the horizontal fret-axis mirror; in the 2D
    /// lanes it also flips the vertical string order (composed with
    /// <see cref="InvertStrings"/> via <see cref="FlipStrings"/>).</summary>
    bool LeftyFlip { get; }

    /// <summary>Inverted string layout (tab convention): a vertical string flip
    /// that composes with <see cref="LeftyFlip"/>.</summary>
    bool InvertStrings { get; }

    /// <summary>THE effective vertical string flip hit-testing must use — lefty
    /// and inverted-strings compose (XOR). The renderer applies the identical
    /// combination in <c>ChartRenderContext.LaneTopFor</c>, so clicks and gems
    /// always agree.</summary>
    bool FlipStrings => LeftyFlip ^ InvertStrings;

    /// <summary>Composable top-ribbon layout for this viewport (per-track row visibility).</summary>
    RibbonLayout TopRibbon { get; }

    // Abstracting out Avalonia's rendering invalidation method
    void RequestNextFrameRendering();

    /// <summary>
    ///     Shows (or hides with null) the marquee-selection rubber band over
    ///     this viewport, in viewport pixel coordinates. Rendered by a
    ///     lightweight Avalonia overlay (<see cref="MarqueeSelectionOverlay" />)
    ///     — never by the GL scene, so pointer moves don't force GL redraws.
    /// </summary>
    void SetMarquee(Rect? marquee);

    /// <summary>
    ///     Opens the timeline context menu at the pointer. Items come from the
    ///     pure <see cref="DotCharter.UI.Input.TimelineContextMenuBuilder" />;
    ///     the viewport only realizes them (a popup is its own HWND, so it
    ///     renders fine over the native 3D panel). On the interface so every
    ///     viewport — the main timeline and each stacked row — presents its own.
    /// </summary>
    void ShowContextMenu(IReadOnlyList<DotCharter.UI.Input.TimelineMenuItem> items);
}
