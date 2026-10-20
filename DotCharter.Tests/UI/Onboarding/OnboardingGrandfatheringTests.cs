using DotCharter.UI.ViewModels.Onboarding;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     Who counts as an EXISTING install, i.e. who must never be greeted by the
///     first-run wizard on upgrade.
///
///     <para>
///         The rule shipped in 0.49 with two signals — a psarc library folder or
///         a per-device latency calibration — and both are PLAYER evidence. A
///         CHARTER opens the app to edit charts: no psarc library, no live-input
///         calibration, and therefore, on the upgrade that shipped the wizard,
///         the first-run flow on top of an editor they had been using for months.
///         The signals below are all written by ordinary use and by nothing else.
///     </para>
///     <para>
///         The other half matters just as much: a genuinely FRESH config must
///         still be greeted, so nothing here may key off a field the app seeds on
///         its own first load.
///     </para>
/// </summary>
/// <remarks>The rig hands the wizard the process-wide LocalizationService, and a
/// Next off the Welcome page reaches SetConfiguredCulture — a culture write
/// through product code races exactly as a direct one does. See
/// <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingGrandfatheringTests
{
    /// <summary>Each of these on its own is proof the app has been used. Named
    /// one per case so a failure says which signal stopped working.</summary>
    public static TheoryData<string, Action<AppConfig>> UsedInstallSignals => new()
    {
        { "a psarc library folder", c => c.PsarcLibraryPath = @"D:\cdlc" },
        { "a calibrated device", c => c.LivePlayLatencyOffsetsByDevice["ASIO Focusrite"] = 4.2 },
        { "a recent project", c => c.RecentProjects.Add(new RecentProjectEntry { Path = @"D:\songs\a.dcp" }) },
        { "a last project path", c => c.LastProjectPath = @"D:\songs\a.dcp" },
        { "a remembered import folder", c => c.LastGpDir = @"D:\tabs" },
        { "a remembered export folder", c => c.LastExportDir = @"D:\out" }
    };

    /// <summary>
    ///     The bug: a charter who never set a library folder and never calibrated
    ///     had no signal at all, so the upgrade greeted them as a stranger.
    /// </summary>
    [Theory]
    [MemberData(nameof(UsedInstallSignals))]
    public void AnInstallThatHasBeenUsed_IsGrandfathered_NotGreeted(string signal, Action<AppConfig> use)
    {
        var rig = new OnboardingWizardRig(appVersion: "0.61.0");
        use(rig.Config.AppConfig);

        Assert.False(rig.Vm.OpenAtStartupIfNeeded(), $"{signal} should have grandfathered this install");
        Assert.False(rig.Vm.IsOpen);
        // Recorded, not re-decided every launch.
        Assert.True(rig.Config.AppConfig.OnboardingCompleted);
        Assert.Equal("0.61.0", rig.Config.AppConfig.OnboardingCompletedVersion);
    }

    /// <summary>
    ///     …and the guard is not simply "true". A config as the app first writes
    ///     it must still open the wizard — which is what stops a field the app
    ///     SEEDS on first load (there is at least one, and it is deliberately not
    ///     consulted) from being mistaken for evidence of use.
    /// </summary>
    [Fact]
    public void AFreshConfig_IsStillGreeted()
    {
        var rig = new OnboardingWizardRig();

        Assert.False(OnboardingWizardViewModel.IsExistingInstall(rig.Config.AppConfig));
        Assert.True(rig.Vm.OpenAtStartupIfNeeded());
        Assert.True(rig.Vm.IsOpen);
    }

    [Fact]
    public void ClosingAnUnfinishedFirstRun_DoesNotCountAsCompletingSetup()
    {
        var rig = new OnboardingWizardRig();
        Assert.True(rig.Vm.OpenAtStartupIfNeeded());
        // The shell persists these on close even without any editing or setup.
        rig.Config.AppConfig.WindowWidth = 1600;
        rig.Config.AppConfig.WindowHeight = 900;
        rig.Config.AppConfig.DockLayout = new DockLayoutState();

        Assert.False(OnboardingWizardViewModel.IsExistingInstall(rig.Config.AppConfig));
        Assert.False(rig.Config.AppConfig.OnboardingCompleted);
        Assert.True(rig.Vm.OpenAtStartupIfNeeded());
    }

    /// <summary>An empty string is not a remembered folder — the picker
    /// memories default to <c>string.Empty</c>, not null, so a
    /// null-check alone would grandfather everyone.</summary>
    [Fact]
    public void BlankPickerMemories_AreNotEvidenceOfUse()
    {
        var config = new AppConfig
        {
            LastGpDir = "",
            LastExportDir = "   ",
            LastProjectPath = ""
        };

        Assert.False(OnboardingWizardViewModel.IsExistingInstall(config));
    }
}
