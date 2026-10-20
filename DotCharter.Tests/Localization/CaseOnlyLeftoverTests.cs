using System.Xml.Linq;

namespace DotCharter.Tests.Localization;

/// <summary>
///     #636: de-DE <c>Inspector_PassOther</c> read "Pass Other", the English label re-capitalised.
///     <c>LocalizationSweepTests.UntranslatedValues_DoNotGrow</c> compares values ORDINALLY, so a
///     copy that differs only in case is not counted as untranslated. A case-only rule over the
///     whole table would be noise (German capitalises nouns: "{0} Songs" is German), so this pins
///     the labels that shipped that way.
/// </summary>
public sealed class CaseOnlyLeftoverTests
{
    private static readonly string[] Keys = ["Inspector_PassOther"];

    [Theory]
    [InlineData(".de-DE")]
    [InlineData(".ja-JP")]
    public void These_labels_are_translated_not_recapitalised(string culture)
    {
        var english = Values("");
        var translated = Values(culture);

        Assert.All(Keys, key =>
        {
            Assert.True(english.ContainsKey(key) && translated.ContainsKey(key), $"{key} is gone — update this list");
            Assert.False(string.Equals(english[key], translated[key], StringComparison.OrdinalIgnoreCase),
                $"{key} in Resources{culture}.resx is the English \"{english[key]}\"");
        });
    }

    private static Dictionary<string, string> Values(string culture) =>
        XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => ((string?)d.Element("value") ?? "").Trim(), StringComparer.Ordinal);
}
