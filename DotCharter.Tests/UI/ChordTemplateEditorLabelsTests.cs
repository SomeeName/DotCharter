using System.Xml.Linq;
using DotCharter.Localization;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every picker in the chord editor says what it is on screen. The chord-name picker carried
///     its name only in a tooltip and the automation name, so a sighted user saw a blank box under
///     "Template" (inspector review, 2026-09-24).
/// </summary>
public sealed class ChordTemplateEditorLabelsTests
{
    private static readonly XNamespace Av = "https://github.com/avaloniaui";

    [Fact]
    public void EachPicker_HasAVisibleLabel_InItsRow()
    {
        var doc = XDocument.Load(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Panels", "ChordTemplateEditorView.axaml")));
        var grid = doc.Descendants(Av + "Grid").Single(g => (string?)g.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ChordTemplatePickers");

        var combos = grid.Elements(Av + "ComboBox").ToList();
        Assert.Equal(2, combos.Count);
        foreach (var combo in combos)
        {
            var row = (string?)combo.Attribute("Grid.Row") ?? "0";
            var name = (string?)combo.Attribute(XName.Get("AutomationProperties.Name")) ?? "";
            var label = grid.Elements(Av + "TextBlock").SingleOrDefault(t => ((string?)t.Attribute("Grid.Row") ?? "0") == row);
            Assert.True(label != null, $"the picker in row {row} ({name}) has no visible label beside it");
            Assert.Equal(name, (string?)label!.Attribute("Text"));
        }
    }

    /// <summary>
    ///     The Nop flag is a NAMED toggle (#489) — it used to be reachable only by typing
    ///     "-nop" into the name. Bound to the view model, its accessible name is its
    ///     visible label, and its tooltip says what the flag is.
    /// </summary>
    [Fact]
    public void TheNopToggle_IsBoundAndNamed()
    {
        var doc = XDocument.Load(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Panels", "ChordTemplateEditorView.axaml")));
        var toggle = doc.Descendants(Av + "CheckBox")
            .Single(c => (string?)c.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "NopToggle");

        Assert.Equal("{Binding IsNop}", (string?)toggle.Attribute("IsChecked"));
        Assert.Equal("{localization:Loc ChordTemplate_Nop}", (string?)toggle.Attribute("AutomationProperties.Name"));
        Assert.Equal((string?)toggle.Attribute("Content"), (string?)toggle.Attribute("AutomationProperties.Name"));
        Assert.Equal("{localization:Loc ChordTemplate_NopTip}", (string?)toggle.Attribute("ToolTip.Tip"));
        Assert.Contains("-nop", LocalizationService.Instance["ChordTemplate_Nop"], StringComparison.Ordinal);
    }
}
