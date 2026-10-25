using System.Collections.Specialized;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Song pane re-reads the whole project on EVERY committed edit, and it
///     may not republish three bound collections when the answer did not move.
///
///     <para>
///         <c>SongOverviewViewModel.Refresh</c> is subscribed to
///         <c>IHistoryService.HistoryChanged</c>, so it runs between a one-note
///         edit and the next frame. It used to <c>Clear()</c> the Tones, Sections
///         and Phrases collections and re-<c>Add</c> a fresh
///         <c>RenameableItem</c> per distinct name — one <c>CollectionChanged</c>
///         per item into three bound <c>ItemsControl</c>s, each of which drops
///         and re-realizes every container for a <c>Reset</c> — plus fifteen
///         <c>Replace</c> notifications into the key <c>ComboBox</c>'s
///         <c>KeyOptions</c>. A note edit changes no tone, section or phrase NAME
///         and no count, so every one of those rebuilds published the identical
///         list.
///     </para>
///     <para>
///         Republishing was also what discarded a half-typed rename: the row a
///         <c>EditName</c> was being typed into is replaced by a new instance.
///     </para>
/// </summary>
public sealed class SongOverviewRefreshCostTests
{
    /// <summary>
    ///     An edit that changes nothing the pane shows raises nothing on any of
    ///     its three bound collections.
    /// </summary>
    [Fact]
    public void AnEditThatChangesNothingThePaneShows_RepublishesNothing()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = ProjectWith("Clean", "Intro", "riff1");
        vm.Refresh();

        // Not vacuous: the pane really is showing rows to republish.
        Assert.Single(vm.TonesPanel.Groups);
        Assert.Single(vm.SectionsPanel.Groups);

        var events = CountEventsOn(vm);
        history.Do(new NoOpAction());

        Assert.Equal(0, events());
    }

    /// <summary>
    ///     …and a REAL change still republishes. Without this the case above is
    ///     satisfied by a pane that stopped refreshing.
    /// </summary>
    [Fact]
    public void AnEditThatAddsASection_StillRepublishesThatCollection()
    {
        var (vm, history, state) = NewPane();
        var project = ProjectWith("Clean", "Intro", "riff1");
        state.ActiveProject = project;
        vm.Refresh();

        var events = CountEventsOn(vm);
        project.Arrangements[0].EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(8), Section = "Chorus"
        });
        history.Do(new NoOpAction());

        Assert.True(events() > 0, "a new section name must reach the pane");
        Assert.Equal(["Intro", "Chorus"], vm.SectionsPanel.Groups.Select(s => s.Name));
    }

    /// <summary>
    ///     A changed COUNT is a change even when the names are identical — the
    ///     row prints it.
    /// </summary>
    [Fact]
    public void AChangedCountWithUnchangedNames_StillRepublishes()
    {
        var (vm, history, state) = NewPane();
        var project = ProjectWith("Clean", "Intro", "riff1");
        state.ActiveProject = project;
        vm.Refresh();
        Assert.Single(vm.SectionsPanel.Groups[0].Occurrences);

        project.Arrangements[0].EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(8), Section = "Intro"
        });
        history.Do(new NoOpAction());

        Assert.Equal(["Intro"], vm.SectionsPanel.Groups.Select(s => s.Name));
        Assert.Equal(2, vm.SectionsPanel.Groups[0].Occurrences.Count);
    }

    /// <summary>
    ///     A rename being TYPED survives an unrelated edit. The rows carry the
    ///     user's in-progress <c>EditName</c>, and republishing replaced the
    ///     instance it was bound to.
    /// </summary>
    [Fact]
    public void AHalfTypedRename_SurvivesAnUnrelatedEdit()
    {
        var (vm, history, state) = NewPane();
        state.ActiveProject = ProjectWith("Clean", "Intro", "riff1");
        vm.Refresh();
        var row = vm.SectionsPanel.Groups[0];
        row.EditName = "Intr";

        history.Do(new NoOpAction());

        Assert.Same(row, Assert.Single(vm.SectionsPanel.Groups));
        Assert.Equal("Intr", vm.SectionsPanel.Groups[0].EditName);
    }

    /// <summary>
    ///     Flipping major↔minor still rewrites the key list — the two name
    ///     tables differ, and the guard compares contents rather than assuming
    ///     the list is constant.
    /// </summary>
    [Fact]
    public void FlippingToMinor_StillRewritesTheKeyOptions()
    {
        var (vm, _, state) = NewPane();
        state.ActiveProject = ProjectWith("Clean", "Intro", "riff1");
        vm.Refresh();
        var major = vm.KeyOptions.ToList();

        vm.KeyIsMinor = true;

        Assert.NotEqual(major, vm.KeyOptions.ToList());
        Assert.Equal(major.Count, vm.KeyOptions.Count);
    }

    // ---- fixture -------------------------------------------------------------

    /// <summary>Counts every notification on all three bound collections.</summary>
    private static Func<int> CountEventsOn(SongOverviewViewModel vm)
    {
        var count = 0;
        ((INotifyCollectionChanged)vm.TonesPanel.Groups).CollectionChanged += (_, _) => count++;
        ((INotifyCollectionChanged)vm.SectionsPanel.Groups).CollectionChanged += (_, _) => count++;
        ((INotifyCollectionChanged)vm.KeyOptions).CollectionChanged += (_, _) => count++;
        return () => count;
    }

    private static (SongOverviewViewModel Vm, HistoryService History, ProjectState State) NewPane()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var history = new HistoryService(logger);
        return (new SongOverviewViewModel(state, history, logger, FakeLocalization.English(),
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy), history, state);
    }

    private static ChartProject ProjectWith(string tone, string section, string phrase)
    {
        var project = new ChartProject();
        for (var i = 0; i < 32; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead", StartingTone = tone };
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = new FractionalPosition(0), Section = section, Phrase = phrase
        });
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>An edit that touches nothing the pane reads — a one-note change,
    /// as far as the Song pane is concerned.</summary>
    private sealed class NoOpAction : IChartAction
    {
        public void Execute() { }
        public void Undo() { }
    }
}
