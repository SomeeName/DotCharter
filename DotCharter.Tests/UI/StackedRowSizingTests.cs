using System.Text.RegularExpressions;
using Avalonia.Controls;
using DotCharter.UI.Layout;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>Which row of <c>TimelineGrid</c> the layout controller is allowed to
///     collapse to zero.</b>
///     <para>
///         This guard exists because of a real, shipped, whole-surface failure.
///         <c>MainWindowLayoutController.UpdateStackedRowSizing</c> collapses the
///         stacked-timelines row to 0 px whenever the stacked panel is hidden —
///         which is the DEFAULT — and it used to name that row by the literal
///         index <c>1</c>. When 0.51 added a <c>ROWS</c> pill strip as an
///         <c>Auto</c> row at the top of the same grid, the strip took index 0
///         and the <c>ChartRenderer</c> moved to index 1. The very first call
///         (<c>Bind</c>, before a single frame) then collapsed the RENDERER's
///         row instead, and the whole chart area drew nothing: no string lines,
///         no ruler, no playhead. There was no exception, no warning, and the
///         log still reported the composed-Vulkan timeline as active, because
///         the renderer had initialised perfectly — it just had no height. The
///         default gate was green and every shell smoke test passed.
///     </para>
///     <para>
///         So the row is resolved through the HOST
///         (<c>Grid.GetRow(StackedTracksHost)</c>), and the two halves below pin
///         both ends of that: the markup really does put the renderer and the
///         stacked host in different rows, and the resolver really does return
///         the host's.
///     </para>
/// </summary>
public sealed class StackedRowSizingTests
{
    /// <summary>
    ///     The collapse targets the stacked host's row, and that row is NOT the
    ///     renderer's — read from the real markup, so adding another row to
    ///     <c>TimelineGrid</c> cannot silently repeat 0.51's blank timeline.
    /// </summary>
    [Fact]
    public void TheCollapsedRow_IsTheStackedHostsRow_NeverTheRenderers()
    {
        var timeline = TimelineGridMarkup();

        var rendererRow = GridRowOf(timeline, "MainChartRenderer");
        var stackedRow = GridRowOf(timeline, "StackedTracksHost");
        var rowCount = RowCount(timeline);

        Assert.True(rendererRow >= 0, "MainWindow.axaml no longer gives MainChartRenderer an explicit Grid.Row");
        Assert.True(stackedRow >= 0, "MainWindow.axaml no longer gives StackedTracksHost an explicit Grid.Row");
        Assert.True(rowCount >= 2, $"TimelineGrid declares only {rowCount} row(s) — this guard is blind");
        Assert.NotEqual(rendererRow, stackedRow);

        // The real resolver, against a grid shaped exactly like the markup's.
        var grid = new Grid();
        for (var i = 0; i < rowCount; i++) grid.RowDefinitions.Add(new RowDefinition());

        var renderer = new Border();
        Grid.SetRow(renderer, rendererRow);
        grid.Children.Add(renderer);

        var host = new ContentControl();
        Grid.SetRow(host, stackedRow);
        grid.Children.Add(host);

        var resolved = MainWindowLayoutController.StackedRowIndex(grid, host);
        Assert.Equal(stackedRow, resolved);
        Assert.NotEqual(rendererRow, resolved);
    }

    /// <summary>
    ///     A host parked outside the grid's rows collapses nothing at all. The
    ///     old literal wrote row 1 unconditionally; a resolver that fell back to
    ///     an index would reintroduce exactly the failure this file is about.
    /// </summary>
    [Fact]
    public void AHostOutsideTheGrid_CollapsesNoRow()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var stray = new ContentControl();
        Grid.SetRow(stray, 7);

        Assert.Equal(-1, MainWindowLayoutController.StackedRowIndex(grid, stray));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>The <c>TimelineGrid</c> element and everything inside it.</summary>
    private static string TimelineGridMarkup()
    {
        var markup = MainWindowSource.ReadAllText();
        var start = markup.IndexOf("x:Name=\"TimelineGrid\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainWindow.axaml no longer declares TimelineGrid");
        var end = markup.IndexOf("x:Name=\"TimelinePlaceholder\"", start, StringComparison.Ordinal);
        return end > start ? markup[start..end] : markup[start..];
    }

    private static int RowCount(string timeline)
    {
        var rows = Regex.Match(timeline, @"RowDefinitions=""(?<r>[^""]+)""");
        Assert.True(rows.Success, "TimelineGrid no longer declares RowDefinitions inline");
        return rows.Groups["r"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>The <c>Grid.Row</c> written on the element carrying
    /// <paramref name="name" />, or -1 when it has none.</summary>
    private static int GridRowOf(string timeline, string name)
    {
        var at = timeline.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        if (at < 0) return -1;
        // The attribute sits within the same element — bounded by the next '>'.
        var close = timeline.IndexOf('>', at);
        var element = close > at ? timeline[at..close] : timeline[at..];
        var row = Regex.Match(element, @"Grid\.Row=""(?<n>\d+)""");
        return row.Success ? int.Parse(row.Groups["n"].Value) : -1;
    }
}
