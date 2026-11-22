using static DotCharter.Tests.Presentation.Settings.GraphicsStubs;
using System.Reflection;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Editing;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Editor on the Presentation layer: load, the unsaved counter,
///     save (which re-applies the note spacing to the open chart, redraws it and
///     says so), revert, reset and search.
/// </summary>
public sealed class EditorSettingsViewModelTests
{
    [Fact]
    public void PasteReplacement_LoadsTracksSavesAndReverts()
    {
        var rig = NewRig();
        Assert.True(rig.Vm.PasteReplacesExisting);
        rig.Vm.PasteReplacesExisting = false;
        Assert.Single(rig.Vm.UnsavedSettings);
        rig.Vm.Save();
        Assert.False(rig.Config.AppConfig.PasteReplacesExisting);
        rig.Vm.Load();
        Assert.False(rig.Vm.PasteReplacesExisting);
        rig.Vm.PasteReplacesExisting = true;
        rig.Vm.Revert();
        Assert.False(rig.Vm.PasteReplacesExisting);
        Assert.Empty(rig.Vm.UnsavedSettings);
        Assert.Contains(rig.Vm.SearchGroups.SelectMany(g => g.Rows), r => r.Key == "editor.pasteReplacesExisting");
    }

    [Fact]
    public void Saving_spacing_is_one_dirty_edit_and_undo_restores_the_chart()
    {
        var rig = NewRig();
        var history = new HistoryService(new FakeAppLogger());
        var vm = HistoryTestActivation.Create<EditorSettingsViewModel>(history, rig.Config, FakeLocalization.English(), rig.State,
            RecordingProxy.Create<IChartEditorSurface>().Proxy, rig.Device, new FakeAppLogger());
        vm.Load();
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        var note = new GuitarNote { Position = new FractionalPosition(0), Sustain = new FractionalPosition(1), Fret = 5 };
        level.Notes.Add(note);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = 5 });
        var firstAnchor = new FretAnchor(new FractionalPosition(0), 5);
        var removedAnchor = new FretAnchor(new FractionalPosition(1), 5);
        level.Anchors.Add(firstAnchor);
        level.Anchors.Add(removedAnchor);
        var template = new ChordTemplate { Id = 0, Name = "C-arp" };
        arrangement.ChordTemplates.Add(template);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        rig.State.ActiveProject = project;
        using var dirty = new ProjectDirtyTracker(history);
        vm.MinNoteDistanceModeIndex = 0;
        vm.MinNoteDistanceMs = 250;

        vm.Save();

        Assert.Equal(new FractionalPosition(0, new Fraction(1, 2)), note.Sustain);
        Assert.True(dirty.IsDirty);
        Assert.Single(history.GetUndoSteps());
        Assert.Single(level.Anchors);
        Assert.Equal("C", template.Name);
        Assert.True(template.ForceArpeggioInRs);
        vm.Save();
        Assert.Single(history.GetUndoSteps());
        history.Undo();
        Assert.Equal(new FractionalPosition(1), note.Sustain);
        Assert.False(dirty.IsDirty);
        Assert.Same(note, level.Notes[0]);
        Assert.Same(firstAnchor, level.Anchors[0]);
        Assert.Same(removedAnchor, level.Anchors[1]);
        Assert.Equal("C-arp", template.Name);
        Assert.False(template.ForceArpeggioInRs);
        history.Redo();
        Assert.Equal(new FractionalPosition(0, new Fraction(1, 2)), note.Sustain);
    }

    private sealed record Rig(EditorSettingsViewModel Vm, FakeConfigurationService Config, ProjectState State, RedrawCounter Editor, FakeDevice Device);

    private static Rig NewRig(Action<AppConfig>? seed = null)
    {
        var config = new FakeConfigurationService();
        seed?.Invoke(config.AppConfig);
        var state = new ProjectState();
        var editor = DispatchProxy.Create<IChartEditorSurface, RedrawCounter>();
        var device = new FakeDevice();
        var vm = HistoryTestActivation.Create<EditorSettingsViewModel>(new HistoryService(new FakeAppLogger()),
            config, FakeLocalization.English(), state, editor, device, new FakeAppLogger());
        vm.Load();
        return new Rig(vm, config, state, (RedrawCounter)(object)editor, device);
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
        });

        Assert.Equal(2, rig.Vm.MinNoteDistanceModeIndex);
        Assert.False(rig.Vm.IsMinNoteDistanceInMilliseconds);
        Assert.Equal(64, rig.Vm.MinNoteDistanceDivisor);
        Assert.True(rig.Vm.UseFastChartingInput);
        Assert.False(rig.Vm.PasteIncludeTones);
        Assert.Equal("Alex", rig.Vm.DefaultCharterName);
        Assert.Equal(0, rig.Vm.LeadingSilenceSeconds);
        Assert.Empty(rig.Vm.UnsavedSettings);
    }

    [Fact]
    public void EverySettableMember_CountsOnce_AndPuttingItBackCountsNothing() =>
        SplitTabAssert.EverySettableMember_CountsOnce<IEditorSettingsScreen>(() => NewRig().Vm, atLeast: 25);

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

        rig.Vm.Save();

        var c = rig.Config.AppConfig;
        Assert.Equal(MinNoteDistanceMode.Milliseconds, c.MinNoteDistanceMode);
        Assert.Equal(0, c.MinNoteDistanceMs);
        Assert.Equal(1, c.MinNoteDistanceDivisor);
        Assert.Equal("Alex", c.DefaultCharterName);
        Assert.False(c.PasteIncludeEvents);
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

    /// <summary>#229: String order lives on this tab, turns the timeline as soon as it is picked, and saves to InvertStrings.</summary>
    [Fact]
    public void TheStringOrder_LoadsAppliesLiveAndSaves()
    {
        var rig = NewRig(c => c.InvertStrings = true);
        Assert.Equal(1, rig.Vm.StringOrderIndex);
        Assert.True(rig.State.InvertStrings);
        var redraws = rig.Editor.Redraws;

        rig.Vm.StringOrderIndex = 0;
        Assert.False(rig.State.InvertStrings);
        Assert.Equal(redraws + 1, rig.Editor.Redraws);
        Assert.Contains("editor.StringOrderIndex", rig.Vm.UnsavedSettings);

        rig.Vm.Save();
        Assert.False(rig.Config.AppConfig.InvertStrings);
        Assert.False(rig.Config.AppConfig.PlayModeInvertStrings);
    }

    /// <summary>The 2D timeline's look sits beside the String order now, and applies the same way: live, then saved.</summary>
    [Fact]
    public void TheTimelineLook_AppliesLiveAndSaves()
    {
        var rig = NewRig(c => c.Timeline2DLaneTint = false);
        rig.Device.Calls.Clear();
        var redraws = rig.Editor.Redraws;

        rig.Vm.Timeline2DLaneTint = true;

        Assert.Contains("timeline:True/True", rig.Device.Calls);
        Assert.Equal(redraws + 1, rig.Editor.Redraws);
        Assert.Contains("editor.Timeline2DLaneTint", rig.Vm.UnsavedSettings);
        rig.Vm.Save();
        Assert.True(rig.Config.AppConfig.Timeline2DLaneTint);
        Assert.True(rig.Config.AppConfig.Timeline2DPolishEnabled);
    }

    /// <summary>Revert puts a live-applied String order back on the timeline too.</summary>
    [Fact]
    public void Revert_PutsTheStringOrderBackOnTheTimeline()
    {
        var rig = NewRig();
        rig.Vm.StringOrderIndex = 1;
        Assert.True(rig.State.InvertStrings);

        rig.Vm.Revert();

        Assert.Equal(0, rig.Vm.StringOrderIndex);
        Assert.False(rig.State.InvertStrings);
    }

    [Fact]
    public void TheStringOrder_IsFoundBySearch()
    {
        var rig = NewRig();
        var row = rig.Vm.SearchGroups.SelectMany(g => g.Rows).Single(r => r.Key.EndsWith("stringOrder", StringComparison.Ordinal));

        rig.Vm.ApplySearch(row.Label);

        Assert.DoesNotContain(row.Key, rig.Vm.HiddenKeys);
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

    /// <summary>#805: the inspector's Apply step is off by default, persists, and a save reaches the live state the
    /// inspector reads (no restart).</summary>
    [Fact]
    public void TheInspectorApplyStep_IsOffByDefault_PersistsAndReachesTheLiveStateOnSave()
    {
        Assert.False(new AppConfig().InspectorApplyStep);
        var rig = NewRig();
        Assert.False(rig.Vm.InspectorApplyStep);
        Assert.False(rig.State.InspectorApplyStep);

        rig.Vm.InspectorApplyStep = true;
        Assert.Single(rig.Vm.UnsavedSettings);
        rig.Vm.Save();

        Assert.True(rig.Config.AppConfig.InspectorApplyStep);
        Assert.True(rig.State.InspectorApplyStep);
        rig.Vm.InspectorApplyStep = false;
        rig.Vm.Load();
        Assert.True(rig.Vm.InspectorApplyStep);
        Assert.Contains(rig.Vm.SearchGroups.SelectMany(g => g.Rows), r => r.Key == "editor.inspectorApplyStep");

        rig.Vm.InspectorApplyStep = false;
        rig.Vm.Save();
        Assert.False(rig.Config.AppConfig.InspectorApplyStep);
        Assert.False(rig.State.InspectorApplyStep);
    }

    [Fact]
    public void TheScreen_ConformsToItsContract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IEditorSettingsScreen), NewRig().Vm));
}
