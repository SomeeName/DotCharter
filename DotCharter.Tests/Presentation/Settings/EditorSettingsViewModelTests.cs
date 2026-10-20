using static DotCharter.Tests.Presentation.Settings.GraphicsStubs;
using System.Reflection;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Editor on the Presentation layer: load, the unsaved counter,
///     save (which re-applies the note spacing to the open chart, redraws it and
///     says so), revert, reset and search.
/// </summary>
public sealed class EditorSettingsViewModelTests
{
    private sealed record Rig(EditorSettingsViewModel Vm, FakeConfigurationService Config, ProjectState State, RedrawCounter Editor);

    private static Rig NewRig(Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var state = new ProjectState();
        var editor = DispatchProxy.Create<IChartEditorSurface, RedrawCounter>();
        var vm = new EditorSettingsViewModel(config, FakeLocalization.English(), state, editor, new FakeAppLogger());
        vm.Load();
        return new Rig(vm, config, state, (RedrawCounter)(object)editor);
    }

    [Fact]
    public void Load_ReadsThePersistedValues()
    {
        var rig = NewRig(c =>
        {
            c.MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction;
            c.MinNoteDistanceDivisor = 0;
            c.UseFastChartingInput = true;
            c.PasteIncludeTones = false;
            c.DefaultCharterName = "Alex";
            c.LeadingSilenceSeconds = -3;
            c.GpImportSlideInFretDistance = 3;
        });

        Assert.Equal(2, rig.Vm.MinNoteDistanceModeIndex);
        Assert.False(rig.Vm.IsMinNoteDistanceInMilliseconds);
        Assert.Equal(64, rig.Vm.MinNoteDistanceDivisor);
        Assert.True(rig.Vm.UseFastChartingInput);
        Assert.False(rig.Vm.PasteIncludeTones);
        Assert.Equal("Alex", rig.Vm.DefaultCharterName);
        Assert.Equal(0, rig.Vm.LeadingSilenceSeconds);
        Assert.Equal(3, rig.Vm.GpImportSlideInFretDistance);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IEditorSettingsScreen>(() => NewRig().Vm, atLeast: 30);

    /// <summary>The millisecond row and the denominator row enable by the mode, so the flag must be raised when it changes.</summary>
    [Fact]
    public void TheSpacingMode_RaisesTheFlagItsTwoRowsEnableBy()
    {
        var rig = NewRig();
        var raised = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Vm.MinNoteDistanceModeIndex = 0;

        Assert.Contains(nameof(IEditorSettingsScreen.IsMinNoteDistanceInMilliseconds), raised);
        Assert.True(rig.Vm.IsMinNoteDistanceInMilliseconds);
    }

    [Fact]
    public void Save_PersistsEverything_RedrawsTheChart_AndSaysSo()
    {
        var rig = NewRig();
        rig.Vm.MinNoteDistanceModeIndex = 0;
        rig.Vm.MinNoteDistanceMs = -5;
        rig.Vm.MinNoteDistanceDivisor = 0;
        rig.Vm.DefaultCharterName = "  Alex  ";
        rig.Vm.PasteIncludeEvents = false;
        rig.Vm.GpImportSlideInFretDistance = 9;
        rig.Vm.GpImportMarkersToSections = true;

        rig.Vm.Save();

        var c = rig.Config.AppConfig;
        Assert.Equal(MinNoteDistanceMode.Milliseconds, c.MinNoteDistanceMode);
        Assert.Equal(0, c.MinNoteDistanceMs);
        Assert.Equal(1, c.MinNoteDistanceDivisor);
        Assert.Equal("Alex", c.DefaultCharterName);
        Assert.False(c.PasteIncludeEvents);
        Assert.Equal(3, c.GpImportSlideInFretDistance);
        Assert.True(c.GpImportMarkersToSections);
        Assert.Equal(1, rig.Editor.Redraws);
        Assert.Equal(FakeLocalization.English()["Status_EditorSettingsApplied"], rig.State.MainDisplayMessage);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    /// <summary>#493: the chord-naming style is an enum in the config and one toggle on the tab.</summary>
    [Fact]
    public void TheChordNamingStyle_LoadsAndSavesThroughTheMusicalToggle()
    {
        var rig = NewRig(c => c.ChordNamingStyle = ChordNamingStyle.Musical);
        Assert.True(rig.Vm.MusicalChordNames);

        rig.Vm.MusicalChordNames = false;
        Assert.Contains("editor.MusicalChordNames", rig.Vm.UnsavedSettings);
        rig.Vm.Save();
        Assert.Equal(ChordNamingStyle.Eof, rig.Config.AppConfig.ChordNamingStyle);

        rig.Vm.MusicalChordNames = true;
        rig.Vm.Save();
        Assert.Equal(ChordNamingStyle.Musical, rig.Config.AppConfig.ChordNamingStyle);
    }

    [Fact]
    public void TheChordNamingStyle_DefaultsToEof() =>
        Assert.Equal(ChordNamingStyle.Eof, new AppConfig().ChordNamingStyle);

    [Fact]
    public void Revert_AndReset_PutThePersistedValuesBack()
    {
        foreach (var verb in new Action<EditorSettingsViewModel>[] { v => v.Revert(), v => v.Reset() })
        {
            var rig = NewRig(c => c.SyncAnchorToChordFingering = true);
            rig.Vm.SyncAnchorToChordFingering = false;
            rig.Vm.TrailingSilenceSeconds = 42;

            verb(rig.Vm);

            Assert.True(rig.Vm.SyncAnchorToChordFingering);
            Assert.Equal(rig.Config.AppConfig.TrailingSilenceSeconds, rig.Vm.TrailingSilenceSeconds);
            Assert.Empty(rig.Vm.UnsavedSettings);
            Assert.Equal(0, rig.Editor.Redraws);
        }
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys() => SplitTabAssert.TheSearchCatalogue_HasNoRawResourceKeys(NewRig().Vm);

    [Fact]
    public void Search_HidesWhatDoesNotMatch_AndAGroupTitleKeepsItsRows()
    {
        var rig = NewRig();
        var groups = rig.Vm.SearchGroups;
        var gp = groups[3];

        rig.Vm.ApplySearch(gp.Title);

        Assert.All(gp.Rows, r => Assert.DoesNotContain(r.Key, rig.Vm.HiddenKeys));
        Assert.DoesNotContain(EditorSettingsViewModel.GroupKey(3), rig.Vm.HiddenKeys);
        Assert.Contains("editor.fastInput", rig.Vm.HiddenKeys);

        rig.Vm.ApplySearch("");
        Assert.Empty(rig.Vm.HiddenKeys);
    }

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IEditorSettingsScreen), NewRig().Vm));
}
