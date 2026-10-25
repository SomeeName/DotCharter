using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.UI.Theming;
using static DotCharter.Tests.Presentation.Settings.AppearanceStubs;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Appearance on the Presentation layer, over fake seams: load,
///     the unsaved counter, save (both scale modes; palette versus free-form
///     colours), revert and reset, the preview-dirty gate, the reload after a
///     backup import, search and the contract.
/// </summary>
public sealed class AppearanceSettingsViewModelTests
{
    private sealed record Rig(
        AppearanceSettingsViewModel Vm,
        FakeConfigurationService Config,
        FakeTheme Theme,
        FakeScale Scale,
        SettingsSession Session,
        FakeAppLogger Log);

    private static Rig NewRig(Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var theme = new FakeTheme();
        var scale = new FakeScale();
        var session = new SettingsSession();
        var log = new FakeAppLogger();
        var vm = new AppearanceSettingsViewModel(config, FakeLocalization.English(), theme, scale, session, log);
        vm.Load();
        return new Rig(vm, config, theme, scale, session, log);
    }

    private static AppearanceColorItemViewModel Row(AppearanceSettingsViewModel vm, string tokenKey) =>
        vm.ColorGroups.Cast<AppearanceColorGroupViewModel>().SelectMany(g => g.Rows).Single(r => r.TokenKey == tokenKey);

    // ── load ────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_ReadsThePaletteAndTheScale_AndPreviewsNothing()
    {
        var r = NewRig(c =>
        {
            c.StringColorPresetId = "IBM";
            c.UiScaleAuto = false;
            c.UiScale = 1.5;
        });

        Assert.Equal(2, r.Vm.SelectedStringPaletteIndex);
        Assert.Equal(3, r.Vm.UiScaleChoiceIndex);
        Assert.Equal(0, r.Theme.PaletteCalls);
        Assert.Equal(0, r.Scale.ApplyCalls);
        Assert.False(r.Scale.IsAutoMode);
        Assert.Equal(1.5, r.Scale.AppliedFactor);
        Assert.Equal(1.5.ToString("P0", CultureInfo.CurrentCulture), r.Vm.UiScaleReadout);
        Assert.Empty(r.Vm.UnsavedSettings);
        Assert.False(r.Vm.IsPreviewDirty);
    }

    /// <summary>Under Auto the readout says what Auto chose, not the manual factor on file.</summary>
    [Fact]
    public void Load_UnderAuto_ShowsTheDerivedFactor()
    {
        var r = NewRig(c =>
        {
            c.UiScaleAuto = true;
            c.UiScale = 1.75;
        });

        Assert.Equal(0, r.Vm.UiScaleChoiceIndex);
        Assert.True(r.Scale.IsAutoMode);
        Assert.Equal(FakeScale.AutoDerived.ToString("P0", CultureInfo.CurrentCulture), r.Vm.UiScaleReadout);
    }

    /// <summary>Home's moving hero moved here from Graphics: opt-in, off on a fresh config, saved when switched on.</summary>
    [Fact]
    public void TheAnimatedHomeHero_IsOffByDefault_AndSaves()
    {
        var r = NewRig();
        Assert.False(r.Vm.AnimatedHomeHero);

        r.Vm.AnimatedHomeHero = true;
        Assert.Contains("appearance.AnimatedHomeHero", r.Vm.UnsavedSettings);
        r.Vm.Save();

        Assert.True(r.Config.AppConfig.AnimatedHomeHero);
        Assert.Empty(r.Vm.UnsavedSettings);
    }

    [Fact]
    public void AnUnknownPalette_ReadsAsTheDefault() =>
        Assert.Equal(0, NewRig(c => c.StringColorPresetId = "gone").Vm.SelectedStringPaletteIndex);

    /// <summary>A reload must not clobber the preview: the theme on screen is left as it is.</summary>
    [Fact]
    public void Load_NeverReappliesTheTheme()
    {
        var r = NewRig();
        Row(r.Vm, ThemeTokens.AppBackground).Hex = "#123456";

        r.Vm.Load();

        Assert.Equal(0, r.Theme.PersistedCalls);
        Assert.Equal("#123456", r.Theme.Color(ThemeTokens.AppBackground));
    }

    // ── counting and previewing ─────────────────────────────────────────────

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IAppearanceSettingsScreen>(() => NewRig().Vm, atLeast: 2);

    [Fact]
    public void AnEditRaisesUnsavedSettings()
    {
        var r = NewRig();
        SplitTabAssert.AnEditRaisesUnsavedSettings(r.Vm, () => r.Vm.UiScaleChoiceIndex = 2);
    }

