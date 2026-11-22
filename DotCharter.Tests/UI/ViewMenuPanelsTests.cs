using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     View ▸ Panels lists only things that open and close their own area of the window (#512);
///     the Inspector's tabs have their own submenu, and the stacked-arrangements option sits with the
///     timeline options. Both sets are derived from the code, not from a list written here.
/// </summary>
public sealed class ViewMenuPanelsTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";

    private static readonly Regex FlagBinding = new(@"\{Binding (\w+)", RegexOptions.Compiled);
    private static readonly Regex TabIndexParameter = new(@"ConverterParameter=(\d+)", RegexOptions.Compiled);

    private static XElement Menu() =>
        XDocument.Load(Path.Combine(MainMenuSource.MenusDirectory, "ViewMenu.axaml")).Root!;

    private static string Key(XElement item) =>
        Regex.Match((string?)item.Attribute("Header") ?? "", @"Loc (\w+)").Groups[1].Value;

    private static XElement Group(string key) => Menu().Elements(Ns + "MenuItem").Single(e => Key(e) == key);

    private static string Flag(XElement row) =>
        FlagBinding.Match((string?)row.Attribute("IsChecked") ?? "").Groups[1].Value;

    /// <summary>
    ///     The visibility flags the main window answers with an area of its own: a dock pane, the AI chat column
    ///     or the 3D preview host. A flag whose handler only selects an Inspector tab, or builds a
    ///     timeline surface, is not one.
    /// </summary>
    private static HashSet<string> PanelFlags()
    {
        var window = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "MainWindow.axaml.cs"));
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(
                     window, @"case nameof\(MainWindowViewModel\.(Is\w+Visible)\):(?<body>.*?)break;", RegexOptions.Singleline))
        {
            var body = m.Groups["body"].Value;
            if (body.Contains("ShowInspectorTab", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(body, @"SetPaneVisible|ApplyAiChatColumn|ClosePreview3DWindow"))
                flags.Add(m.Groups[1].Value);
        }

        return flags;
    }

    [Fact]
    public void TheDerivedPanelSet_IsTheFourPanels() =>
        Assert.Equal(
            ["IsAiChatVisible", "IsInspectorVisible", "IsPreviewVisible", "IsSheetMusicVisible"],
            PanelFlags().OrderBy(f => f, StringComparer.Ordinal));

    [Fact]
    public void EveryRowUnderPanels_TogglesARegisteredPanel_AndEveryPanelHasARow()
    {
        var rows = Group("Menu_GroupPanels").Elements(Ns + "MenuItem").ToList();
        var panels = PanelFlags();
        foreach (var row in rows)
            Assert.True(panels.Contains(Flag(row)), $"{Key(row)} toggles {Flag(row)}, which is not a panel");

        Assert.Equal(panels.OrderBy(f => f, StringComparer.Ordinal), rows.Select(Flag).OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryInspectorTab_HasExactlyOneRow_InTheStripsOrder()
    {
        var inspector = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "InspectorView.axaml"));
        var strip = inspector.Substring(inspector.IndexOf("x:Name=\"InspectorTabRow\"", StringComparison.Ordinal));
        strip = strip.Substring(0, strip.IndexOf("InspectorTabMore", StringComparison.Ordinal));
        var stripOrder = Regex.Matches(strip, @"ColumnFlowNav""[^>]*?\bTag=""(\d+)""")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(8, stripOrder.Count);

        var rows = Group("Menu_GroupInspectorTab").Elements(Ns + "MenuItem").ToList();
        var rowTabs = rows
            .Select(r => int.Parse(TabIndexParameter.Match((string?)r.Attribute("IsChecked") ?? "").Groups[1].Value))
            .ToList();
        Assert.Equal(stripOrder, rowTabs);
        // The Riff library row keeps the verb its keybind runs (MenuGestureCoverageTests ties the chord to it).
        foreach (var row in rows)
        {
            var expected = Key(row) == "Menu_PanelPhraseCatalog" ? "TogglePhraseCatalogCommand" : "TabMenu.ShowRowCommand";
            Assert.Contains(expected, (string?)row.Attribute("Command"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MixerRow_UsesTheSameNameAsTheInspectorTab()
    {
        var mixer = Group("Menu_GroupInspectorTab").Elements(Ns + "MenuItem").Single(r =>
            (string?)r.Attribute("CommandParameter") == InspectorViewModel.MixerTabIndex.ToString());
        Assert.Equal("Inspector_Tab_Mixer", Key(mixer));
    }

    [Fact]
    public void TheStemMixerAndRiffLibraryRows_AreInspectorTabRows_NotPanels()
    {
        var panels = Group("Menu_GroupPanels").ToString();
        var tabs = Group("Menu_GroupInspectorTab").ToString();
        Assert.DoesNotContain("IsStemMixerVisible", panels, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPhraseCatalogVisible", panels, StringComparison.Ordinal);
        Assert.Contains("Inspector_Tab_Mixer", tabs, StringComparison.Ordinal);
        Assert.Contains("Menu_PanelPhraseCatalog", tabs, StringComparison.Ordinal);
        Assert.Contains("Keybinds.TogglePhraseCatalog", tabs, StringComparison.Ordinal);
        Assert.Contains($"ConverterParameter={InspectorViewModel.MixerTabIndex}", tabs, StringComparison.Ordinal);
        Assert.Contains($"ConverterParameter={InspectorViewModel.RiffLibraryTabIndex}", tabs, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInspectorTabSubmenu_SitsDirectlyUnderPanels_AndStackedTracksJoinsTheTimelineOptions()
    {
        var top = Menu().Elements().Where(e => e.Name.LocalName is "MenuItem").Select(Key).ToList();
        Assert.Equal(["Menu_GroupPanels", "Menu_GroupInspectorTab"], top.Take(2));

        var stacked = Menu().Elements(Ns + "MenuItem").Single(e => Key(e) == "Menu_PanelStackedTracks");
        Assert.Equal("IsStackedTracksVisible", Flag(stacked));
        var order = top.ToList();
        Assert.Equal(order.IndexOf("Timeline_Rows") + 1, order.IndexOf("Menu_ShowWaveform"));
        Assert.True(order.IndexOf("Menu_PanelStackedTracks") > order.IndexOf("Timeline_Rows"));
        Assert.True(order.IndexOf("Menu_PanelStackedTracks") - order.IndexOf("Menu_ShowWaveform") is 1 or -1);
    }
}
