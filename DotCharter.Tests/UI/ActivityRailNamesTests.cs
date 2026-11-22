using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Activity-rail rows use matching captions and accessible names in every shipped language.
///     Settings keeps its full accessible name and uses a short caption for the narrow rail slot.
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
            var nameKey = row.Groups["name"].Value;
            var captionKey = row.Groups["caption"].Value;
            if (captionKey == "Nav_Settings_Rail")
            {
                Assert.Equal("Nav_Settings", nameKey);
                continue;
            }

            var name = strings[nameKey];
            var caption = strings[captionKey];
            Assert.True(name.Contains(caption, StringComparison.CurrentCultureIgnoreCase),
                $"[{(culture == "" ? "en" : culture)}] the rail row captioned \"{caption}\" is named \"{name}\"");
        }
    }

    [Fact]
    public void Settings_UsesTheShortRailCaptionAndFullAccessibleName()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Shell", "ActivityRailView.axaml")));
        var rail = XDocument.Parse(markup);
        var settings = rail.Descendants().Single(e =>
            (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "RailSettings");
        var caption = settings.Descendants().Single(e => e.Name.LocalName == "TextBlock"
            && (string?)e.Attribute("Classes") == "FireRailCaption");

        Assert.Equal("{localization:Loc Nav_Settings_Rail}", (string?)caption.Attribute("Text"));
        Assert.Equal("{localization:Loc Nav_Settings}", (string?)settings.Attribute("AutomationProperties.Name"));
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
