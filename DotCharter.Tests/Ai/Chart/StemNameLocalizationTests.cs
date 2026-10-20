using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Chart;

/// <summary>
///     #636: a stem id ("guitar", "bass") is a file name and a prompt word, never a caption. The AI
///     tone review's "Measured on the {0} stem" printed it raw, so German and Japanese users read
///     English; every user-facing line now names the stem through <see cref="ToneStemNames" />, the
///     stem mixer's own <c>Stem_*</c> keys.
/// </summary>
public sealed class StemNameLocalizationTests
{
    [Theory]
    [InlineData(ToneStems.Guitar, "Stem_Guitar")]
    [InlineData(ToneStems.Bass, "Stem_Bass")]
    public void The_tone_reviews_evidence_names_the_stem_by_its_key(string stem, string key)
    {
        var loc = FakeLocalization.KeysOnly();

        Assert.Equal($"AiTones_FromStem({key})", AiToneText.Evidence(Item(stem, null), loc));
        Assert.Equal($"AiTones_FromStemRated({key}, 82)", AiToneText.Evidence(Item(stem, 82), loc));
    }

    [Fact]
    public void The_evidence_reads_as_the_stem_mixers_word_in_English() =>
        Assert.Equal("Measured on the Guitar stem",
            AiToneText.Evidence(Item(ToneStems.Guitar, null), FakeLocalization.English()));

    [Fact]
    public void A_stem_this_build_has_no_word_for_prints_its_id()
    {
        Assert.Null(ToneStemNames.KeyOf("kazoo"));
        Assert.Equal("AiTones_FromStem(kazoo)", AiToneText.Evidence(Item("kazoo", null), FakeLocalization.KeysOnly()));
    }

    /// <summary>A stem added to <see cref="ToneStems.All" /> needs a name in all three tables, or it
    /// falls back to its id in every language.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".de-DE")]
    [InlineData(".ja-JP")]
    public void Every_tone_stem_has_a_name_in_every_table(string culture)
    {
        var names = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", $"Resources{culture}.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => ((string?)d.Element("value") ?? "").Trim(), StringComparer.Ordinal);

        Assert.All(ToneStems.All, stem =>
        {
            var key = ToneStemNames.KeyOf(stem);
            Assert.True(key is not null, $"stem '{stem}' has no name key in ToneStemNames");
            Assert.True(names.TryGetValue(key!, out var value) && value.Length > 0, $"{key} is missing in Resources{culture}.resx");
        });
    }

    /// <summary>The recurrence: a localized line handed a stem id straight from a record. Every such
    /// argument in the service layer goes through <see cref="ToneStemNames.Display" />.</summary>
    [Fact]
    public void No_localized_line_in_the_services_is_handed_a_raw_stem_id()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services");
        var formats = 0;
        var raw = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            foreach (Match call in Regex.Matches(File.ReadAllText(file), @"\b(loc|_loc|_localization|Localization)\.Format\([^;]*;"))
            {
                formats++;
                var rawStem = Regex.IsMatch(call.Value, @"\.(Stem|Instrument)\b") &&
                              !call.Value.Contains("ToneStemNames.Display(", StringComparison.Ordinal);
                if (rawStem) raw.Add($"{Path.GetFileName(file)}: {Regex.Replace(call.Value, @"\s+", " ")}");
            }
        }

        Assert.True(formats >= 50, $"only {formats} localized Format calls found — the pattern no longer fits Services");
        Assert.True(raw.Count == 0, "stem ids handed to a localized line:\n  " + string.Join("\n  ", raw));
    }

    private static AiToneReviewItem Item(string stem, int? percent)
    {
        var (amp, cab) = GenerateFakes.RealGuitarGear();
        var tone = new RsTone(
            "Lead", "Lead", RsToneAuthoring.DefaultVolumeDb, [RsToneAuthoring.CleanDescriptorUIName],
            new RsAmp(amp, RsGearCatalog.Shared.GetCategory(amp), new Dictionary<string, float>(StringComparer.Ordinal)),
            new RsCab(cab, RsGearCatalog.Shared.GetCategory(cab), new Dictionary<string, float>(StringComparer.Ordinal)),
            [], [], []);
        var window = new ToneWindow(0, "Lead", 4000, 2000, 4000, 2000, 2000, ToneWindowClamp.None);
        var provenance = new ToneGenerationProvenance(window, stem, [ToneGenerationSources.Audio], "fake-model", "",
            percent, percent is null ? RsToneGenerator.UnratedNoRenderer : null, null);
        return new AiToneReviewItem("Lead", "Lead", window, tone, null, null, provenance);
    }
}
