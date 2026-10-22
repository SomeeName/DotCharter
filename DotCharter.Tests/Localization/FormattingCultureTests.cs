using System.Globalization;
using DotCharter.Localization;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Localization;

/// <summary>
///     <para>
///         The UI LANGUAGE and the NUMBER/DATE FORMATTING are two separate
///         choices, and DotCharter only owns the first one. The second is a
///         Windows setting the user already made.
///     </para>
///     <para>
///         <c>LocalizationService.SetConfiguredCulture</c> overwrote both until
///         2026-08-29 — it assigned the resolved RESOURCE culture to
///         <see cref="CultureInfo.DefaultThreadCurrentCulture" /> as well as to
///         the UI one. Because <c>ResolveCulture</c> falls back to en-US for
///         anything outside the three built-in languages and the installed
///         packs, that meant a French, Spanish, Polish or Portuguese user — on
///         their own machine, with "system" selected — read <c>1,234.5</c> and
///         <c>8/29/2026</c>. And a SUPPORTED language is normalised to its base
///         culture, so a de-AT user was moved to de-DE and their months started
///         saying "Januar" instead of "Jänner".
///     </para>
///     <para>
///         Leaving the formatting culture alone is only safe because nothing on
///         the file or wire layers reads it any more — see
///         <see cref="InvariantWireFormatTests" /> and
///         <c>Infrastructure/InvariantWireParseLintTests</c>. Do not undo one
///         without the other.
///     </para>
/// </summary>
/// <remarks>
///     In the <c>LocalizationSingleton</c> collection: every case moves the
///     process-wide culture, and <c>SetConfiguredCulture</c> itself writes
///     <see cref="CultureInfo.DefaultThreadCurrentUICulture" />. Required by
///     <c>Infrastructure/CultureCollectionLintTests</c>. The source-scanning half
///     lives in <see cref="ResourceCultureFormatLintTests" />, which writes no
///     culture and so stays out of the collection.
/// </remarks>
[Collection("LocalizationSingleton")]
public sealed class FormattingCultureTests
{
    /// <summary>An English sentence with a decimal in it — both halves of the
    /// split are observable in one string.</summary>
    private const string NumericKey = "Status_DefaultStartSilenceUpdated";

