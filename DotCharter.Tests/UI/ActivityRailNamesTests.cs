using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Every activity-rail row's accessible name includes the caption it shows. The Settings row said
///     "Settings" and was named "Project settings" (tooltip too) — it opens the app's Settings, and a
///     screen reader or UI Automation looking for "Settings" found only the caption text, not the
///     button (live app, 2026-09-23). Checked in every shipped language.
/// </summary>
public sealed class ActivityRailNamesTests
{
    public static TheoryData<string> Cultures => ["", "de-DE", "ja-JP"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void EveryRailRow_IsNamedAfterWhatItSays(string culture)
    {
        var root = TestPaths.RepoRoot();
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml")));
        var strings = LoadStrings(root, culture);

        var rows = Regex.Matches(markup,
            @"<(RadioButton|ToggleButton)\s[^>]*?(?:Classes=""FireRailItem""|Theme=""\{StaticResource FireRailToggle\}"")[^>]*?AutomationProperties\.Name=""\{localization:Loc (?<name>\w+)\}""[^>]*>.*?FireRailCaption"" Text=""\{localization:Loc (?<caption>\w+)\}""",
            RegexOptions.Singleline);
        Assert.True(rows.Count >= 5, $"found {rows.Count} rail rows — the pattern no longer matches the markup");

        foreach (Match row in rows)
        {
            var name = strings[row.Groups["name"].Value];
            var caption = strings[row.Groups["caption"].Value];
            Assert.True(name.Contains(caption, StringComparison.CurrentCultureIgnoreCase),
                $"[{(culture == "" ? "en" : culture)}] the rail row captioned \"{caption}\" is named \"{name}\"");
        }
    }

    /// <summary>The window's top-left corner carries the app's mark on every surface (owner,
    /// 2026-09-23): the rail's top cell where there is a rail, the window caption where there is not.</summary>
    [Fact]
    public void TheWindowsTopLeftCorner_CarriesTheAppMark_OnEverySurface()
    {
        var root = TestPaths.RepoRoot();
        var rail = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml")));
        var mark = Regex.Match(rail, @"<Border[^>]*x:Name=""RailAppMark""[\s\S]*?</Border>");
        Assert.True(mark.Success, "the rail has no app mark");
        Assert.Contains("app-icon.png", mark.Value, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"AppMark_PointerPressed\"", mark.Value, StringComparison.Ordinal);
        Assert.True(rail.IndexOf("RailAppMark", StringComparison.Ordinal) < rail.IndexOf("RailHome", StringComparison.Ordinal),
            "the app mark is not the rail's first cell");

        var window = MainWindowSource.ReadAllText();
        var caption = window[window.IndexOf("x:Name=\"WindowCaption\"", StringComparison.Ordinal)..];
        Assert.Contains("app-icon.png", caption[..caption.IndexOf("</Grid>", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static Dictionary<string, string> LoadStrings(string root, string culture)
    {
        var file = culture == "" ? "Resources.resx" : $"Resources.{culture}.resx";
        return XDocument.Load(Path.Combine(root, "DotCharter.Localization", file)).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
    }
}
