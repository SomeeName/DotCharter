using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Song tab's information card (inspector design D, #751): title, artist, album and year are STAGED
///     and written by "Apply song details" as one undo node; Revert drops them; a re-read keeps an unapplied
///     edit and lets an untouched field follow the chart.
/// </summary>
public sealed class SongDetailsDraftTests
{
    [Fact]
    public void Typing_WritesNothing_UntilApply_ThenOneUndoNodeWritesEveryField()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Refresh();
        var details = vm.Details;
        Assert.Equal("Old title", details.Title);
        Assert.False(details.ApplyCommand.CanExecute(null));

        details.Title = "New title";
        details.Year = " 2021 ";
        Assert.True(details.IsDirty);
        Assert.True(details.ApplyCommand.CanExecute(null));
        Assert.Equal("Old title", state.ActiveProject!.Chart.SongName);
        Assert.False(history.CanUndo);

        details.ApplyCommand.Execute(null);
        var chart = state.ActiveProject.Chart;
        Assert.Equal(("New title", "2021", "Old artist"), (chart.SongName, chart.AlbumYear, chart.Artist));
        Assert.False(details.IsDirty);
        Assert.Equal("2021", details.Year);

        history.Undo();
        Assert.Equal(("Old title", "1999"), (chart.SongName, chart.AlbumYear));
        Assert.False(history.CanUndo); // one node, not one per field
        Assert.Equal("Old title", details.Title);
    }

    [Fact]
    public void Revert_PutsTheChartsValuesBack()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Refresh();
        vm.Details.Artist = "Someone else";

        vm.Details.RevertCommand.Execute(null);

        Assert.Equal("Old artist", vm.Details.Artist);
        Assert.False(vm.Details.IsDirty);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AReRead_KeepsAnUnappliedEdit_AndAnUntouchedFieldFollowsTheChart()
    {
        var (vm, _, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Refresh();
        vm.Details.Title = "Typed";

        state.ActiveProject!.Chart.AlbumName = "Changed elsewhere";
        vm.Refresh();

        Assert.Equal("Typed", vm.Details.Title);
        Assert.Equal("Changed elsewhere", vm.Details.Album);
        Assert.True(vm.Details.IsDirty);
    }

    /// <summary>
    ///     The review's HIGH finding: a title typed in project A must not move to project B. Opening B reads B whole;
    ///     Apply then has nothing to write.
    /// </summary>
    [Fact]
    public void AnotherProject_DropsTheDraft_AndApplyWritesNothingToIt()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Details.Title = "Typed for A";
        vm.KeyAccidentalsIndex = 9;

        var b = NewProject();
        b.Chart.SongName = "Title of B";
        state.ActiveProject = b;

        Assert.Equal("Title of B", vm.Details.Title);
        Assert.Equal(0, vm.Details.KeyAccidentals);
        Assert.Equal(7, vm.KeyAccidentalsIndex);
        Assert.False(vm.Details.IsDirty);
        Assert.False(vm.Details.ApplyCommand.CanExecute(null));
        vm.Details.ApplyPending();
        Assert.Equal("Title of B", b.Chart.SongName);
        Assert.False(history.CanUndo);
    }

    /// <summary>Refresh tidies every marker list in one undo node (time order, exact duplicates gone) and keeps the
    ///     unapplied details.</summary>
    [Fact]
    public void Refresh_TidiesTheMarkersInOneUndoNode_AndKeepsTheDraft()
    {
        var (vm, history, state) = NewPane();
        var project = NewProject();
        var lead = project.Arrangements[0];
        lead.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "verse" });
        lead.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "intro" });
        lead.EventPoints.Add(new EventPoint { Position = new FractionalPosition(8), Section = "verse" });
        lead.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "clean" });
        lead.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "clean" });
        state.ActiveProject = project;
        vm.Details.Title = "Typed";

        vm.RefreshCommand.Execute(null);

        Assert.Equal(["intro", "verse"], lead.EventPoints.Select(e => e.Section));
        Assert.Single(lead.ToneChanges);
        Assert.Single(history.GetUndoSteps());
        Assert.Equal("Typed", vm.Details.Title);
        vm.RefreshCommand.Execute(null); // already tidy: no second node
        Assert.Single(history.GetUndoSteps());

        history.Undo();
        Assert.Equal(["verse", "intro", "verse"], lead.EventPoints.Select(e => e.Section));
        Assert.Equal(2, lead.ToneChanges.Count);
    }

    /// <summary>"All groups" or one name: the Sections list shows every row or only that name's.</summary>
    [Fact]
    public void TheGroupFilter_ShowsAllGroupsOrOne_AndARenameKeepsIt()
    {
        var (vm, _, state) = NewPane();
        var project = NewProject();
        foreach (var (beat, name) in new[] { (0, "intro"), (8, "verse"), (16, "chorus"), (24, "verse") })
            project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Section = name });
        state.ActiveProject = project;
        var sections = vm.SectionsPanel;

        Assert.Equal(["All groups", .. sections.Groups.Select(g => g.Name)], sections.GroupFilterOptions);
        Assert.Equal(3, sections.Groups.Count);
        var verseIndex = sections.GroupFilterOptions.IndexOf("verse");

        sections.GroupFilterIndex = verseIndex;
        Assert.Equal("verse", Assert.Single(sections.Groups).Name);

        sections.GroupFilterIndex = -1; // a picker dropping its selection keeps the filter
        Assert.Equal(verseIndex, sections.GroupFilterIndex);
        Assert.Equal("verse", Assert.Single(sections.Groups).Name);

        var verse = sections.Groups[0];
        verse.EditName = "bridge";
        sections.RenameCommand.Execute(verse);
        Assert.Equal("bridge", Assert.Single(sections.Groups).Name);

        sections.GroupFilterIndex = 0;
        Assert.Equal(3, sections.Groups.Count);
    }

    /// <summary>The starting-tone chooser writes the active arrangement's starting tone, one undo node; a re-read
    ///     writes nothing.</summary>
    [Fact]
    public void TheStartingTone_IsPickedFromTheTones_AsOneUndoNode()
    {
        var (vm, history, state) = NewPane();
        var project = NewProject();
        var lead = project.Arrangements[0];
        lead.StartingTone = "clean";
        lead.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "lead" });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean" });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "lead" });
        state.ActiveProject = project;
        state.ActiveArrangement = lead;
        var choice = vm.StartingTone;
        Assert.Contains("lead", choice.Options);
        Assert.Equal("clean", choice.Options[choice.SelectedIndex]);
        vm.Refresh();
        Assert.False(history.CanUndo);

        choice.SelectedIndex = choice.Options.IndexOf("lead");

        Assert.Equal("lead", lead.StartingTone);
        Assert.Single(history.GetUndoSteps());
        history.Undo();
        Assert.Equal("clean", lead.StartingTone);
        Assert.Equal("clean", choice.Options[choice.SelectedIndex]);
    }

    [Fact]
    public void WithNoProject_NothingIsStaged()
    {
        var (vm, _, _) = NewPane();
        vm.Details.Title = "x";

        Assert.False(vm.Details.HasProject);
        Assert.False(vm.Details.ApplyCommand.CanExecute(null));
    }

    private static (SongOverviewViewModel Vm, HistoryService History, ProjectState State) NewPane()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        return (new SongOverviewViewModel(state, history, logger, FakeLocalization.English(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy), history, state);
    }

    private static ChartProject NewProject()
    {
        var project = new ChartProject
        {
            Chart = { SongName = "Old title", Artist = "Old artist", AlbumName = "Old album", AlbumYear = "1999" }
        };
        project.Arrangements.Add(new Arrangement { Name = "Lead" });
        return project;
    }
}
