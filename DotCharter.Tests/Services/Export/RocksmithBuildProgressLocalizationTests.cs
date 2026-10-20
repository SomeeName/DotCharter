using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Localization;
using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     <para>
///         The Rocksmith export dialog composes
///         <c>Dialog_Export_ProgressStep</c> — "Step {0} of {1}: {2}" — and
///         <c>{2}</c> used to be a hard-coded English fragment the SERVICE
///         supplied: <c>Report("Resolving audio")</c>. A German user read
///         "Schritt 1 von 6: Resolving audio" for the whole export, six times.
///     </para>
///     <para>
///         <see cref="RocksmithBuildStage" /> carries the identity now and the
///         dialog resolves it. These keys are a COMPUTED family
///         (<c>Dialog_Export_Stage_{Stage}</c>), so their completeness is pinned
///         by <c>UI/LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage</c>
///         — nothing in the repo spells the six names out. What is pinned HERE
///         is that they are actually TRANSLATED and that the builder cannot go
///         back to prose.
///     </para>
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class RocksmithBuildProgressLocalizationTests
{
    [Fact]
    public void EveryStage_ResolvesToARealTranslation_NotItsOwnKey()
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "de-DE", "ja-JP" })
            {
                LocalizationService.Instance.SetConfiguredCulture(language);

                foreach (var stage in Enum.GetValues<RocksmithBuildStage>())
                {
                    var key = new RocksmithBuildProgress(1, 6, stage).ResourceKey;
                    var text = LocalizationService.Instance[key];

                    Assert.False(string.IsNullOrWhiteSpace(text), $"{key} is blank in {language}.");
                    Assert.NotEqual(key, text); // GetString falls back to the KEY, silently.
                }
            }

            // …and the translations are not just the English copied across. If
            // they were, this whole finding would have been "fixed" by moving
            // the English into a resx.
            LocalizationService.Instance.SetConfiguredCulture("en-US");
            var english = Enum.GetValues<RocksmithBuildStage>()
                .Select(s => LocalizationService.Instance[$"Dialog_Export_Stage_{s}"]).ToList();
            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            var german = Enum.GetValues<RocksmithBuildStage>()
                .Select(s => LocalizationService.Instance[$"Dialog_Export_Stage_{s}"]).ToList();

            Assert.Equal(6, english.Count);
            Assert.All(english.Zip(german), pair => Assert.NotEqual(pair.First, pair.Second));
        }
        finally
        {
            // Ambient first, then the singleton back to en-US EXPLICITLY:
            // restoring the configured "system" re-resolved against the de-DE UI
            // culture this test had just set, and left every later test reading
            // German off the singleton (#596).
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            CultureInfo.CurrentUICulture = previousUi;
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    /// <summary>
    ///     The builder must never name a stage in a language again. A source
    ///     lint, because the fault is a string literal in a method body and the
    ///     type system is perfectly happy with it — <c>Report</c> took a
    ///     <see cref="string" /> for four releases.
    /// </summary>
    [Fact]
    public void TheBuilder_ReportsAStage_NeverASentence()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Export", "RocksmithPackageBuilder.cs");
        Assert.True(File.Exists(path), $"{path} moved — repoint this lint at it.");

        // Comments STRIPPED: the enum's own doc comment QUOTES the offending
        // call to explain what it replaced, and a `see cref` is exactly as
        // inert as a commented-out line. This lint failed on its own
        // documentation the first time it ran.
        var text = SourceText.StripComments(File.ReadAllText(path));

        // Non-vacuous: the calls it is guarding really are still there.
        var reports = Regex.Matches(text, @"\bReport\(RocksmithBuildStage\.");
        Assert.True(reports.Count == 6, $"expected 6 stage reports, found {reports.Count} — the lint lost its subject.");

        Assert.DoesNotContain("Report(\"", text, StringComparison.Ordinal);
    }
}