    /// <summary>The palette previews live and KEEPS the customized colours; it counts as one setting.</summary>
    [Fact]
    public void PickingAPalette_PreviewsIt_AndKeepsTheCustomColours()
    {
        var r = NewRig();
        Row(r.Vm, ThemeTokens.AppBackground).Hex = "#123456";

        r.Vm.SelectedStringPaletteIndex = 1;

        Assert.Equal(1, r.Theme.PaletteCalls);
        Assert.Equal(0, r.Theme.ResetCalls);
        Assert.Equal("#123456", r.Theme.Color(ThemeTokens.AppBackground));
        Assert.Equal("#D55E00", r.Theme.Color(ThemeTokens.String1));
        Assert.Equal(["appearance.SelectedStringPaletteIndex"], r.Vm.UnsavedSettings);
        Assert.True(r.Vm.IsPreviewDirty);
    }

    [Fact]
    public void PickingAScale_PreviewsIt_AndTheReadoutFollows()
    {
        var r = NewRig(c => c.UiScaleAuto = true);

        r.Vm.UiScaleChoiceIndex = 4;

        Assert.Equal(1, r.Scale.ApplyCalls);
        Assert.False(r.Scale.IsAutoMode);
        Assert.Equal(1.75.ToString("P0", CultureInfo.CurrentCulture), r.Vm.UiScaleReadout);
        Assert.Equal(["appearance.UiScaleChoiceIndex"], r.Vm.UnsavedSettings);
    }

    /// <summary>A colour edit previews and is reverted by Cancel, but the footer does not count it.</summary>
    [Fact]
    public void EditingAColour_PreviewsIt_WithoutCountingIt()
    {
        var r = NewRig();
        var row = Row(r.Vm, ThemeTokens.Accent);

        row.Hex = "#abcdef";

        Assert.Equal("#ABCDEF", r.Theme.Color(ThemeTokens.Accent));
        Assert.Equal("#ABCDEF", row.Hex);
        Assert.Empty(r.Vm.UnsavedSettings);
        Assert.True(r.Vm.IsPreviewDirty);
    }

    [Fact]
    public void AnUnparsableColour_ChangesNothing()
    {
        var r = NewRig();
        var row = Row(r.Vm, ThemeTokens.Accent);

        row.Hex = "not a colour";

        Assert.Equal(Base, r.Theme.Color(ThemeTokens.Accent));
        Assert.Equal(Base, row.Hex);
    }

    // ── the preview-dirty gate ──────────────────────────────────────────────

    /// <summary>Re-applying the theme costs 30–80 ms; leaving Settings with nothing previewed must not pay it.</summary>
    [Fact]
    public void Revert_WithNothingPreviewed_DoesNotReapplyTheTheme()
    {
        var r = NewRig();
        r.Vm.UiScaleChoiceIndex = 2;

        r.Vm.Revert();

        Assert.Equal(0, r.Theme.PersistedCalls);
    }

    [Fact]
    public void Revert_AfterAColourPreview_PutsThePersistedThemeBack()
    {
        var r = NewRig(c =>
        {
            c.StringColorPresetId = "okabe-ito";
            c.ThemeColorOverrides = new Dictionary<string, string> { [ThemeTokens.Accent] = "#222222" };
        });
        Row(r.Vm, ThemeTokens.Accent).Hex = "#999999";

        r.Vm.Revert();

        Assert.Equal(1, r.Theme.PersistedCalls);
        Assert.Equal("okabe-ito", r.Theme.LastPersisted!.Value.PaletteId);
        Assert.Equal("#222222", r.Theme.Color(ThemeTokens.Accent));
        Assert.Equal("#222222", Row(r.Vm, ThemeTokens.Accent).Hex);
        Assert.False(r.Vm.IsPreviewDirty);

        r.Vm.Revert();
        Assert.Equal(1, r.Theme.PersistedCalls);
    }

    [Fact]
    public void Revert_PutsThePaletteAndTheScaleBack()
    {
        var r = NewRig(c =>
        {
            c.UiScaleAuto = false;
            c.UiScale = 1.25;
        });
        r.Vm.SelectedStringPaletteIndex = 2;
        r.Vm.UiScaleChoiceIndex = 5;

        r.Vm.Revert();

        Assert.Equal(0, r.Vm.SelectedStringPaletteIndex);
        Assert.Equal(2, r.Vm.UiScaleChoiceIndex);
        Assert.Equal(1.25, r.Scale.AppliedFactor);
        Assert.False(r.Scale.IsAutoMode);
        Assert.Empty(r.Vm.UnsavedSettings);
    }

    /// <summary>Reset section restores through the same path a reopen does, including the applied theme.</summary>
    [Fact]
    public void Reset_IsRevert_ThroughTheLoadPath()
    {
        var r = NewRig();
        r.Vm.SelectedStringPaletteIndex = 1;
        r.Vm.ResetColorsCommand.Execute(null);

        r.Vm.Reset();

        Assert.Equal(1, r.Theme.PersistedCalls);
        Assert.Equal(0, r.Vm.SelectedStringPaletteIndex);
        Assert.Equal(Base, r.Theme.Color(ThemeTokens.String1));
        Assert.Empty(r.Vm.UnsavedSettings);
    }

