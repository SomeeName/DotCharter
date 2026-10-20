using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Menu-only verbs the palette missed. <c>CommandPaletteCoverageTests</c>
///     walks <c>KeybindConfig</c>, so a menu row bound to a command (or an
///     <c>IsChecked</c> two-way) with NO keybind is structurally invisible to
///     it — and five real gaps had accumulated behind that blind spot: the ten
///     Edit ▸ Bookmarks rows (the <c>BookmarkBase</c> digit-family exclusion is
///     a fair excuse for the KEYS, but the menu proves the verb needs no
///     argument picker), the five View ▸ Timeline Rows toggles (whose previous
///     always-visible pill strip was deleted, leaving the menu as their only
///     door), and Help ▸ Usage Statistics / Session Health (the "something is
///     wrong" surfaces). These lints are source-shaped for the same reason the
///     coverage tests are: <c>CommandCatalog.Build</c> takes a real
///     <c>MainWindowViewModel</c>, which needs the app session.
/// </summary>
public sealed class CommandPaletteMenuVerbTests
{
    [Fact]
    public void EveryTimelineRowToggleInTheMenu_HasAPaletteTwin()
    {
        // Derived from the MENU, not restated: a sixth row added to the
        // Timeline Rows submenu without a palette twin fails here.
        var menu = MainMenuSource.Document();
        var ns = menu.Root!.Name.Namespace;
        var rowsMenu = menu.Descendants(ns + "MenuItem")
            .Single(e => (string?)e.Attribute("Header") == "{localization:Loc Timeline_Rows}");

        var flagNames = rowsMenu.Elements(ns + "MenuItem")
            .Select(e => (string?)e.Attribute("IsChecked") ?? "")
            .Select(binding => Regex.Match(binding, @"ActiveRibbon\.(\w+)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(flagNames.Count >= 5,
            $"expected the Timeline Rows submenu to bind at least five ActiveRibbon flags, saw {flagNames.Count}");

        var catalog = UiSourceTree.ReadAllText(CatalogPath());
        foreach (var flag in flagNames)
            Assert.Contains($"ribbon.{flag} = !ribbon.{flag}", catalog);
    }

    [Fact]
    public void TheBookmarkRows_AreInThePalette_WithTheirFormatStringInBothResx()
    {
        var catalog = UiSourceTree.ReadAllText(CatalogPath());
        Assert.Contains("edit.toggleBookmark.", catalog);
        Assert.Contains("Palette_ToggleBookmarkFmt", catalog);
        // All ten menu slots: the loop bound must cover 0..9.
        Assert.Contains("slot <= 9", catalog);

        foreach (var resx in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var text = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", resx));
            Assert.Contains("Palette_ToggleBookmarkFmt", text);
        }
    }

    [Fact]
    public void TheHelpEscalationSurfaces_AreInThePalette()
    {
        var catalog = UiSourceTree.ReadAllText(CatalogPath());
        Assert.Contains("\"help.usageStatistics\"", catalog);
        Assert.Contains("\"help.sessionHealth\"", catalog);
    }

    private static string CatalogPath() => UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs"));
}
