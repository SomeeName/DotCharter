using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>
///     A content selection belongs to ONE (arrangement, level) pair, and
///     <see cref="TrackEditorContext.Selection" /> is the app-wide
///     <see cref="ISelectionService" /> singleton every track shares.
///     <para>
///         Only a PROJECT swap, a snapshot restore and a vocal-phrase replacement
///         ever cleared it. Switching ARRANGEMENT (the Tracks picker,
///         <c>ChartEditorViewModel.CycleArrangement</c>) or LEVEL (the difficulty
///         picker — and the one <c>ProjectState.ActiveArrangement</c> performs on its
///         own by adopting the hardest level) left it holding the previous track's
///         sounds, which the inspector then edited through a real
///         <c>MutateNotesAction</c> while a different chart was on screen.
///     </para>
/// </summary>
public sealed class TrackSwitchSelectionTests
{
    [Fact]
    public void SwitchingArrangement_DropsTheSelectionItOrphaned()
    {
        var (state, selection, registry, project) = Open();
        using var _ = registry;

        var lead = project.Arrangements[0];
        selection.SelectedNotes.Add(lead.Levels[0].Notes[0]);
        selection.SelectedChords.Add(lead.Levels[0].Chords[0]);
        Assert.NotEmpty(selection.SelectedNotes);

        state.ActiveArrangement = project.Arrangements[1];

        Assert.Empty(selection.SelectedNotes);
        Assert.Empty(selection.SelectedChords);
    }

    [Fact]
    public void SwitchingLevel_DropsTheSelectionItOrphaned()
    {
        var (state, selection, registry, project) = Open();
        using var _ = registry;

        var lead = project.Arrangements[0];
        state.ActiveLevel = lead.Levels[1];
        selection.SelectedNotes.Add(lead.Levels[1].Notes[0]);
        selection.SelectedHandShapes.Add(lead.Levels[1].HandShapes[0]);
        Assert.NotEmpty(selection.SelectedNotes);

        state.ActiveLevel = lead.Levels[0];

        Assert.Empty(selection.SelectedNotes);
        Assert.Empty(selection.SelectedHandShapes);
    }

    /// <summary>
    ///     The hover/preview overlays point into the same graph, so they go with it —
    ///     <c>ChartRenderer</c> reads both into the frame snapshot.
    /// </summary>
    [Fact]
    public void SwitchingArrangement_DropsThePreviewAndHoverState()
    {
        var (state, selection, registry, project) = Open();
        using var _ = registry;

        var lead = project.Arrangements[0];
        selection.HoveredNote = lead.Levels[0].Notes[0];
        selection.PreviewNotes.Add(lead.Levels[0].Notes[0]);

        state.ActiveArrangement = project.Arrangements[1];

        Assert.Null(selection.HoveredNote);
        Assert.Empty(selection.PreviewNotes);
    }

    /// <summary>
    ///     Instance comparison, not the notification: the arrangement settings dialog
    ///     re-raises <c>ActiveArrangement</c> after a rename or a retune
    ///     (<c>ProjectState.NotifyActiveArrangementChanged</c>) without changing it,
    ///     and a rename must not cost the charter their selection.
    /// </summary>
    [Fact]
    public void ARaiseWithoutAChange_KeepsTheSelection()
    {
        var (state, selection, registry, project) = Open();
        using var _ = registry;

        selection.SelectedNotes.Add(project.Arrangements[0].Levels[0].Notes[0]);

        state.NotifyActiveArrangementChanged();

        Assert.Single(selection.SelectedNotes);
    }

    /// <summary>
    ///     The issue-triage jump switches the level FIRST and selects afterwards
    ///     (<c>InspectorIssuesViewModel.JumpToIssue</c>), so the clear cannot eat its pick.
    /// </summary>
    [Fact]
    public void SelectingAfterTheLevelSwitch_Survives()
    {
        var (state, selection, registry, project) = Open();
        using var _ = registry;

        var lead = project.Arrangements[0];
        state.ActiveLevel = lead.Levels[0];
        selection.SelectedNotes.Add(lead.Levels[0].Notes[0]);

        Assert.Single(selection.SelectedNotes);
        Assert.Same(lead.Levels[0].Notes[0], selection.SelectedNotes[0]);
    }

    private static (ProjectState State, SelectionService Selection, TrackContextRegistry Registry, ChartProject Project)
        Open()
    {
        var state = new ProjectState();
        var selection = new SelectionService();
        var registry = new TrackContextRegistry(state, selection);

        var project = BuildProject();
        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
        foreach (var arrangement in project.Arrangements)
            registry.GetOrCreate(arrangement);

        return (state, selection, registry, project);
    }

    private static ChartProject BuildProject()
    {
        var project = new ChartProject();
        for (var a = 0; a < 2; a++)
        {
            var arrangement = new Arrangement { Name = $"Arr{a}" };
            for (var l = 0; l < 2; l++)
            {
                var level = new ChartLevel { DifficultyIndex = l };
                level.Notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(l * 4 + a), StringIndex = 0, Fret = 3
                });
                level.Chords.Add(new Chord { Position = new FractionalPosition(l * 4 + a + 1) });
                level.HandShapes.Add(new HandShape
                {
                    Position = new FractionalPosition(l * 4 + a),
                    EndPosition = new FractionalPosition(l * 4 + a + 2)
                });
                arrangement.Levels.Add(level);
            }

            project.Arrangements.Add(arrangement);
        }

        return project;
    }
}
