using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     The Tools menu and the command palette say "Separate stems…" without naming a model.
///     It said "(Demucs)" long after the default moved to BS-RoFormer SW (#258): which model
///     runs depends on the GPU, the sign-in and the user's choice in Settings ▸ Stem models,
///     so no fixed name in the verb can be right.
/// </summary>
public sealed class SeparateStemsLabelTests
{
    private static readonly string[] ModelNames = ["Demucs", "RoFormer", "Roformer", "MDX", "htdemucs"];

    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void TheSeparateStemsVerb_NamesNoModel(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
        var value = XDocument.Load(path).Root!.Elements("data")
            .Single(d => (string?)d.Attribute("name") == "Menu_SeparateStems")
            .Element("value")!.Value;

        Assert.All(ModelNames, name => Assert.DoesNotContain(name, value, StringComparison.OrdinalIgnoreCase));
    }
}
