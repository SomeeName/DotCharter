using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>Settings ▸ Play on the Presentation layer: load, the unsaved counter, save, revert, reset and search.</summary>
public sealed class PlaySettingsViewModelTests
{
    private static (PlaySettingsViewModel Vm, FakeConfigurationService Config) NewRig(Action<AppConfig>? seed = null) =>
        NewRig(new ProjectState(), seed);

    private static (PlaySettingsViewModel Vm, FakeConfigurationService Config) NewRig(ProjectState state, Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var vm = new PlaySettingsViewModel(config, FakeLocalization.English(), state);
        vm.Load();
        return (vm, config);
    }

    /// <summary>#229: Play Mode has its own String order, saved apart from the editor's and handed to the highway.</summary>
    [Fact]
    public void TheStringOrder_IsPlayModesOwn()
    {
        var state = new ProjectState();
        var (vm, config) = NewRig(state, c => c.InvertStrings = true);
        Assert.Equal(0, vm.StringOrderIndex);

        vm.StringOrderIndex = 1;
        Assert.Contains("play.StringOrderIndex", vm.UnsavedSettings);
        vm.Save();

        Assert.True(config.AppConfig.PlayModeInvertStrings);
        Assert.True(config.AppConfig.InvertStrings);
        Assert.True(state.PlayModeInvertStrings);
        Assert.False(state.InvertStrings);
        Assert.Equal(1, NewRig(c => c.PlayModeInvertStrings = true).Vm.StringOrderIndex);
    }

    [Fact]
    public void Load_ReadsThePersistedValues_ThroughTheSharedCountInMap()
    {
        var (vm, _) = NewRig(c =>
        {
            c.GameplayCountInBeats = 2;
            c.GameplayClapsEnabled = true;
            c.GameplayRockMeterEnabled = true;
        });

        Assert.Equal(1, vm.GameplayCountInIndex);
        Assert.True(vm.GameplayClapsEnabled);
        Assert.True(vm.GameplayRockMeterEnabled);
        Assert.False(vm.GameplayMetronomeEnabled);
        Assert.Empty(vm.UnsavedSettings);
    }

    /// <summary>A hand-edited count-in still shows a row: anything positive rounds up to the next step.</summary>
    [Theory]
    [InlineData(-4, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(16, 3)]
    public void AnOddPersistedCountIn_LandsOnARow(int beats, int row) =>
        Assert.Equal(row, NewRig(c => c.GameplayCountInBeats = beats).Vm.GameplayCountInIndex);

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IPlaySettingsScreen>(() => NewRig().Vm, atLeast: 4);

    [Fact]
    public void Save_PersistsEveryRow_AndClearsTheCount()
    {
        var (vm, config) = NewRig();
        vm.GameplayCountInIndex = 3;
        vm.GameplayMetronomeEnabled = true;
        vm.GameplayRockMeterEnabled = true;

        vm.Save();

        Assert.Equal(8, config.AppConfig.GameplayCountInBeats);
        Assert.True(config.AppConfig.GameplayMetronomeEnabled);
        Assert.True(config.AppConfig.GameplayRockMeterEnabled);
        Assert.False(config.AppConfig.GameplayClapsEnabled);
        Assert.Empty(vm.UnsavedSettings);
    }

    /// <summary>Play Mode's own modal writes the same values; reopening Settings must show what it wrote.</summary>
    [Fact]
    public void Revert_AndReset_ReadWhatIsPersisted_IncludingAnotherSurfacesWrite()
    {
        foreach (var verb in new Action<PlaySettingsViewModel>[] { v => v.Revert(), v => v.Reset() })
        {
            var (vm, config) = NewRig();
            vm.GameplayClapsEnabled = true;
            config.AppConfig.GameplayCountInBeats = 4;

            verb(vm);

            Assert.False(vm.GameplayClapsEnabled);
            Assert.Equal(2, vm.GameplayCountInIndex);
            Assert.Empty(vm.UnsavedSettings);
        }
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void Search_HidesWhatDoesNotMatch_AndTheGroupWhenNothingIsLeft()
    {
        var (vm, _) = NewRig();
        var claps = vm.SearchGroups[0].Rows.Single(r => r.Key == "play.claps");

        vm.ApplySearch(claps.Label);
        Assert.DoesNotContain("play.claps", vm.HiddenKeys);
        Assert.Contains("play.rockMeter", vm.HiddenKeys);

        vm.ApplySearch("zzqx-no-such-setting");
        Assert.Contains(PlaySettingsViewModel.GroupKey(0), vm.HiddenKeys);

        vm.ApplySearch("");
        Assert.Empty(vm.HiddenKeys);
    }

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IPlaySettingsScreen), NewRig().Vm));
}