    [Fact]
    public void ResetColors_DropsTheCustomColours_KeepsThePalette_AndMarksAPreview()
    {
        var r = NewRig();
        r.Vm.SelectedStringPaletteIndex = 1;
        Row(r.Vm, ThemeTokens.AppBackground).Hex = "#123456";

        r.Vm.ResetColorsCommand.Execute(null);

        Assert.Equal(1, r.Theme.ResetCalls);
        Assert.Equal(Base, r.Theme.Color(ThemeTokens.AppBackground));
        Assert.Equal(Base, Row(r.Vm, ThemeTokens.AppBackground).Hex);
        Assert.Equal("#D55E00", r.Theme.Color(ThemeTokens.String1));
        Assert.True(r.Vm.IsPreviewDirty);
    }

    // ── save ────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_WithAPaletteActive_PersistsItsId_AndLeavesTheStringColoursOutOfTheOverrides()
    {
        var r = NewRig();
        r.Vm.SelectedStringPaletteIndex = 1;
        Row(r.Vm, ThemeTokens.AppBackground).Hex = "#123456";

        r.Vm.Save();

        Assert.Equal("okabe-ito", r.Config.AppConfig.StringColorPresetId);
        Assert.Equal("#123456", r.Config.AppConfig.ThemeColorOverrides[ThemeTokens.AppBackground]);
        Assert.DoesNotContain(r.Config.AppConfig.ThemeColorOverrides.Keys, StringColorPresets.IsStringToken);
        Assert.Empty(r.Vm.UnsavedSettings);
        Assert.False(r.Vm.IsPreviewDirty);
    }

    /// <summary>With the default palette the string colours are the user's own, so they are free-form overrides.</summary>
    [Fact]
    public void Save_WithTheDefaultPalette_KeepsHandPickedStringColours()
    {
        var r = NewRig();
        Row(r.Vm, ThemeTokens.String1).Hex = "#654321";

        r.Vm.Save();

        Assert.Equal("default", r.Config.AppConfig.StringColorPresetId);
        Assert.Equal("#654321", r.Config.AppConfig.ThemeColorOverrides[ThemeTokens.String1]);
    }

    [Fact]
    public void Save_AManualScale_PersistsTheFactorAndLeavesAuto()
    {
        var r = NewRig(c => c.UiScaleAuto = true);
        r.Vm.UiScaleChoiceIndex = 2;

        r.Vm.Save();

        Assert.False(r.Config.AppConfig.UiScaleAuto);
        Assert.Equal(1.25, r.Config.AppConfig.UiScale);
    }

    /// <summary>Under Auto the manual factor on file is kept, so a later percentage pick restores the user's own.</summary>
    [Fact]
    public void Save_Auto_KeepsTheManualFactorOnFile()
    {
        var r = NewRig(c =>
        {
            c.UiScaleAuto = false;
            c.UiScale = 1.75;
        });
        r.Vm.UiScaleChoiceIndex = 0;

        r.Vm.Save();

        Assert.True(r.Config.AppConfig.UiScaleAuto);
        Assert.Equal(1.75, r.Config.AppConfig.UiScale);
    }

    [Fact]
    public void Save_LogsWhatItWrote()
    {
        var r = NewRig();
        r.Vm.SelectedStringPaletteIndex = 2;

        r.Vm.Save();

        var line = Assert.Single(r.Log.Entries, e => e.Message.StartsWith("Appearance saved.", StringComparison.Ordinal));
        Assert.Contains("stringColors=ibm", line.Message, StringComparison.Ordinal);
        Assert.Contains("auto=", line.Message, StringComparison.Ordinal);
    }

    /// <summary>After a save the applied theme IS the persisted one, so leaving must not re-apply it.</summary>
    [Fact]
    public void Save_ClosesThePreview()
    {
        var r = NewRig();
        Row(r.Vm, ThemeTokens.Accent).Hex = "#333333";

        r.Vm.Save();
        r.Vm.Revert();

        Assert.Equal(0, r.Theme.PersistedCalls);
    }

    // ── reload after a backup import ────────────────────────────────────────

    [Fact]
    public void AReloadRequest_AppliesTheImportedTheme()
    {
        var r = NewRig();
        Row(r.Vm, ThemeTokens.Accent).Hex = "#333333";
        r.Config.AppConfig.StringColorPresetId = "okabe-ito";
        r.Config.AppConfig.ThemeColorOverrides = new Dictionary<string, string> { [ThemeTokens.AppBackground] = "#0A0B0C" };

        r.Session.RequestReload();

        Assert.Equal(1, r.Theme.PersistedCalls);
        Assert.Equal("#0A0B0C", r.Theme.Color(ThemeTokens.AppBackground));
        Assert.Equal("#0A0B0C", Row(r.Vm, ThemeTokens.AppBackground).Hex);
        Assert.Equal(Base, r.Theme.Color(ThemeTokens.Accent));
        Assert.False(r.Vm.IsPreviewDirty);
    }

