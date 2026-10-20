using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Icon hygiene across ALL of <c>Views/**</c> (2026-08-11; the dialog
///     grammar had this rule since its conversion, the other 130+ icon uses
///     did not).
///     <para>
///         <b>Icon geometry resolves through <c>{DynamicResource}</c>.</b> A
///         <c>{StaticResource IconX}</c> throws at construction when the view
///         is built without the app-scoped dictionary — which is exactly how
///         <c>ViewLocator</c> renders a "Not Found:" placeholder — and a typo'd
///         DYNAMIC key is a blank glyph, which
///         <c>ShellConstructionUiPerfSmokeTests.AssertIconsResolved</c> is the
///         net for. 88 uses were converted when this landed.
///     </para>
///     <para>
///         <b>Inline <c>Data="M…"</c> is RATCHETED.</b> 76 inline geometries
///         live in views today (52 of them in the menu bar, now one row per
///         <c>Views/Shell/Menus</c> file); each is a
///         glyph no second view can share and no theme can retarget. The
///         per-file budget below may only SHRINK — lift a glyph into
///         <c>Styles/Icons.axaml</c> when you touch its view, and update the
///         row. The window-chrome trio in <c>Views/Shell/WindowChromeView.axaml</c>
///         (in <c>MainWindow.axaml</c> until #654 lifted the rows out) is the
///         sanctioned floor there: 10×10 chrome glyphs, one swapped from
///         code-behind (<c>WindowChromeView.ShowWindowState</c>), which a resource
///         cannot express.
///     </para>
/// </summary>
public sealed class IconResourceTests
{
    [Fact]
    public void NoView_PullsAnIconThroughStaticResource()
    {
        var viewsRoot = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file), @"\{StaticResource (Icon[A-Za-z0-9_]+)\}"))
                offenders.Add($"{Path.GetFileName(file)}: {{StaticResource {m.Groups[1].Value}}}");
        }

        Assert.True(offenders.Count == 0,
            "Icon geometry must resolve dynamically — a StaticResource throws at construction " +
            "outside the app dictionary's scope:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The ratchet: per-file inline-geometry budgets, measured
    /// 2026-08-11. A number here may only go DOWN.</summary>
    private static readonly Dictionary<string, int> InlineGeometryBudget = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowChromeView.axaml"] = 3, // the sanctioned window-chrome trio (MainWindow.axaml's until #654)
        ["SettingsShellView.axaml"] = 2,
        ["AudioSettingsView.axaml"] = 1,
        ["GeneralSettingsView.axaml"] = 1,
        ["GraphicsSettingsView.axaml"] = 1,
        // The menu bar's glyphs, one row per menu file since #653 split
        // MainMenuView.axaml (52 then; 48 measured at the split).
        ["FileMenu.axaml"] = 10,
        ["EditMenu.axaml"] = 8,
        ["TempoMapMenu.axaml"] = 2,
        ["ArrangementsMenu.axaml"] = 2,
        ["VocalsMenu.axaml"] = 2,
        ["ToolsMenu.axaml"] = 21,
        ["ViewMenu.axaml"] = 1,
        ["HelpMenu.axaml"] = 2,
        ["ToolbarView.axaml"] = 2
    };

    [Fact]
    public void InlineIconGeometry_OnlyEverShrinks()
    {
        var viewsRoot = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            var count = Regex.Matches(UiSourceTree.ReadAllText(file), "Data=\"M[ 0-9.,]").Count;
            if (count == 0) continue;
            var budget = InlineGeometryBudget.GetValueOrDefault(Path.GetFileName(file));
            if (count > budget)
            {
                offenders.Add($"{Path.GetFileName(file)}: {count} inline geometries (budget {budget}) — " +
                              "lift the new glyph into Styles/Icons.axaml and reach it via DynamicResource");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }
}
