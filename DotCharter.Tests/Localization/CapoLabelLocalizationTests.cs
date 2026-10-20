using DotCharter.Localization;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.Localization;

/// <summary>
///     The song lists fold the capo into the tuning cell ("E Standard · Capo 2"), and the
///     Library's detail panel shows it as a chip. Both built "Capo {n}" in English — the
///     German lists said "Capo 2" beside a filter that already said "Kapo 2".
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class CapoLabelLocalizationTests
{
    [Fact]
    public void TheCapoLabel_FollowsTheUiLanguage()
    {
        try
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
            Assert.Equal("Capo 2", SongRowFacts.CapoLabel(2, LocalizationService.Instance));
            Assert.Equal("", SongRowFacts.CapoLabel(0, LocalizationService.Instance));

            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            Assert.Equal("Kapo 2", SongRowFacts.CapoLabel(2, LocalizationService.Instance));
            Assert.Equal("Drop D · Kapo 2", SongRowFacts.TuningCell("D A D G B E", 2, LocalizationService.Instance));
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }
}
