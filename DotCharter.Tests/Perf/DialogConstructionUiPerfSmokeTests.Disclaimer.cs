// DialogConstructionUiPerfSmokeTests.Disclaimer: the Rocksmith/Ubisoft trademark disclaimer (#754) lays out on the About dialog and on the first-run wizard's welcome page.

using Avalonia.Controls;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    private const string DisclaimerKey = "Legal_RocksmithDisclaimer";

    [Fact]
    public async Task About_ShowsTheTrademarkDisclaimer()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == typeof(IAboutScreen));
            WithHostedDialog(entry, FakeVariantNames.Default, (dialog, _) =>
                AssertDisclaimerLaidOut(dialog, "AboutDisclaimer"));
        }, CancellationToken.None);
    }

    /// <summary>The wizard opens on its welcome page, which is where the line sits.</summary>
    [Fact]
    public async Task OnboardingWelcome_ShowsTheTrademarkDisclaimer()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            rig.Vm.Open();

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                AssertDisclaimerLaidOut(view, "OnboardingDisclaimer");
            });
        }, CancellationToken.None);
    }

    private static void AssertDisclaimerLaidOut(Control root, string name)
    {
        var line = Descendants<TextBlock>(root).FirstOrDefault(t => t.Name == name);
        Assert.True(line is not null, $"no TextBlock named {name}: the disclaimer is gone");
        var expected = LocalizationService.Instance[DisclaimerKey];
        Assert.False(string.IsNullOrWhiteSpace(expected), $"{DisclaimerKey} resolved to nothing");
        Assert.Equal(expected, line!.Text);
        Assert.True(line.IsEffectivelyVisible, $"{name} is hidden");
        Assert.True(line.Bounds.Width > 1 && line.Bounds.Height > 1, $"{name} laid out as {line.Bounds} — it rendered nothing");
        Assert.True(line.Foreground is not null, $"{name} resolved no foreground — its text theme did not apply");
    }
}
