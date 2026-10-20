using static DotCharter.Tests.Presentation.Settings.SplitTabStubs;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ General on the Presentation layer: load, the unsaved counter,
///     save (which applies the language and reloads the log filter), revert,
///     reset, the first-run wizard and search. The language write goes through a
///     recording localization, so nothing here moves the process's culture.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class GeneralSettingsViewModelTests
{
    private sealed record Rig(GeneralSettingsViewModel Vm, FakeConfigurationService Config,
        RecordingLocalization Loc, FakeSetup Setup, FakeAppLogger Logger);

    private static Rig NewRig(Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var loc = new RecordingLocalization();
        var setup = new FakeSetup();
        var logger = new FakeAppLogger();
        var vm = new GeneralSettingsViewModel(config, loc, setup, logger);
        vm.Load();
        return new Rig(vm, config, loc, setup, logger);
    }

    private static int IndexOf(string culture)
    {
        var values = new RecordingLocalization().LanguageOptions.Select(o => o.ConfigValue).ToList();
        var index = values.FindIndex(v => string.Equals(v, culture, StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"'{culture}' is not a language the app offers");
        return index;
    }

    [Fact]
    public void Load_ReadsThePersistedValues()
    {
        var rig = NewRig(c =>
        {
            c.LanguageCulture = "de-DE";
            c.StartupScreen = StartupScreen.Editor;
            c.CrashReportOptIn = true;
            c.EnableFileLogging = false;
            c.MinimumLogLevel = AppLogLevel.Warning;
            c.LogFileMaxBytes = 3 * 1024 * 1024;
            c.EnabledLogCategories = AppLogCategory.Audio | AppLogCategory.Errors;
        });

        Assert.Equal(IndexOf("de-DE"), rig.Vm.SelectedLanguageIndex);
        Assert.Equal(1, rig.Vm.SelectedStartupScreenIndex);
        Assert.True(rig.Vm.CrashReportOptIn);
        Assert.False(rig.Vm.EnableFileLogging);
        Assert.Equal((int)AppLogLevel.Warning, rig.Vm.MinimumLogLevelIndex);
        Assert.Equal(3.0, rig.Vm.LogFileMaxMb, 6);
        Assert.True(rig.Vm.LogAudio && rig.Vm.LogErrors);
        Assert.False(rig.Vm.LogApplication || rig.Vm.LogProject || rig.Vm.LogExternal);
        Assert.Equal(rig.Loc.LanguageOptions.Count, rig.Vm.LanguageOptions.Count);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>Zero and blank persisted values read as the shipped defaults, never as a zero the row cannot hold.</summary>
    [Fact]
    public void Load_FallsBackToDefaults_ForUnsetValues()
    {
        var rig = NewRig(c =>
        {
            c.LogFilePath = "  ";
            c.LogFileMaxBytes = 0;
            c.LogFileMaxRollovers = 0;
            c.LogRepeatCoalesceWindowMs = 0;
            c.LogRepeatSummaryIntervalMs = 0;
            c.LanguageCulture = "xx-NOPE";
        });

        Assert.Equal("logs/dotcharter.log", rig.Vm.LogFilePath);
        Assert.Equal(10.0, rig.Vm.LogFileMaxMb);
        Assert.Equal(5, rig.Vm.LogFileMaxRollovers);
        Assert.Equal(2000, rig.Vm.LogRepeatCoalesceWindowMs);
        Assert.Equal(1000, rig.Vm.LogRepeatSummaryIntervalMs);
        Assert.Equal(0, rig.Vm.SelectedLanguageIndex);
    }

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IGeneralSettingsScreen>(
            () => NewRig().Vm, atLeast: 26, nameof(IGeneralSettingsScreen.IsAdvancedOpen));

    [Fact]
    public void OpeningAdvanced_IsNotAnUnsavedChange()
    {
        var rig = NewRig();
        rig.Vm.IsAdvancedOpen = true;
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void AnEdit_RaisesUnsavedSettings()
    {
        var vm = NewRig().Vm;
        SplitTabAssert.AnEditRaisesUnsavedSettings(vm, () => vm.LogAudio = !vm.LogAudio);
    }

    [Fact]
    public void Save_PersistsEverything_AppliesTheLanguage_AndReloadsTheLogFilter()
    {
        var rig = NewRig();
        rig.Vm.SelectedLanguageIndex = IndexOf("ja-JP");
        rig.Vm.SelectedStartupScreenIndex = 1;
        rig.Vm.CrashReportOptIn = true;
        rig.Vm.MinimumLogLevelIndex = (int)AppLogLevel.Debug;
        rig.Vm.LogFileMaxMb = 2;
        rig.Vm.LogFilePath = "  ";
        rig.Vm.LogApplication = false;
        rig.Vm.LogExternal = false;

        rig.Vm.Save();

        var c = rig.Config.AppConfig;
        Assert.Equal("ja-JP", c.LanguageCulture);
        Assert.Equal(StartupScreen.Editor, c.StartupScreen);
        Assert.True(c.CrashReportOptIn);
        Assert.Equal(AppLogLevel.Debug, c.MinimumLogLevel);
        Assert.Equal(2 * 1024 * 1024, c.LogFileMaxBytes);
        Assert.Equal("logs/dotcharter.log", c.LogFilePath);
        Assert.Equal(AppLogCategory.None, c.EnabledLogCategories & (AppLogCategory.Application | AppLogCategory.External));
        Assert.NotEqual(AppLogCategory.None, c.EnabledLogCategories & AppLogCategory.Audio);
        Assert.Equal(["ja-JP"], rig.Loc.CulturesSet);
        Assert.True(rig.Logger.ReloadSettingsCalled);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>The eleven category switches are ONE flags field; a round trip must keep exactly the set the user left on.</summary>
    [Fact]
    public void TheLogCategories_RoundTripAsOneFlagsField()
    {
        var mask = AppLogCategory.Import | AppLogCategory.History | AppLogCategory.External;
        var rig = NewRig(c => c.EnabledLogCategories = mask);

        rig.Vm.Save();

        Assert.Equal(mask, rig.Config.AppConfig.EnabledLogCategories);
    }

    [Fact]
    public void Revert_AndReset_PutThePersistedValuesBack()
    {
        foreach (var verb in new Action<GeneralSettingsViewModel>[] { v => v.Revert(), v => v.Reset() })
        {
            var rig = NewRig(c => c.CrashReportOptIn = true);
            rig.Vm.CrashReportOptIn = false;
            rig.Vm.SelectedStartupScreenIndex = 1;

            verb(rig.Vm);

            Assert.True(rig.Vm.CrashReportOptIn);
            Assert.Equal(0, rig.Vm.SelectedStartupScreenIndex);
            Assert.Empty(rig.Vm.UnsavedSettings);
            Assert.Empty(rig.Loc.CulturesSet);
        }
    }

    [Fact]
    public void RunSetupAgain_OpensTheFirstRunWizard()
    {
        var rig = NewRig();
        rig.Vm.RunSetupAgainCommand.Execute(null);
        Assert.Equal(1, rig.Setup.Opened);
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void Search_HidesWhatDoesNotMatch_AndOpensAdvancedForALoggingHit()
    {
        var rig = NewRig();
        var rotation = rig.Vm.SearchGroups.SelectMany(g => g.Rows).Single(r => r.Key == "general.rotation");

        rig.Vm.ApplySearch(rotation.Label);

        Assert.DoesNotContain("general.rotation", rig.Vm.HiddenKeys);
        Assert.Contains("general.language", rig.Vm.HiddenKeys);
        Assert.Contains(GeneralSettingsViewModel.GroupKey(0), rig.Vm.HiddenKeys);
        Assert.DoesNotContain(GeneralSettingsViewModel.GroupKey(2), rig.Vm.HiddenKeys);
        Assert.True(rig.Vm.IsAdvancedOpen);

        rig.Vm.ApplySearch("");
        Assert.Empty(rig.Vm.HiddenKeys);
    }

    [Fact]
    public void AHitOutsideAdvanced_LeavesItClosed()
    {
        var rig = NewRig();
        var language = rig.Vm.SearchGroups[0].Rows[0];

        rig.Vm.ApplySearch(language.Label);

        Assert.DoesNotContain(language.Key, rig.Vm.HiddenKeys);
        Assert.False(rig.Vm.IsAdvancedOpen);
    }

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IGeneralSettingsScreen), NewRig().Vm));
}
