using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Structure guard for the Song Select "loading looks broken" layout bug.
///     While the library loads, the list is empty and the empty state is suppressed,
///     so the surface has almost no content. A <c>HorizontalAlignment="Center"</c>
///     column sizes to its content and therefore COLLAPSED during load — the header
///     and filter row shifted and the always-on list scrollbar detached mid-surface;
///     it only "snapped" to the intended centered 1360-wide column once songs filled
///     it. The fix makes the one shared column <c>Stretch</c> (fills to
///     <c>MaxWidth</c> and centers) so it is identical in every state, with every
///     piece of state chrome (loading indicator, filters, list, empty state) INSIDE
///     that single column.
///
///     Asserted against the real XAML tree (parsed as XML) rather than a live
///     headless view: booting the full app session for a visual-tree walk is exactly
///     the <c>HeadlessSessionFixture</c> teardown trap the suite excludes from the
///     default gate — this is the same source-structure approach as
///     <see cref="PowerToolReachabilityTests" />, deterministic and gate-safe.
/// </summary>
public sealed class SongSelectLayoutStructureTests
{
    [Fact]
    public void SongSelect_FillsTheWindow_SoItStaysStableInEveryState()
    {
        var grid = SongSelectContainer();

        // Stretch (not Center) is the load-bearing half — see the class summary.
        Assert.Equal("Stretch", (string?)grid.Attribute("HorizontalAlignment"));

        // The 1360 px cap is GONE in 0.47. The redesign's columns meet the
        // window's own edges (a header + filter bar over a flexible list and a
        // fixed 420 px detail pane), and a centred island is exactly what the
        // design rules out. Dropping the cap does not weaken this guard: it was
        // never the cap that kept the surface stable during a scan, it was
        // Stretch — a Center-aligned grid sizes to its CONTENT, which is what
        // collapsed the header and filter row while the list was empty.
        Assert.Null(grid.Attribute("MaxWidth"));
    }

    [Fact]
    public void SongSelect_AllStateChrome_LivesInsideTheOneCenteredColumn()
    {
        var inside = SongSelectContainer().Descendants().ToList();

        // The loading indicator.
        Assert.Contains(inside, e => e.Name.LocalName == "ProgressBar"
            && (string?)e.Attribute("IsVisible") == "{Binding SongSelect.IsLibraryLoading}");

        // The caption row's filters (the unified browser, 2026-08-15): the
        // 0.47 combo row is gone — representative facet popovers (tuning +
        // played) hang off the header's funnels instead.
        Assert.Contains(inside, e => e.Name.LocalName == "ContentControl"
            && (string?)e.Attribute("Content") == "{Binding SongSelect.TuningFacet}");
        Assert.Contains(inside, e => e.Name.LocalName == "ContentControl"
            && (string?)e.Attribute("Content") == "{Binding SongSelect.PlayedFacet}");

        // The song list.
        Assert.Contains(inside, e => e.Name.LocalName == "ListBox"
            && (string?)e.Attribute("ItemsSource") == "{Binding SongSelect.SongList}");

        // BOTH empty states (0.37.1 — an empty library and a filter that hid
        // everything are different problems with different escape hatches, so
        // they are two panels, not one message).
        Assert.Contains(inside, e => e.Name.LocalName == "Border"
            && (string?)e.Attribute("IsVisible") == "{Binding SongSelect.ShowSongSelectFirstRun}");
        Assert.Contains(inside, e => e.Name.LocalName == "Border"
            && (string?)e.Attribute("IsVisible") == "{Binding SongSelect.ShowSongSelectNoMatch}");
    }

    /// <summary>The list stays VIRTUALIZED. A real library is thousands of rows,
    /// and an <c>ItemsPanel</c> override on this ListBox would realize every one
    /// of them on every filter pass. The editor's song browser has carried this
    /// guard since 0.44 (<c>SongBrowserFilterPassTests.TheSongList_DeclaresNoItemsPanelOverride</c>);
    /// Play Mode's list had no equivalent. The behavioural half (realized
    /// containers stay a handful) is
    /// <c>Perf/PlayModeScreensUiPerfSmokeTests.SongSelect</c>.</summary>
    [Fact]
    public void TheSongList_DeclaresNoItemsPanelOverride()
    {
        var list = SongSelectContainer().Descendants()
            .Single(e => e.Name.LocalName == "ListBox"
                && (string?)e.Attribute("ItemsSource") == "{Binding SongSelect.SongList}");

        Assert.DoesNotContain(list.Descendants(),
            e => e.Name.LocalName == "ListBox.ItemsPanel");
    }

    /// <summary>The single Grid that is the whole Song Select surface — the one
    /// gated on <c>IsSongSelectScreen</c> (since #294 through the element that
    /// declares its view, which <see cref="PlayModeMarkup.GateOf" /> follows).
    /// Unprefixed XAML attributes live in no namespace, so they are read by local
    /// name.</summary>
    private static XElement SongSelectContainer()
    {
        var doc = PlayModeMarkup.Document();
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Grid")
            .Single(e => PlayModeMarkup.GateOf(e) == "{Binding IsSongSelectScreen}");
    }
}
