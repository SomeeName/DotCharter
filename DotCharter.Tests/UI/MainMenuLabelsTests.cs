using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     One menu label, one verb. "Delete current" served both the Arrangements and the Vocals
///     menus (and the command palette, where the menu around it is gone), so it named neither
///     thing it deleted (menu review, 2026-09-24).
/// </summary>
public sealed class MainMenuLabelsTests
{
    [Fact]
    public void NoLabel_NamesTwoDifferentCommands()
    {
        var menu = MainMenuSource.ReadAllText();
        var pairs = Regex.Matches(menu, @"<MenuItem\s+Header=""\{localization:Loc (?<key>\w+)\}""\s+Command=""\{Binding (?<cmd>[\w.]+)\}""")
            .Select(m => (Key: m.Groups["key"].Value, Command: m.Groups["cmd"].Value))
            .ToList();
        Assert.True(pairs.Count > 40, $"only {pairs.Count} menu items matched — the pattern no longer fits the menu");

        var shared = pairs.GroupBy(p => p.Key)
            .Where(g => g.Select(p => p.Command).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => p.Command).Distinct(StringComparer.Ordinal))}")
            .ToList();
        Assert.True(shared.Count == 0, "one label names different commands:\n  " + string.Join("\n  ", shared));
    }

    /// <summary>A separator groups; one between every item is clutter. Help had four for six
    /// items, so three of its groups were a single line each (menu review, 2026-09-24).</summary>
    [Fact]
    public void NoMenu_IsMostlySeparators()
    {
        var doc = MainMenuSource.Document();
        var tops = doc.Descendants(Avalonia + "MenuItem")
            .Where(m => ((string?)m.Attribute("Classes") ?? "").Contains("FireMenuTop", StringComparison.Ordinal))
            .ToList();
        Assert.True(tops.Count >= 6, $"only {tops.Count} top-level menus found — the pattern no longer fits the menu");

        var cluttered = tops
            .Select(t => (Header: (string?)t.Attribute("Header") ?? "?",
                          Items: t.Elements(Avalonia + "MenuItem").Count(),
                          Separators: t.Elements(Avalonia + "Separator").Count()))
            .Where(t => t.Separators * 2 > t.Items)
            .Select(t => $"{t.Header}: {t.Separators} separators for {t.Items} items")
            .ToList();
        Assert.True(cluttered.Count == 0, "menus with more separators than half their items:\n  " + string.Join("\n  ", cluttered));
    }

    /// <summary>An item that opens something to fill in says so with "…" (the desktop rule the
    /// rest of the menu follows). "Showlights editor" opened a dialog without one. Commands that
    /// go to a SCREEN (Play mode, Settings) are places, not dialogs, and take none.</summary>
    [Fact]
    public void EveryDialogOpener_EndsInAnEllipsis()
    {
        var menu = MainMenuSource.ReadAllText();
        var resx = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.resx"));
        string[] screens = ["OpenPlayModeCommand", "OpenSettingsCommand"];
        var openers = Regex.Matches(menu, @"<MenuItem\s+Header=""\{localization:Loc (?<key>\w+)\}""\s+Command=""\{Binding (?:\w+\.)?(?<cmd>Open\w+|Show\w+)\}""")
            .Select(m => (Key: m.Groups["key"].Value, Command: m.Groups["cmd"].Value))
            .Where(p => !screens.Contains(p.Command))
            .ToList();
        Assert.True(openers.Count > 20, $"only {openers.Count} dialog openers matched — the pattern no longer fits the menu");
        foreach (var screen in screens) Assert.Contains($"{{Binding {screen}}}", menu);

        var bare = openers
            .Select(p => (p.Key, Value: System.Net.WebUtility.HtmlDecode(Regex.Match(resx,
                $@"<data name=""{p.Key}"" xml:space=""preserve"">\s*<value>(.*?)</value>", RegexOptions.Singleline).Groups[1].Value)))
            .Where(p => !p.Value.EndsWith('…'))
            .Select(p => $"{p.Key} = \"{p.Value}\"")
            .ToList();
        Assert.True(bare.Count == 0, "dialog openers without \"…\":\n  " + string.Join("\n  ", bare));
    }

    /// <summary>A menu item has a menu caption. The keybind table names ACTIONS ("Paste
    /// special"), while the Edit menu's item opens a dialog and says "Paste special…" — it
    /// borrowed the keybind's words and lost the ellipsis (2026-09-24).</summary>
    [Fact]
    public void NoMenuItem_BorrowsAKeybindLabel()
    {
        var menu = MainMenuSource.ReadAllText();
        var borrowed = Regex.Matches(menu, @"<MenuItem\s+Header=""\{localization:Loc (Keybind_\w+)\}""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(borrowed.Count == 0, "menu items captioned with a keybind label: " + string.Join(", ", borrowed));
    }

    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
}
