using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The onboarding wizard is reached end to end, not merely registered —
///     the <c>CoopRigApplier</c> lesson, applied on day one. Every hop is read
///     from the REAL source: the DI registration, the dialog-service property
///     (and its Esc order), the lazy view registration, the startup hook in the
///     shell, and the Settings ▸ General row that re-runs it plus the row that
///     changes the startup screen. Then the view's every <c>Loc</c> key in all
///     three resx files, so a page cannot ship with a raw key on it.
/// </summary>
public sealed class OnboardingReachabilityTests
{
    [Fact]
    public void Registered_HeldByTheDialogService_AndBuiltLazilyByTheHost()
    {
        var app = string.Concat(UiSourceTree.EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .Select(UiSourceTree.ReadAllText));
        Assert.Contains("AddSingleton<DotCharter.UI.ViewModels.Onboarding.OnboardingWizardViewModel>(sp =>", app);
        // The tone page hands its pick to the pedalboard's preset browser so an
        // OPEN board re-syncs — resolved at pick time, so the wizard's own
        // construction never builds the rig.
        Assert.Contains("sp.GetService<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>()", app);

        var dialogs = Read("DotCharter.UI", "ViewModels", "Shell", "AppDialogService.cs");
        Assert.Contains("OnboardingWizardViewModel Onboarding { get; }", dialogs);

        // The calibration wizard opens on top of onboarding. It is a HOSTED dialog, and
        // the host sits above every legacy dialog and answers Esc before them, so it
        // must not be in the legacy list at all. (The settings page is a screen, not a
        // dialog, and is not in the list either.)
        var allDialogs = Between(dialogs, "AllDialogs =", "];");
        Assert.Contains("Onboarding,", allDialogs, StringComparison.Ordinal);
        Assert.DoesNotContain("LatencyWizard", allDialogs, StringComparison.Ordinal);
        Assert.DoesNotContain("            Settings,", allDialogs, StringComparison.Ordinal);
        Assert.Contains("Dialog<ILatencyWizardScreen, LatencyCalibrationWizardView>",
            Read("DotCharter.UI", "Views", "Shell", "ScreenCatalog.cs"));

        var container = Read("DotCharter.UI", "Views", "Shell", "DialogContainerView.axaml.cs");
        Assert.Contains("RegisterLazy(d.Onboarding, static () => new OnboardingWizardView())", container);
    }

    [Fact]
    public void OpenedAtStartup_ByTheShell_OnTheDeferredHook()
    {
        var shell = Read("DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs");
        Assert.Contains("_ui.Post(() => Dialogs.Onboarding.OpenAtStartupIfNeeded())", shell);
    }

    [Fact]
    public void ReRunnable_AndTheStartupScreenChangeable_FromSettingsGeneral()
    {
        var tab = Read("DotCharter.UI", "Views", "Settings", "GeneralSettingsView.axaml");
        Assert.Contains("Command=\"{Binding RunSetupAgainCommand}\"", tab);
        Assert.Contains("SelectedIndex=\"{Binding SelectedStartupScreenIndex, Mode=TwoWay}\"", tab);

        var vm = Read("DotCharter.Presentation", "Screens", "Settings", "GeneralSettingsViewModel.cs");
        Assert.Contains("RunSetupAgainCommand = new RelayCommand(() => _setup.Open());", vm);
        // The row round-trips through the General load/save like every other setting.
        Assert.Contains("SelectedStartupScreenIndex = config.StartupScreen == StartupScreen.Editor ? 1 : 0;", vm);
        Assert.Contains("config.StartupScreen = SelectedStartupScreenIndex == 1 ? StartupScreen.Editor : StartupScreen.PlayMode;", vm);
    }

    [Fact]
    public void TheWizardStillWritesThroughTheSharedAudioPage_NotItsOwnEngineCalls()
    {
        // The audio pages reuse AudioSettingsViewModel's Save (the ONE writer of
        // the live seat layout, via ApplyAudioConfiguration). A second
        // ApplyAudioConfiguration here would be the second writer the root
        // AGENTS.md forbids.
        var vm = Read("DotCharter.UI", "ViewModels", "Onboarding", "OnboardingWizardViewModel.cs");
        Assert.Contains("_audio.SaveCommand.Execute(null)", vm);
        Assert.DoesNotContain("ApplyAudioConfiguration(", SourceText.StripComments(vm));
        Assert.DoesNotContain("new DispatcherTimer", vm);
        Assert.DoesNotContain("Task.Delay", vm);
    }

    [Fact]
    public void EveryLocKeyOnTheView_ExistsInAllThreeLanguages()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "OnboardingWizardView.axaml")));
        var keys = Regex.Matches(markup, @"localization:Loc\s+([A-Za-z0-9_]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(keys.Count >= 40, $"only {keys.Count} Loc keys found on the wizard — the scan is broken");

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var names = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file))
                .Descendants("data").Select(d => d.Attribute("name")?.Value).ToHashSet(StringComparer.Ordinal);
            var missing = keys.Where(k => !names.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{file} lacks: {string.Join(", ", missing)}");
        }
    }

    private static string Between(string text, string start, string end)
    {
        var s = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, $"'{start}' not found");
        var e = text.IndexOf(end, s, StringComparison.Ordinal);
        Assert.True(e > s, $"'{end}' not found after '{start}'");
        return text[s..e];
    }

    private static string Read(params string[] parts) =>
        UiSourceTree.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. parts]));
}
