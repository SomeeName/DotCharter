using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     The Rocksmith/Ubisoft trademark disclaimer (#754): one resx key, worn by the About
///     dialog and the first-run wizard's welcome page, and the same English sentence in the
///     repository README and the release zip's README.txt. The headless half
///     (<c>DialogConstructionUiPerfSmokeTests.Disclaimer</c>) proves both views lay it out;
///     this half keeps it in the default gate.
/// </summary>
public sealed class TrademarkDisclaimerTests
{
    private const string Key = "Legal_RocksmithDisclaimer";
    private const string English = "Not affiliated with or endorsed by Ubisoft. Rocksmith is a trademark of Ubisoft Entertainment.";

    private static readonly string[] Cultures = ["Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx"];

    private static IEnumerable<(string Name, string Value)> Values(string file) =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file)).Root!.Elements("data")
            .Select(d => ((string?)d.Attribute("name") ?? string.Empty, d.Element("value")?.Value ?? string.Empty));

    [Fact]
    public void TheEnglishDisclaimer_IsTheAgreedSentence() =>
        Assert.Equal(English, Values("Resources.resx").Single(v => v.Name == Key).Value);

    /// <summary>A translation may reword the sentence but must keep the mark and its owner exact.</summary>
    [Theory]
    [InlineData("Resources.resx")]
    [InlineData("Resources.de-DE.resx")]
    [InlineData("Resources.ja-JP.resx")]
    public void EveryCulture_NamesTheMarkAndItsOwner(string file)
    {
        var value = Values(file).Single(v => v.Name == Key).Value;
        Assert.Contains("Rocksmith", value, StringComparison.Ordinal);
        Assert.Contains("Ubisoft Entertainment", value, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Nothing a user reads may name Ubisoft except the disclaimer — a string such as
    ///     "Ubisoft-approved" would imply the affiliation the disclaimer denies.
    /// </summary>
    [Fact]
    public void OnlyTheDisclaimer_NamesUbisoft()
    {
        var offenders = Cultures
            .SelectMany(file => Values(file)
                .Where(v => v.Name != Key && v.Value.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase))
                .Select(v => $"{file}:{v.Name}"))
            .ToList();
        Assert.True(offenders.Count == 0, "strings naming Ubisoft outside the disclaimer: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("DotCharter.UI/Views/Dialogs/AboutDialogView.axaml")]
    [InlineData("DotCharter.UI/Views/Dialogs/OnboardingWizardView.axaml")]
    public void TheView_WearsTheDisclaimer(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath));
        Assert.Contains("{localization:Loc " + Key + "}", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("tools/release/New-Release.ps1")]
    public void TheReadme_CarriesTheSentence(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath));
        Assert.Contains(English, text, StringComparison.Ordinal);
    }
}
