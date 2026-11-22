using System.Text.Json;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

/// <summary>
///     per project, the active arrangement, the ghost and stacked selections
///     and the playhead come back when the project opens again, through a config that
///     round-trips as JSON (a restart). Opening another project must not erase them.
/// </summary>
public sealed class ProjectViewMemoryTests
{
    private const string Path1 = @"C:\charts\one\one.dcproj";

    private sealed class Session
    {
        public Session(FakeConfigurationService config)
        {
            Config = config;
            Ghosts = new GhostArrangementsService();
            Stacks = new StackedArrangementsService();
            Memory = new ProjectViewMemory(config, State, Ghosts, Stacks);
        }

        public FakeConfigurationService Config { get; }
        public ProjectState State { get; } = new();
        public GhostArrangementsService Ghosts { get; }
        public StackedArrangementsService Stacks { get; }
        public ProjectViewMemory Memory { get; }

        /// <summary>The order the session service uses: path, swap, restore.</summary>
        public ChartProject Open(string path, out double? playhead)
        {
            var project = NewProject();
            State.CurrentProjectPath = path;
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements[0];
            Ghosts.Clear();
            Stacks.Clear();
            playhead = Memory.Restore(project, path);
            return project;
        }
    }

    private static ChartProject NewProject() => new()
    {
        Arrangements =
        [
            new Arrangement { Name = "Lead" }, new Arrangement { Name = "Rhythm" }, new Arrangement { Name = "Bass" },
        ],
    };

    private static FakeConfigurationService Restart(FakeConfigurationService old) => new()
    {
        AppConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(old.AppConfig))!,
    };

    [Fact]
    public void ArrangementGhostsStacksAndPlayhead_SurviveARestart()
    {
        var before = new Session(new FakeConfigurationService());
        var project = before.Open(Path1, out _);
        before.State.ActiveArrangement = project.Arrangements[2];
        before.Ghosts.SetVisible(project.Arrangements[1], true);
        before.Stacks.SetStacked(project.Arrangements[0], true);
        before.State.CurrentTimeMs = 12_345;

        var after = new Session(Restart(before.Config));
        var reopened = after.Open(Path1, out var playhead);

        Assert.Same(reopened.Arrangements[2], after.State.ActiveArrangement);
        Assert.Equal([reopened.Arrangements[1]], after.Ghosts.VisibleGhosts);
        Assert.Equal([reopened.Arrangements[0]], after.Stacks.StackedArrangements);
        Assert.Equal(12_345, playhead);
    }

    [Fact]
    public void OpeningAnotherProject_DoesNotEraseTheFirstProjectsMemory()
    {
        var s = new Session(new FakeConfigurationService());
        var project = s.Open(Path1, out _);
        s.State.ActiveArrangement = project.Arrangements[1];

        // The swap clears the ghosts and re-picks the first arrangement BEFORE the restore runs.
        s.Open(@"C:\charts\two\two.dcproj", out _);
        s.Open(Path1, out _);

        Assert.Equal("Rhythm", s.State.ActiveArrangement!.Name);
    }

    [Fact]
    public void AProjectNeverSeenBefore_OpensOnItsFirstArrangement_WithNoPlayhead()
    {
        var s = new Session(new FakeConfigurationService());
        var project = s.Open(Path1, out var playhead);
        Assert.Same(project.Arrangements[0], s.State.ActiveArrangement);
        Assert.Null(playhead);
    }

    [Fact]
    public void ARenamedOrRemovedArrangement_FallsBackQuietly()
    {
        var before = new Session(new FakeConfigurationService());
        var project = before.Open(Path1, out _);
        before.State.ActiveArrangement = project.Arrangements[2];

        var after = new Session(Restart(before.Config));
        after.Config.AppConfig.ProjectViews.Values.Single().ActiveArrangementName = "Gone";
        var reopened = after.Open(Path1, out _);

        Assert.Same(reopened.Arrangements[0], after.State.ActiveArrangement);
    }

    [Fact]
    public void ThePlayhead_IsWrittenInSteps_NotOnEveryTick()
    {
        var s = new Session(new FakeConfigurationService());
        s.Open(Path1, out _);
        var writes = s.Config.Writes.Count;
        for (var t = 0; t < 900; t += 10) s.State.CurrentTimeMs = t;
        Assert.Equal(writes + 1, s.Config.Writes.Count);
    }

    [Fact]
    public void TheMemory_IsCappedToTheMostRecentProjects()
    {
        var s = new Session(new FakeConfigurationService());
        for (var i = 0; i < ProjectViewMemory.MaxEntries + 5; i++)
        {
            var project = s.Open($@"C:\charts\p{i}\p{i}.dcproj", out _);
            s.State.ActiveArrangement = project.Arrangements[1];
        }

        Assert.Equal(ProjectViewMemory.MaxEntries, s.Config.AppConfig.ProjectViews.Count);
    }
}
