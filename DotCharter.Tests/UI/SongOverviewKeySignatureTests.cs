using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Song inspector's key-signature row must never author an edit while it
///     is READING the open project.
///     <para>
///         What shipped: loading a Rocksmith package pushed a real
///         <c>SetKeySignatureAction</c> onto the undo history — "Accidentals=-8,
///         minor=False", a value two steps outside the −7…+7 every format here
///         can spell. The pane's own <c>Refresh</c> rewrites the bound
///         <c>KeyOptions</c> collection, the inspector's ComboBox answers a
///         changed item list by dropping its selection to <c>-1</c>, and the
///         two-way binding wrote that index straight back — where index −1 reads
///         as −8 accidentals.
///     </para>
///     <para>
///         The consequences were both remote from the key row: one undo node is
///         enough for <c>ProjectDirtyTracker</c> to report unsaved changes, so
///         closing prompted Save/Discard for a song the user had only OPENED;
///         and the crash-recovery snapshot that followed made the NEXT launch
///         offer to restore a session nobody had edited. Neither symptom names
///         the inspector, which is why this is pinned here rather than there.
///     </para>
/// </summary>
public sealed class SongOverviewKeySignatureTests
{
    /// <summary>
    ///     The exact field report: open a project, then let the bound control do
    ///     what a ComboBox does when its items change under it.
    /// </summary>
    [Fact]
    public void AComboBoxDroppingItsSelection_DoesNotAuthorAKeySignature()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Refresh();
        Assert.False(history.CanUndo); // the load itself is not an edit

        // -1 is how every Avalonia SelectingItemsControl spells "no selection",
        // and it raises it ITSELF — no user ever picked it.
        vm.KeyAccidentalsIndex = -1;

        Assert.False(history.CanUndo);
        Assert.Equal(0, state.ActiveProject!.Chart.KeyAccidentals);
    }

    /// <summary>
    ///     …and the pane must not be left showing an empty key for a chart that
    ///     has one. Refusing the write is only half the fix; the control has to
    ///     be put back on the model's answer.
    /// </summary>
    [Fact]
    public void AfterARefusedSelection_ThePaneStillShowsTheChartsKey()
    {
        var (vm, _, state) = NewPane();
        var project = NewProject();
        project.Chart.KeyAccidentals = 2; // D major
        state.ActiveProject = project;
        vm.Refresh();

        vm.KeyAccidentalsIndex = -1;

        Assert.Equal(9, vm.KeyAccidentalsIndex); // 2 + 7
        Assert.Equal("D", vm.KeyOptions[vm.KeyAccidentalsIndex]);
    }

    /// <summary>
    ///     A refresh is a READ. It runs on every history change and on every
    ///     project/arrangement switch, so anything it writes back compounds.
    /// </summary>
    [Fact]
    public void RepeatedRefreshes_NeverPushAnUndoNode()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();

        for (var i = 0; i < 5; i++) vm.Refresh();

        Assert.False(history.CanUndo);
    }

    /// <summary>
    ///     The guard must not cost the pane its real job: a key the USER picks is
    ///     still one undo node, and still reaches the chart.
    /// </summary>
    [Fact]
    public void AKeyThePlayerPicks_IsStillAuthored()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = NewProject();
        vm.Refresh();

        vm.KeyAccidentalsIndex = 9; // D major

        Assert.True(history.CanUndo);
        Assert.Equal(2, state.ActiveProject!.Chart.KeyAccidentals);
    }

    /// <summary>
    ///     Whatever the pane authors has to be spellable. −7…+7 is the range the
    ///     rscp writer, the EOF exporter and the sheet strip all assume.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-8)]
    [InlineData(15)]
    [InlineData(int.MaxValue)]
    public void NoIndexOutsideTheOptionList_IsTreatedAsAKey(int index) => Assert.False(SongOverviewViewModel.IsKeyIndex(index));

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
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { Name = "Lead" });
        return project;
    }

    /// <summary>The key card said the key three times: the Key picker ("Cb"), the Minor box,
    /// and a "Current: Cb Major" row under them, in English whatever the language.</summary>
    [Fact]
    public void TheKeyCard_SaysTheKeyOnce()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Editor", "Inspector", "SongInspectorView.axaml")));

        Assert.Contains("KeyAccidentalsIndex", view);
        Assert.DoesNotContain("Inspector_Current", view);
        Assert.DoesNotContain("KeyDisplayName", view);
    }
}
