using System.Text.RegularExpressions;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The timeline-over-dock split a first launch shows and the one View ▸ Reset Layout
///     returns to are the SAME split (#232). They were three literals in three files —
///     the markup said 2:3, the layout controller's restore said 2:1 and won at startup,
///     and Reset Layout said 2:3 again — so "reset" moved the splitter somewhere the
///     user had never seen. Every site now reads <see cref="EditorSplitDefaults" />.
/// </summary>
public sealed class EditorSplitDefaultsTests
{
    private static string UiFile(params string[] parts) =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine([TestPaths.RepoRoot(), "DotCharter.UI", .. parts])));

    /// <summary>3 : 4 (#81): the owner's #232 look at 1080p — 412 px of timeline area over
    /// 550 of dock — as a RATIO, so the timeline grows with the window instead of sitting on
    /// its floor (2 : 3's share was under it at every Auto-scaled size).</summary>
    [Fact]
    public void TheDefault_IsThreeToFour()
    {
        Assert.True(EditorSplitDefaults.TimelineRow.IsStar);
        Assert.True(EditorSplitDefaults.DockRow.IsStar);
        Assert.Equal(3, EditorSplitDefaults.TimelineRow.Value);
        Assert.Equal(4, EditorSplitDefaults.DockRow.Value);
    }

    /// <summary>The markup's two content rows name the shared defaults, not literals.</summary>
    [Fact]
    public void TheMarkup_ReadsTheSharedDefaults()
    {
        var window = MainWindowSource.ReadAllText();
        var grid = window[window.IndexOf("x:Name=\"MainSplitGrid\"", StringComparison.Ordinal)..];
        var rows = grid[..grid.IndexOf("</Grid.RowDefinitions>", StringComparison.Ordinal)];

        Assert.Contains("Height=\"{x:Static shellViews:EditorSplitDefaults.TimelineRow}\"", rows, StringComparison.Ordinal);
        Assert.Contains("Height=\"{x:Static shellViews:EditorSplitDefaults.DockRow}\"", rows, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Height=""\d*\*"""), rows);
    }

    /// <summary>
    ///     The layout controller's remembered dock height starts as the default (it is what
    ///     the dock row is set to on the first layout pass), and its "nothing saved"
    ///     fallback is the default too.
    /// </summary>
    [Fact]
    public void TheLayoutController_ReadsTheSharedDefault()
    {
        var source = UiFile("Layout", "MainWindowLayoutController.cs");

        Assert.Contains("_savedDockRowHeight = Views.Shell.EditorSplitDefaults.DockRow;", source, StringComparison.Ordinal);
        Assert.Contains("? Views.Shell.EditorSplitDefaults.DockRow", source, StringComparison.Ordinal);
    }

    /// <summary>Reset Layout puts both rows back to the shared defaults.</summary>
    [Fact]
    public void ResetLayout_ReadsTheSharedDefaults()
    {
        var source = UiFile("Views", "MainWindow.Docking.cs");
        var start = source.IndexOf("void ResetLayoutToDefaults", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResetLayoutToDefaults moved; repoint this test at it.");
        var body = source[start..source.IndexOf("InspectorSplitGrid", start, StringComparison.Ordinal)];

        Assert.Contains("RowDefinitions[0].Height = Shell.EditorSplitDefaults.TimelineRow;", body, StringComparison.Ordinal);
        Assert.Contains("RowDefinitions[2].Height = Shell.EditorSplitDefaults.DockRow;", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"GridUnitType\.Star"), body);
    }
}
