using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #181, the Inspector's Song tab: renaming a tone there rewrote the tone changes and the
///     starting tone only, so the definition and the Base/A–D slots stayed on the old key and the
///     export reported a tone with no definition. The tab now lists the project's TONES (defined
///     or not), renames and deletes through <c>ToneKeyEdits</c>, and prints why a rename is refused.
/// </summary>
public sealed class SongOverviewToneRenameTests
{
    [Fact]
    public void ARename_MovesEveryStore_AsOneUndoStep_AndTheExportStillFindsTheDefinition()
    {
        var (vm, history, state) = NewPane();
        var project = Project();
        state.ActiveProject = project;
        var before = ChartProjectCloner.Clone(project);
        var lead = project.Arrangements[0];

        var row = Assert.Single(vm.TonesPanel.Groups);
        row.EditName = "lead_crunch";
        vm.TonesPanel.RenameCommand.Execute(row);

        Assert.Equal("lead_crunch", Assert.Single(project.ToneDefinitions).Key);
        Assert.Equal(["lead_crunch", "lead_crunch", "lead_crunch"], [lead.ToneBase, lead.ToneA, lead.StartingTone]);
        Assert.Equal(["lead_crunch"], lead.ToneNames);
        Assert.Equal("lead_crunch", Assert.Single(lead.ToneChanges).ToneName);
        Assert.Empty(MissingTones(project));
        Assert.Equal("lead_crunch", Assert.Single(vm.TonesPanel.Groups).Name);

        history.Undo();
        Assert.False(history.CanUndo, "the rename must be ONE undo step");
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    [Fact]
    public void ARenameOntoAnotherTone_IsRefused_AndSaysWhyUnderTheRow()
    {
        var (vm, history, state) = NewPane();
        var project = Project();
        project.ToneDefinitions.Add(Definition("lead_clean"));
        state.ActiveProject = project;
        var before = ChartProjectCloner.Clone(project);

        var row = vm.TonesPanel.Groups.Single(t => t.Name == "lead_dist");
        row.EditName = "Lead_Clean";
        vm.TonesPanel.RenameCommand.Execute(row);

        Assert.False(history.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
        Assert.True(row.HasError);
        Assert.Contains("Lead_Clean", row.Error, StringComparison.Ordinal);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, FakeLocalization.English()["Inspector_Tones_RenameTaken"], "Lead_Clean"),
            row.Error);

        row.EditName = "lead_clean_2";
        Assert.False(row.HasError);
    }

    [Fact]
    public void TheList_IsTheProjectsTones_AndMarksAPlacedKeyWithNoDefinition()
    {
        var (vm, _, state) = NewPane();
        var project = Project();
        project.ToneDefinitions.Add(Definition("unplaced"));
        project.Arrangements[0].ToneChanges.Add(new ToneChange
        {
            Position = TimelineMath.PositionFromMs(1000, project.Beats), ToneName = "dangling"
        });
        state.ActiveProject = project;

        Assert.Equal(["lead_dist", "dangling", "unplaced"], vm.TonesPanel.Groups.Select(t => t.Name));
        Assert.Equal([false, true, false], vm.TonesPanel.Groups.Select(t => t.HasWarning));
        Assert.Empty(vm.TonesPanel.Groups.Single(t => t.Name == "unplaced").Occurrences);
    }

    [Fact]
    public void ADelete_TakesTheDefinitionAndTheSlotsWithTheChanges()
    {
        var (vm, history, state) = NewPane();
        var project = Project();
        state.ActiveProject = project;
        var lead = project.Arrangements[0];

        vm.TonesPanel.DeleteCommand.Execute(Assert.Single(vm.TonesPanel.Groups));

        Assert.Empty(project.ToneDefinitions);
        Assert.Empty(lead.ToneChanges);
        Assert.Equal(["", "", ""], [lead.ToneBase, lead.ToneA, lead.StartingTone]);
        Assert.Empty(lead.ToneNames);
        Assert.Empty(vm.TonesPanel.Groups);

        history.Undo();
        Assert.Equal("lead_dist", Assert.Single(project.ToneDefinitions).Key);
        Assert.Equal("lead_dist", lead.ToneBase);
        Assert.Single(lead.ToneChanges);
    }

    private static (SongOverviewViewModel Vm, HistoryService History, ProjectState State) NewPane()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        return (new SongOverviewViewModel(state, history, logger, FakeLocalization.English(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy), history, state);
    }

    /// <summary>Steps 1–2 of the issue: a designed tone, Lead's Base tone and Tone A, one tone change.</summary>
    private static ChartProject Project()
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.ToneDefinitions.Add(Definition("lead_dist"));
        var lead = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = "lead_dist", ToneA = "lead_dist", StartingTone = "lead_dist"
        };
        lead.ToneNames.Add("lead_dist");
        lead.ToneChanges.Add(new ToneChange { Position = TimelineMath.PositionFromMs(4000, project.Beats), ToneName = "lead_dist" });
        // A note, or the export check stops at "no notes" and never reaches the tones.
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(2000, project.Beats), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        return project;
    }

    private static ToneDefinition Definition(string key) =>
        new() { Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared)) };

    private static List<string> MissingTones(ChartProject project) =>
    [
        .. new ChartExportValidator().Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyToneNotDefined)
            .Select(f => f.Detail)
    ];
}
