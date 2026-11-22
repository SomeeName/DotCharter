using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

public sealed class PasteReplacementResourceTests
{
    [Theory]
    [InlineData("Resources.resx", "Paste replaces what it lands on")]
    [InlineData("Resources.de-DE.resx", "Einfügen ersetzt vorhandene Inhalte")]
    [InlineData("Resources.ja-JP.resx", "貼り付けで既存の内容を置き換える")]
    public void PasteReplacement_HasItsLabelAndHint(string file, string label)
    {
        var document = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file));
        var values = document.Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);

        Assert.Equal(label, values["Settings_PasteReplacesExisting"]);
        Assert.False(string.IsNullOrWhiteSpace(values["Settings_Tip_PasteReplacesExisting"]));
    }
}