    private static void WithOsCulture(string osCulture, Action body)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentCulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            var os = CultureInfo.GetCultureInfo(osCulture);
            CultureInfo.DefaultThreadCurrentCulture = os;
            CultureInfo.DefaultThreadCurrentUICulture = os;
            CultureInfo.CurrentCulture = os;
            CultureInfo.CurrentUICulture = os;
            body();
        }
        finally
        {
            // Ambient FIRST, then the service — and back to en-US explicitly,
            // which is the house rule `UI/RtlSupportTests` already follows: the
            // rest of the gate reads display strings off this singleton and is
            // NOT in this collection, so the resting state has to be the one it
            // expects.
            //
            // Restoring the CONFIGURED value ("system") instead is what a
            // careful-looking teardown does and it is wrong: "system" is
            // re-resolved against `CultureInfo.CurrentUICulture`, so on a German
            // box it resolves to de-DE and strands the singleton there. That
            // cost one run — the tuner popover's "IN TUNE" came back
            // "GESTIMMT" three classes later.
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUi;
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    /// <summary>
    ///     The headline case: a French machine, "system" language. French is not
    ///     one of the three built-ins, so the UI is English — and the numbers
    ///     must stay French.
    /// </summary>
    [Fact]
    public void AnUnsupportedUiLanguage_KeepsTheMachinesOwnNumberFormatting()
    {
        WithOsCulture("fr-FR", () =>
        {
            LocalizationService.Instance.SetConfiguredCulture(LocalizationService.SystemLanguage);

            // The UI fell back to English, which is the behaviour being kept.
            Assert.Equal("en-US", LocalizationService.Instance.Culture.Name);
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);

            // …and the FORMATTING culture was not touched.
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("fr-FR", CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("1,5", 1.5.ToString("0.###", CultureInfo.CurrentCulture));

            // Both halves at once, through the app's own formatter: an English
            // sentence carrying a French decimal comma.
            var text = LocalizationService.Instance.Format(NumericKey, 1.5);
            Assert.StartsWith("Default start silence", text, StringComparison.Ordinal);
            Assert.Contains("1,5", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     The second half of the same bug, and the one a German-speaking user
    ///     would actually notice: a REGIONAL variant of a supported language.
    ///     The resources are de-DE (there is no de-AT resx and there should not
    ///     be), but the calendar and the numbers stay Austrian.
    /// </summary>
    [Fact]
    public void ARegionalVariant_KeepsItsOwnCalendar_WhileReadingTheBaseResources()
    {
        WithOsCulture("de-AT", () =>
        {
            LocalizationService.Instance.SetConfiguredCulture(LocalizationService.SystemLanguage);

            Assert.Equal("de-DE", LocalizationService.Instance.Culture.Name);
            Assert.Equal("de-AT", CultureInfo.CurrentCulture.Name);

            // The month name is the whole point: Austria says Jänner, Germany
            // says Januar, and nothing about a resource table should decide it.
            var january = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)
                .ToString("MMMM", CultureInfo.CurrentCulture);
            Assert.Equal("Jänner", january);
            Assert.Equal("Januar",
                new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)
                    .ToString("MMMM", LocalizationService.Instance.Culture));
        });
    }

    /// <summary>
    ///     An EXPLICIT language choice is a choice about STRINGS only — picking
    ///     Japanese menus on a French machine must not renumber anything.
    /// </summary>
    [Fact]
    public void ChoosingAUiLanguageExplicitly_StillLeavesTheFormattingAlone()
    {
        WithOsCulture("fr-FR", () =>
        {
            LocalizationService.Instance.SetConfiguredCulture("ja-JP");

            Assert.Equal("ja-JP", LocalizationService.Instance.Culture.Name);
            Assert.Equal("ja-JP", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);

            var text = LocalizationService.Instance.Format(NumericKey, 1.5);
            Assert.Contains("1,5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("1.5", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     …and the string half still works, or the two assertions above would
    ///     be satisfied by a service that had stopped localizing entirely.
    /// </summary>
    [Fact]
    public void TheUiStringsStillFollowTheChosenLanguage()
    {
        WithOsCulture("fr-FR", () =>
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
            var english = LocalizationService.Instance["Settings_AudioDriver"];

            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            var german = LocalizationService.Instance["Settings_AudioDriver"];

            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.NotEqual(english, german);
        });
    }

    // ---- the call sites: a number on screen is formatted in the REGION --------

    /// <summary>
    ///     The owner's setup: English menus, an Austrian Windows region. A path's
    ///     best accuracy is printed by Get Ready and, one screen earlier, by Song
    ///     select's row. Get Ready formatted it with the UI LANGUAGE (99.9%) and the
    ///     row with the region (99,9%), so the same figure read two ways.
    /// </summary>
    [Fact]
    public void GetReadyAndSongSelect_PrintAPathsBestIdentically_InTheRegionsFormat()
    {
        WithOsCulture("de-AT", () =>
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
            Assert.Equal("en-US", LocalizationService.Instance.Culture.Name);

            var row = PlaySongItem.FromProject(@"C:\songs\a.rscp", false, LocalizationService.Instance);
            row.ApplyStats(new SongPlayStats("song", "Lead:Lead", SessionCount: 3, BestScore: 1000,
                BestAccuracy: 0.999, MasteryPercent: 50, LetterGrade.A, DateTime.UtcNow, "Song"));
            var getReady = PlayModeViewModel.FormatAccuracy(0.999);

            Assert.Equal(row.AccuracyLabel, getReady);
            Assert.Contains("99,9", getReady, StringComparison.Ordinal);
            Assert.Equal(PercentText.Format(0.999, 1, CultureInfo.GetCultureInfo("de-AT")), getReady);
        });
    }

    /// <summary>One percent spelling, and it is the region's: a space before the
    /// sign where the region writes one ("_" below: ICU picks which no-break space).</summary>
    [Theory]
    [InlineData("en-US", 0.999, 1, "99.9%")]
    [InlineData("de-AT", 0.999, 1, "99,9_%")]
    [InlineData("de-DE", 0.94, 0, "94_%")]
    [InlineData("ja-JP", 0.5, 0, "50%")]
    public void PercentText_FollowsTheRegionsSeparatorAndSpacing(string culture, double fraction, int decimals, string expected) =>
        Assert.Equal(expected,
            System.Text.RegularExpressions.Regex.Replace(
                PercentText.Format(fraction, decimals, CultureInfo.GetCultureInfo(culture)), @"\s", "_"));
}
