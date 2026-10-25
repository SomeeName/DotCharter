using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ AI &amp; models ▸ Lyric auto-sync: each model's installed or
///     missing state, what it runs on (GPU, CPU, or the remembered CPU fallback),
///     and the download, repair and retry-GPU verbs.
/// </summary>
public sealed class LyricModelsSettingsViewModelTests
{
    private static (LyricModelsSettingsViewModel Vm, FakeLyricModelService Models) NewRig(Action<FakeLyricModelService>? seed = null)
    {
        var models = new FakeLyricModelService();
        seed?.Invoke(models);
        var vm = new LyricModelsSettingsViewModel(models, FakeLocalization.English(), new TestUiDispatcher());
        vm.Load();
        return (vm, models);
    }

    private static ILyricModelRow Row(LyricModelsSettingsViewModel vm, string id) =>
        vm.Models.Single(m => m.Key == "lyricModels." + id);

    [Fact]
    public void AMissingModel_SaysWhatTheDownloadCosts_AndOffersOnlyDownload()
    {
        var (vm, _) = NewRig();
        var row = Row(vm, LyricModelCatalog.MultilingualId);

        Assert.False(row.IsInstalled);
        Assert.Contains("216", row.StatusText, StringComparison.Ordinal);
        Assert.True(row.DownloadCommand.CanExecute(null));
        Assert.False(row.RepairCommand.CanExecute(null));
        Assert.False(row.RetryGpuCommand.CanExecute(null));
    }

    [Fact]
    public async Task Download_InstallsTheModel_AndTheRowTurnsInstalled()
    {
        var (vm, models) = NewRig();
        var row = Row(vm, LyricModelCatalog.EnglishId);

        await ((IAsyncRelayCommand)row.DownloadCommand).ExecuteAsync(null);

        Assert.Equal([(LyricModelCatalog.EnglishId, false)], models.Installs);
        Assert.True(row.IsInstalled);
        Assert.Equal(FakeLocalization.English()["Settings_LyricModels_Installed"], row.StatusText);
        Assert.True(row.RepairCommand.CanExecute(null));
        Assert.False(row.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Repair_ReinstallsAnInstalledModel_AndAFailureIsSaid()
    {
        var (vm, models) = NewRig(m =>
        {
            m.States[LyricModelCatalog.MultilingualId] = m.States[LyricModelCatalog.MultilingualId] with { IsInstalled = true };
            m.InstallSucceeds = false;
        });
        var row = Row(vm, LyricModelCatalog.MultilingualId);

        await ((IAsyncRelayCommand)row.RepairCommand).ExecuteAsync(null);

        Assert.Equal([(LyricModelCatalog.MultilingualId, true)], models.Installs);
        Assert.Equal(FakeLocalization.English()["Settings_LyricModels_Failed"], row.ProgressText);
        Assert.False(row.IsBusy);
    }

    [Theory]
    [InlineData(LyricModelRunner.Gpu, "DirectML")]
    [InlineData(LyricModelRunner.Cpu, "CPU")]
    [InlineData(LyricModelRunner.CpuFallback, "failed on the GPU")]
    [InlineData(LyricModelRunner.NotRunYet, "Not run yet")]
    public void TheProviderLine_SaysWhatTheModelRunsOn(LyricModelRunner runner, string expected)
    {
        var (vm, _) = NewRig(m => m.States[LyricModelCatalog.MultilingualId] = m.States[LyricModelCatalog.MultilingualId] with
        {
            IsInstalled = true,
            Runner = runner,
            ProviderName = runner == LyricModelRunner.Gpu ? "DirectML" : null
        });

        Assert.Contains(expected, Row(vm, LyricModelCatalog.MultilingualId).ProviderText, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryGpu_ClearsTheRememberedFallback_AndHidesItself()
    {
        var (vm, models) = NewRig(m => m.States[LyricModelCatalog.MultilingualId] = m.States[LyricModelCatalog.MultilingualId] with
        {
            IsInstalled = true,
            Runner = LyricModelRunner.CpuFallback
        });
        var row = Row(vm, LyricModelCatalog.MultilingualId);
        Assert.True(row.IsCpuFallback);
        Assert.True(row.RetryGpuCommand.CanExecute(null));

        row.RetryGpuCommand.Execute(null);

        Assert.Equal([LyricModelCatalog.MultilingualId], models.GpuRetries);
        Assert.False(row.IsCpuFallback);
        Assert.False(row.RetryGpuCommand.CanExecute(null));
    }

    [Fact]
    public void Search_FindsAModelByName_AndHidesTheOther()
    {
        var (vm, _) = NewRig();
        var english = Row(vm, LyricModelCatalog.EnglishId);

        vm.ApplySearch(english.Name);

        Assert.True(english.IsShown);
        Assert.False(Row(vm, LyricModelCatalog.MultilingualId).IsShown);

        vm.ApplySearch("");
        Assert.All(vm.Models, m => Assert.True(m.IsShown));
    }

    [Fact]
    public void NothingIsEverUnsaved() => Assert.Empty(NewRig().Vm.UnsavedSettings);

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(ILyricModelsSettingsScreen), NewRig().Vm));
}
