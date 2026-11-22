using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

public sealed class ArrangementSwitchTimingTests
{
    [Fact]
    public void ARealSwitchAnnouncesItsStartBeforeAnySubscriberRuns()
    {
        var previous = new Arrangement();
        var next = new Arrangement();
        var state = new ProjectState { ActiveArrangement = previous };
        var events = new List<string>();
        state.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName != nameof(ProjectState.ActiveArrangement)) return;
            Assert.Same(previous, state.ActiveArrangement);
            events.Add("start");
        };
        state.PropertyChanged += (_, _) => events.Add("subscriber");

        state.ActiveArrangement = next;

        Assert.Equal("start", events[0]);
        Assert.Equal(1, events.Count(e => e == "start"));
    }

    [Fact]
    public void ReassigningOrRepublishingTheSameArrangementIsNotASwitch()
    {
        var arrangement = new Arrangement();
        var state = new ProjectState { ActiveArrangement = arrangement };
        var starts = 0;
        state.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.ActiveArrangement)) starts++;
        };

        state.ActiveArrangement = arrangement;
        state.NotifyActiveArrangementChanged();

        Assert.Equal(0, starts);
    }

    [Fact]
    public void ASwitchBetweenTwoArrangementsOfTheOpenProjectIsTimed()
    {
        var (state, project) = OpenProject();
        var timed = RecordSwitches(state);

        state.ActiveArrangement = project.Arrangements[1];
        state.ActiveArrangement = project.Arrangements[0];

        Assert.Equal([true, true], timed);
    }

    [Fact]
    public void AnOpenThatReplacesTheProjectIsNotASwitch()
    {
        var (state, _) = OpenProject();
        var timed = RecordSwitches(state);
        var next = new ChartProject { Arrangements = { new Arrangement(), new Arrangement() } };

        // ApplyProject's order: the project first, then its first arrangement.
        state.ActiveProject = next;
        state.ActiveArrangement = next.Arrangements[0];

        Assert.Equal([false], timed);
    }

    [Fact]
    public void TheArrangementAnOpenRestoresIsNotASwitch()
    {
        var (state, project) = OpenProject();
        var timed = RecordSwitches(state);

        using (state.BeginProjectOpen())
        {
            // ProjectViewMemory.Restore: same project, remembered arrangement.
            state.ActiveArrangement = project.Arrangements[1];
        }

        state.ActiveArrangement = project.Arrangements[0];

        Assert.Equal([false, true], timed);
        Assert.False(state.IsOpeningProject);
    }

    [Fact]
    public void LeavingANullOrRemovedArrangementIsNotASwitch()
    {
        var (state, project) = OpenProject();
        state.ActiveArrangement = null;
        var timed = RecordSwitches(state);

        state.ActiveArrangement = project.Arrangements[0];
        project.Arrangements.RemoveAt(0);
        state.ActiveArrangement = project.Arrangements[0];

        Assert.Equal([false, false], timed);
    }

    [Fact]
    public void OpenScopesNestAndADoubleDisposeCountsOnce()
    {
        var state = new ProjectState();
        var outer = state.BeginProjectOpen();
        var inner = state.BeginProjectOpen();
        inner.Dispose();
        inner.Dispose();

        Assert.True(state.IsOpeningProject);
        outer.Dispose();
        Assert.False(state.IsOpeningProject);
    }

    [Fact]
    public void TheAppTimesOnlyUserSwitchesAndEveryOpenPathTakesTheScope()
    {
        var app = Source("DotCharter.UI", "App.axaml.cs");
        Assert.Contains("ArrangementSwitchTiming.Observe(", app);
        Assert.DoesNotContain("Arrangement switch #", app);

        var timing = Source("DotCharter.UI", "Views", "Shell", "ArrangementSwitchTiming.cs");
        Assert.Contains("IsUserSwitch(state)", timing);
        Assert.Contains("SurfaceShowTiming.Begin($\"Arrangement switch #{++sequence}\", DispatcherPriority.ApplicationIdle)", timing);

        foreach (var path in new[]
                 {
                     Source("DotCharter.Services", "Session", "ProjectSessionService.cs"),
                     Source("DotCharter.UI", "ViewModels", "Panels", "SongBrowserViewModel.cs")
                 })
        {
            Assert.Contains("using var opening = _projectState.BeginProjectOpen();", path);
        }
    }

    private static (ProjectState State, ChartProject Project) OpenProject()
    {
        var project = new ChartProject { Arrangements = { new Arrangement(), new Arrangement() } };
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = project.Arrangements[0] };
        return (state, project);
    }

    private static List<bool> RecordSwitches(ProjectState state)
    {
        var timed = new List<bool>();
        state.PropertyChanging += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectState.ActiveArrangement)) timed.Add(ArrangementSwitchTiming.IsUserSwitch(state));
        };
        return timed;
    }

    private static string Source(params string[] parts) =>
        SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(
            Path.Combine([TestPaths.RepoRoot(), .. parts]))));
}
