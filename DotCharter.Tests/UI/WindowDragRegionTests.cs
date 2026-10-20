using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The shell asks for <c>WindowDecorations="BorderOnly"</c>, so Windows draws
///     no caption: the ONLY gesture that moves the window is a left press on
///     <c>TitleBarDragArea</c>, which calls <c>BeginMoveDrag</c>. That makes the
///     strip window CHROME, and a full-window overlay that layers over it takes
///     the whole gesture away — silently, because the overlay itself works
///     perfectly.
///     <para>
///         v0.48.5 shipped it: the chart editor's Amp &amp; FX rig overlay was a
///         child of <c>WindowRootGrid</c> at <c>Grid.Row="0" Grid.RowSpan="2"</c>,
///         which spans the title-bar row, and the rig paints the fully opaque
///         <c>FireOverlayFill</c>. Opening the rig pinned the window to the
///         desktop — the owner: "almost like it's fullscreen but it isn't."
///     </para>
///     <para>
///         <c>Perf/TitleBarDragUiPerfSmokeTests</c> is the real proof (it
///         hit-tests the point with the rig open), but that suite is outside the
///         commit gate and only blocks at release time. This is the cheap
///         structural half, in the DEFAULT gate: it reads the markup, so it costs
///         nothing and catches the reshuffle that reintroduces the bug on the day
///         it lands.
///     </para>
/// </summary>
public sealed class WindowDragRegionTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheTitleBar_KeepsAHitTestableDragRegion_BoundToBeginMoveDrag()
    {
        var window = MainWindowMarkup();
        var drag = Named(window, "TitleBarDragArea");
        Assert.True(drag is not null,
            "MainWindow.axaml has no `TitleBarDragArea`. With BorderOnly decorations that strip is the "
            + "only way to move the window; without it the window cannot be moved at all.");

        Assert.Equal("TitleBar_PointerPressed", (string?)drag!.Attribute("PointerPressed"));

        // Transparent, not unset: a Panel/Border with no Background is not
        // hit-testable at all, which is the same bug from the other direction.
        Assert.Equal("Transparent", (string?)drag.Attribute("Background"));

        Assert.Contains(drag.Ancestors(), a => (string?)a.Attribute(X + "Name") == "TitleBarRow");
    }

    /// <summary>
    ///     The title bar is one of THREE rows that take its place (#654: all three
    ///     live in <c>WindowChromeView</c>): a full-bleed Library or Settings has the
    ///     back bar, Home and Play Mode the window caption. Each must carry a strip
    ///     that moves the window and double-click-maximizes it, or that surface
    ///     opens pinned to the desktop.
    /// </summary>
    [Theory]
    [InlineData("TitleBarRow", "TitleBarDragArea")]
    [InlineData("BackBar", "BackBarDragArea")]
    [InlineData("WindowCaption", "WindowCaptionDragArea")]
    public void EveryChromeRow_KeepsAStripThatMovesTheWindow(string row, string strip)
    {
        var window = MainWindowMarkup();
        var drag = Named(window, strip);
        Assert.True(drag is not null, $"the window chrome has no `{strip}` — {row} cannot move the window.");
        Assert.Contains(drag!.Ancestors(), a => (string?)a.Attribute(X + "Name") == row);
        Assert.Equal("TitleBar_PointerPressed", (string?)drag.Attribute("PointerPressed"));
        Assert.Equal("TitleBar_DoubleTapped", (string?)drag.Attribute("DoubleTapped"));
        Assert.False(string.IsNullOrEmpty((string?)drag.Attribute("Background")),
            $"`{strip}` has no Background, so it is not hit-testable and moves nothing.");
    }

    [Fact]
    public void TheRigOverlay_IsConfinedToTheContentRow_AndNeverSpansTheTitleBar()
    {
        var window = MainWindowMarkup();

        // Non-vacuity: the row model this test reasons about has to be the one in
        // the file. TitleContentGrid's row 0 IS the title bar, row 1 the content.
        // Since #654 the title bar is a row of WindowChromeView, which takes that cell.
        var chrome = Named(window, "WindowChrome");
        Assert.True(chrome is not null, "MainWindow no longer hosts WindowChromeView as `WindowChrome` — repoint this test.");
        Assert.Equal("0", (string?)chrome!.Attribute("Grid.Row"));
        Assert.Equal("TitleContentGrid", (string?)chrome.Parent?.Attribute(X + "Name"));
        var titleBar = Named(window, "TitleBarRow");
        Assert.True(titleBar is not null, "TitleBarRow is gone — repoint this test.");
        Assert.Contains(chrome, titleBar!.Ancestors());

        var overlay = Named(window, "PedalboardOverlay");
        Assert.True(overlay is not null,
            "MainWindow.axaml has no `PedalboardOverlay` — the chart editor's Amp & FX rig host.");

        Assert.True((string?)overlay!.Parent?.Attribute(X + "Name") == "TitleContentGrid",
            "the rig overlay is no longer a child of TitleContentGrid. It has to sit in a container "
            + "whose rows exclude the title bar: as a child of WindowRootGrid or ShellRootGrid it covers "
            + "TitleBarDragArea, and the rig's opaque FireOverlayFill then eats the only gesture that "
            + "moves the window (v0.48.5).");

        Assert.True((string?)overlay.Attribute("Grid.Row") == "1",
            $"the rig overlay is in row `{(string?)overlay.Attribute("Grid.Row") ?? "(unset → 0)"}` of "
            + "TitleContentGrid. Row 0 is the title bar.");

        Assert.True(overlay.Attribute("Grid.RowSpan") is null,
            "the rig overlay declares a Grid.RowSpan, which is how it reaches back over the title-bar "
            + "row. It covers the content row and nothing else.");
    }

    // ---- helpers --------------------------------------------------------------

    private static XElement? Named(XElement root, string name) =>
        root.Descendants().FirstOrDefault(e => (string?)e.Attribute(X + "Name") == name);

    private static XElement MainWindowMarkup() =>
        MainWindowSource.Document().Root!;
}
