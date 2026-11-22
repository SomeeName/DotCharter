using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chart-view polish on the menu bar: the over-long labels are short with the full sentence in the tooltip (all three
///     languages), the Tools rows have distinct glyphs, the frequent commands that had no chord have one that
///     collides with nothing and shows on their rows, and the Rig / Tone designer rows say what they are for.
/// </summary>
public sealed class ChartViewPolishMenuTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";
    private static readonly string[] Cultures = ["Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx"];

    /// <summary>Menu row key, the full sentence's key and the tooltip key that must carry it.</summary>
    public static TheoryData<string, string, string> ShortenedLabels => new()
    {
        { "Menu_CopyStructure", "Menu_CopyStructureToArrangements", "Menu_CopyStructureToArrangements_Tip" },
        { "Menu_Generate_AcceptSuggestedArpeggios", "Menu_AcceptSuggestedArpeggios", "Menu_AcceptSuggestedArpeggios_Tip" },
        { "Menu_Generate_AcceptSuggested", "Menu_AcceptSuggestedHandShapes", "Menu_AcceptSuggestedHandShapes_Tip" },
        { "Menu_Generate_ChordShapeFromHandShape", "Menu_ApplyHandShapeTemplateToChords", "Menu_ApplyHandShapeTemplateToChords" }
    };

    [Theory]
    [MemberData(nameof(ShortenedLabels))]
    public void AShortenedLabel_HasTheFullSentenceInItsTooltip_InEveryLanguage(string shortKey, string fullKey, string tipKey)
    {
        foreach (var file in Cultures)
        {
            var resx = Resx(file);
            var shortText = Assert.Contains(shortKey, resx);
            var fullText = Assert.Contains(fullKey, resx);
            var tip = Assert.Contains(tipKey, resx);

            Assert.True(shortText.Length < fullText.Length, $"{file}: {shortKey} is not shorter than {fullKey}");
            Assert.Contains(fullText, tip, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ArrangementsMenu.axaml", "Menu_CopyStructure", "Menu_CopyStructureToArrangements_Tip")]
    [InlineData("ToolsMenu.axaml", "Menu_Generate_ChordShapeFromHandShape", "Menu_ApplyHandShapeTemplateToChords")]
    [InlineData("ToolsMenu.axaml", "Menu_Generate_AcceptSuggested", "Menu_AcceptSuggestedHandShapes_Tip")]
    [InlineData("ToolsMenu.axaml", "Menu_Generate_AcceptSuggestedArpeggios", "Menu_AcceptSuggestedArpeggios_Tip")]
    public void TheMenuRowShowsTheShortLabelAndCarriesTheTooltip(string file, string headerKey, string tipKey)
    {
        var row = Load(file).Descendants(Ns + "MenuItem").Single(e => Header(e) == headerKey);
        var source = row.ToString();
        Assert.Contains(tipKey, source);
    }

    [Theory]
    [InlineData("Menu_ToneDesigner")]
    [InlineData("Menu_Pedalboard")]
    public void RigAndToneDesignerSayWhatTheyAreFor(string headerKey)
    {
        var row = Load("ToolsMenu.axaml").Descendants(Ns + "MenuItem").Single(e => Header(e) == headerKey);
        Assert.Contains(headerKey + "_Tip", (string?)row.Attribute("ToolTip.Tip"));
        foreach (var file in Cultures) Assert.False(string.IsNullOrWhiteSpace(Resx(file)[headerKey + "_Tip"]), file);
    }

    [Fact]
    public void NoToolsRowSharesItsGlyphWithASiblingOrItsOwnGroup()
    {
        var offenders = new List<string>();
        void Walk(XElement menu, string? parentIcon)
        {
            var seen = new Dictionary<string, string>();
            foreach (var row in menu.Elements(Ns + "MenuItem"))
            {
                var icon = Icon(row);
                if (icon is not null)
                {
                    if (seen.TryGetValue(icon, out var other)) offenders.Add($"{Header(row)} and {other} share {Short(icon)}");
                    else seen[icon] = Header(row);
                    if (icon == parentIcon) offenders.Add($"{Header(row)} repeats its group's glyph {Short(icon)}");
                }

                Walk(row, icon);
            }
        }

        Walk(Load("ToolsMenu.axaml"), null);
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void TheToolsGroupsAndTheirRowsUseTheAppIconSet()
    {
        var tools = Load("ToolsMenu.axaml");
        foreach (var key in new[] { "IconPulse", "IconChat", "IconSheetMusic", "IconTune", "IconLightbulb" })
            Assert.Contains($"{{DynamicResource {key}}}", tools.ToString());
    }

    /// <summary>The commands that had no chord and are used all the time; the chord shows on their rows.</summary>
    [Theory]
    [InlineData("GenerateFhp", "ToolsMenu.axaml")]
    [InlineData("MergeNotesIntoChord", "EditMenu.axaml")]
    [InlineData("SplitChordsIntoNotes", "EditMenu.axaml")]
    [InlineData("FixChart", "ToolsMenu.axaml")]
    public void ANewShortcut_IsBound_Unique_AndShownOnItsRow(string property, string menuFile)
    {
        var chords = typeof(KeybindConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(KeyChord))
            .ToDictionary(p => p.Name, p => (KeyChord)p.GetValue(new KeybindConfig())!);
        var chord = chords[property];
        Assert.True(chord.IsBound);
        Assert.Single(chords.Values, c => c.Equals(chord));

        Assert.Contains($"ProjectState.Keybinds.{property},", Load(menuFile).ToString());
        foreach (var file in Cultures) Assert.False(string.IsNullOrWhiteSpace(Resx(file)["Keybind_" + property]), file);
    }

    private static string? Icon(XElement row)
    {
        var icon = row.Element(Ns + "MenuItem.Icon")?.Elements().FirstOrDefault();
        return (string?)icon?.Attribute("Data");
    }

    private static string Short(string icon) => icon.Length > 40 ? icon[..40] + "…" : icon;

    private static string Header(XElement row) =>
        Regex.Match((string?)row.Attribute("Header") ?? "", @"Loc (\w+)").Groups[1].Value;

    private static XElement Load(string file) => XDocument.Load(Path.Combine(MainMenuSource.MenusDirectory, file)).Root!;

    private static Dictionary<string, string> Resx(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
        return Regex.Matches(File.ReadAllText(path), @"<data name=""([^""]+)""[^>]*><value>(.*?)</value>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));
    }
}
