using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every <c>{localization:Loc …}</c> key the main menu names must exist in
///     BOTH resx files.
///     <para>
///         A missing key is one of this codebase's silent failures: no build
///         error, no exception — just a menu entry that renders blank, or an
///         English one in a German session. Regrouping the Tools menu added five
///         new headings at once, which is exactly the change that drops one.
///     </para>
/// </summary>
public sealed class MainMenuLocalizationTests
{
    [Fact]
    public void EveryMenuKeyExistsInBothResxFiles()
    {
        var menu = MainMenuSource.ReadAllText();
        var keys = Regex.Matches(menu, @"localization:Loc\s+(\w+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        // If this collapses the lint has stopped looking at anything.
        Assert.True(keys.Count > 60, $"expected the menu to name many keys, saw {keys.Count}");

        var english = KeysIn("Resources.resx");
        var german = KeysIn("Resources.de-DE.resx");

        var missingEnglish = keys.Where(k => !english.Contains(k)).ToList();
        var missingGerman = keys.Where(k => !german.Contains(k)).ToList();

        Assert.True(missingEnglish.Count == 0,
            "main-menu keys missing from Resources.resx: " + string.Join(", ", missingEnglish));
        Assert.True(missingGerman.Count == 0,
            "main-menu keys missing from Resources.de-DE.resx: " + string.Join(", ", missingGerman));
    }

    /// <summary>
    ///     The Tools menu is grouped, not a flat list. It had ~20 direct entries —
    ///     longer than the eye scans, and it put "Separate Stems" next to "Refret"
    ///     next to "Screenshot" with nothing to say why. The cap is deliberately
    ///     loose: this guards the SHAPE, so adding a tool inside an existing group
    ///     is free while adding a twenty-first top-level one is not.
    /// </summary>
    [Fact]
    public void TheToolsMenuStaysGrouped()
    {
        var menu = MainMenuSource.Document();
        var ns = menu.Root!.Name.Namespace;

        var tools = menu.Descendants(ns + "MenuItem")
            .Single(e => (string?)e.Attribute("Header") == "{localization:Loc Menu_Tools}");

        var directChildren = tools.Elements(ns + "MenuItem").ToList();

        Assert.True(directChildren.Count <= 10,
            $"the Tools menu has {directChildren.Count} top-level entries — group them into submenus.");

        // …and grouping means the entries actually CONTAIN things, rather than the
        // count being low because tools were deleted.
        Assert.True(directChildren.Count(e => e.Elements(ns + "MenuItem").Any()) >= 5,
            "the Tools menu's top-level entries are not submenus — nothing is grouped.");
    }

    /// <summary>
    ///     …and since 0.52 the same rule reaches the OTHER long menus, which the
    ///     owner asked for after living with the grouped Tools menu: File had 20
    ///     flat entries, Edit 21, View 16, Tempo Map 11. What is pinned is the
    ///     SHAPE — a cap on direct entries, and the fact that the long menus
    ///     really do carry submenus — so adding a verb inside a group is free.
    /// </summary>
    [Fact]
    public void EveryTopLevelMenuStaysScannable()
    {
        var (tops, ns) = TopLevelMenus();
        Assert.Equal(8, tops.Count);

        foreach (var top in tops)
        {
            var direct = top.Elements(ns + "MenuItem").ToList();
            Assert.True(direct.Count <= 14,
                $"the {Header(top)} menu has {direct.Count} top-level entries — group them into submenus.");
        }

        // The five that were too long to scan must each carry at least one group.
        foreach (var key in new[] { "Menu_File", "Menu_Edit", "Menu_TempoMap", "Menu_View", "Menu_Tools" })
        {
            var top = tops.Single(e => (string?)e.Attribute("Header") == $"{{localization:Loc {key}}}");
            Assert.True(top.Elements(ns + "MenuItem").Any(c => c.Elements(ns + "MenuItem").Any()),
                $"the {key} menu is flat again — the 0.52 grouping was undone");
        }
    }

    /// <summary>
    ///     A submenu holding one or two rows is an extra click for nothing —
    ///     worse than no submenu, which is why Arrangements, Vocals and Help are
    ///     deliberately still flat and why the File menu's two audio verbs stayed
    ///     side by side.
    /// </summary>
    /// <remarks>
    ///     Two of the Tools menu's groups are a deliberate exception and are
    ///     named here rather than quietly tolerated: they group by TARGET (fret
    ///     hand positions / chart review), which is what makes that menu
    ///     readable, and the pair IS the group. They predate this rule. (Hand
    ///     shapes was the third until #478 gave it a third row.)
    /// </remarks>
    [Fact]
    public void NoSubmenuHidesFewerThanThreeRows()
    {
        string[] byTargetNotByCount = ["Menu_GroupFhp", "Menu_GroupReview"];

        var (tops, ns) = TopLevelMenus();

        var groups = tops.SelectMany(t => t.Descendants(ns + "MenuItem"))
            .Where(e => e.Elements(ns + "MenuItem").Any())
            .ToList();

        Assert.True(groups.Count >= 13, $"only {groups.Count} submenus in the whole bar");

        foreach (var group in groups)
        {
            var rows = group.Elements(ns + "MenuItem").Count();
            var floor = byTargetNotByCount.Any(k => Header(group).Contains(k, StringComparison.Ordinal)) ? 2 : 3;
            Assert.True(rows >= floor,
                $"the submenu {Header(group)} holds {rows} rows — one level for that few is an extra " +
                "click for nothing");
        }
    }

    private static string Header(XElement item) => (string?)item.Attribute("Header") ?? "(unnamed)";

    /// <summary>The bar's eight headers — the ones wearing the top-level class,
    /// so a nested item can never be mistaken for one.</summary>
    private static (List<XElement> Tops, XNamespace Ns) TopLevelMenus()
    {
        var doc = MainMenuSource.Document();
        var ns = doc.Root!.Name.Namespace;
        return ([.. doc.Descendants(ns + "MenuItem")
            .Where(e => (string?)e.Attribute("Classes") == "FireMenuTop")], ns);
    }

    private static HashSet<string> KeysIn(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName));
        return doc.Root!.Elements("data")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
