using System.Xml.Linq;
using DotCharter.Services.Infrastructure.Psarc;

namespace DotCharter.Tests.Services.Infrastructure.Psarc;

/// <summary>The tests of <see cref="RocksmithKeysTests" /> that need no package reader; its fixtures are in <c>RocksmithKeysTests.Fixtures.cs</c>.</summary>
public sealed partial class RocksmithKeysTests
{
    [Fact]
    public void The_config_values_are_the_keys_and_any_may_be_empty()
    {
        var config = TestRocksmithKeys.Config(TestRocksmithKeys.SyntheticToc, TestRocksmithKeys.SyntheticSngPc, null);
        config.PsarcToc = string.Join(' ', config.PsarcToc.Chunk(2).Select(pair => new string(pair)).ToArray()).ToLowerInvariant();
        using (RocksmithKeys.UseKeysForTesting(config))
        {
            var keys = RocksmithKeys.Current!;
            Assert.Equal(TestRocksmithKeys.SyntheticToc, keys.TocKey); // spaced lower-case hex reads too
            Assert.Equal(TestRocksmithKeys.SyntheticSngPc, keys.SngPcKey);
            Assert.Null(keys.SngMacKey);
            Assert.True(RocksmithKeys.CanWriteEncrypted);
            Assert.Null(RocksmithKeys.LoadProblem);
        }

        using (TestRocksmithKeys.UseKeys(null, TestRocksmithKeys.SyntheticSngPc, null))
            Assert.False(RocksmithKeys.CanWriteEncrypted);
    }

    [Fact]
    public void The_empty_default_section_means_no_keys_and_no_problem()
    {
        using var _ = RocksmithKeys.UseKeysForTesting(new AppConfig().RocksmithKeys);

        Assert.Null(RocksmithKeys.Current);
        Assert.False(RocksmithKeys.CanWriteEncrypted);
        Assert.Null(RocksmithKeys.LoadProblem);
    }

    [Theory]
    [InlineData("0011")]
    [InlineData("not hex at all")]
    [InlineData("0x00112233")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000000")]
    public void A_malformed_value_is_ignored_alone_and_named_without_its_content(string value)
    {
        var config = TestRocksmithKeys.Config(null, TestRocksmithKeys.SyntheticSngPc, null);
        config.PsarcToc = value;
        using var _ = RocksmithKeys.UseKeysForTesting(config);

        Assert.Null(RocksmithKeys.Current!.TocKey);
        Assert.Equal(TestRocksmithKeys.SyntheticSngPc, RocksmithKeys.Current.SngPcKey); // the good one still counts
        Assert.False(RocksmithKeys.CanWriteEncrypted);
        var problem = RocksmithKeys.LoadProblem!;
        Assert.Contains("RocksmithKeys.PsarcToc", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(value, problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(config.SngPc, problem, StringComparison.OrdinalIgnoreCase);
    }

    // The plugin's message for a package it cannot read (an encrypted one and no key); the
    // wording is neutral, so the resx stays public (#891).
    private const string EncryptedUnreadableKey = "Library_PackageUnreadable";

    [Fact]
    public void The_neutral_messages_are_in_every_language()
    {
        foreach (var culture in new[] { "", ".de-DE", ".ja-JP" })
        {
            var values = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"))
                .Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value"));
            foreach (var key in new[] { EncryptedUnreadableKey, "Dialog_Export_WrittenUnencrypted" })
                Assert.False(string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)), $"{key} is missing from Resources{culture}.resx");
        }

        Assert.Equal("This package can't be read.",
            FakeLocalization.English()[EncryptedUnreadableKey]);
    }
}