    [Fact]
    public void Dispose_StopsFollowingTheSession()
    {
        var r = NewRig();
        r.Vm.Dispose();

        r.Session.RequestReload();

        Assert.Equal(0, r.Theme.PersistedCalls);
    }

    // ── search ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void Search_ARowQuery_HidesTheOtherRowAndEveryColour()
    {
        var r = NewRig();
        var uiScale = r.Vm.SearchGroups[0].Rows.Single(row => row.Key == "appearance.uiScale");

        r.Vm.ApplySearch(uiScale.Label);

        Assert.DoesNotContain("appearance.uiScale", r.Vm.HiddenKeys);
        Assert.Contains("appearance.stringPalette", r.Vm.HiddenKeys);
        Assert.Contains(AppearanceSettingsViewModel.GroupKey(1), r.Vm.HiddenKeys);
        Assert.All(r.Vm.ColorGroups, g => Assert.False(g.IsVisible));
    }

    /// <summary>Each colour is a searchable row; a hit shows its own heading and hides the others.</summary>
    [Fact]
    public void Search_AColourName_ShowsThatColourAndItsHeadingOnly()
    {
        var r = NewRig();
        var accent = Row(r.Vm, ThemeTokens.Accent);

        r.Vm.ApplySearch(accent.Name);

        Assert.True(accent.IsVisible);
        Assert.Contains(AppearanceSettingsViewModel.GroupKey(0), r.Vm.HiddenKeys);
        Assert.DoesNotContain(AppearanceSettingsViewModel.GroupKey(1), r.Vm.HiddenKeys);
        Assert.Contains(r.Vm.ColorGroups, g => g.IsVisible);
        Assert.Contains(r.Vm.ColorGroups, g => !g.IsVisible);
        Assert.False(Row(r.Vm, ThemeTokens.AppBackground).IsVisible);

        r.Vm.ApplySearch("");
        Assert.Empty(r.Vm.HiddenKeys);
        Assert.All(r.Vm.ColorGroups, g => Assert.True(g.IsVisible));
        Assert.All(r.Vm.ColorGroups.SelectMany(g => g.Items), i => Assert.True(i.IsVisible));
    }

    /// <summary>The view hides by exactly the keys the catalogue emits for the fixed rows and the two groups.</summary>
    [Fact]
    public void TheView_HidesByTheCataloguesKeys()
    {
        var r = NewRig();
        var axaml = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "AppearanceSettingsView.axaml")));
        var inView = Regex.Matches(axaml, @"ConverterParameter=""?(appearance\.[A-Za-z0-9.]+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var fixedKeys = r.Vm.SearchGroups[0].Rows.Select(row => row.Key)
            .Append(AppearanceSettingsViewModel.GroupKey(0)).Append(AppearanceSettingsViewModel.GroupKey(1))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(fixedKeys.Order(StringComparer.Ordinal), inView.Order(StringComparer.Ordinal));
        Assert.All(r.Vm.SearchGroups[1].Rows, row => Assert.StartsWith("appearance.color.", row.Key, StringComparison.Ordinal));
    }

    // ── the contract ────────────────────────────────────────────────────────

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IAppearanceSettingsScreen), NewRig().Vm));

    /// <summary>One theme: the contract offers no theme picker, only its palette, scale and colours.</summary>
    [Fact]
    public void TheContract_HasNoThemePicker() =>
        Assert.DoesNotContain(typeof(IAppearanceSettingsScreen).GetProperties(),
            p => p.Name.Contains("Preset", StringComparison.Ordinal) || p.Name.Contains("Theme", StringComparison.Ordinal));

    [Fact]
    public void TheColourList_IsTheCatalogue_UnderItsHeadings()
    {
        var r = NewRig();

        Assert.Equal(r.Theme.Tokens.Select(t => t.Key),
            r.Vm.ColorGroups.Cast<AppearanceColorGroupViewModel>().SelectMany(g => g.Rows).Select(row => row.TokenKey));
        Assert.Equal(r.Theme.Tokens.Select(t => t.GroupKey).Distinct().Count(), r.Vm.ColorGroups.Count);
        Assert.All(r.Vm.ColorGroups, g => Assert.DoesNotMatch("^ThemeTokenGroup_", g.Title));
        Assert.All(r.Vm.ColorGroups.SelectMany(g => g.Items), i => Assert.DoesNotMatch("^ThemeToken_", i.Name));
    }
}
