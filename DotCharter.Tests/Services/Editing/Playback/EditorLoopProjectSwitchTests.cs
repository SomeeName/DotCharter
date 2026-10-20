using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Playback;

/// <summary>
///     A loop belongs to the project it was punched in.
///     <para>
///         <c>EditorLoopService</c>, <c>ProjectState</c> and the selection service
///         are all app-wide singletons.
///         <c>ChartEditorViewModel.ApplyProject</c> clears the SELECTION on a swap
///         and stops there — so the loop region survived a project switch, the
///         timeline drew a loop band over the incoming chart, and playback wrapped
///         at a time that means nothing in it. An unfinished punch-IN was worse:
///         the next "loop end" pressed in the NEW song built a region reaching
///         back to a moment of the old one.
///     </para>
/// </summary>
public sealed class EditorLoopProjectSwitchTests
{
    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var scope = new EditingScope(State);
            Loop = new EditorLoopService(State, new EditTargetService(Selection, scope, Config), scope, logger);
            State.ActiveProject = NewProject();
            State.TotalAudioLengthMs = 16000;
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public EditorLoopService Loop { get; }

        public static ChartProject NewProject()
        {
            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
            var arr = new Arrangement { Name = "Lead" };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);
            return project;
        }
    }

    [Fact]
    public void OpeningAnotherProject_ForgetsTheLoopTheClosedOneCarried()
    {
        var h = new Harness();
        h.Loop.SetStart(1000);
        Assert.True(h.Loop.SetEnd(3000));
        Assert.True(h.Loop.IsEnabled);

        h.State.ActiveProject = Harness.NewProject();

        Assert.Null(h.Loop.Region);
        Assert.False(h.Loop.IsEnabled);
        Assert.False(h.State.LoopEnabled);
    }

    [Fact]
    public void OpeningAnotherProject_ForgetsAnUnfinishedPunchIn()
    {
        var h = new Harness();
        h.Loop.SetStart(120_000);   // a minute into the song being closed

        h.State.ActiveProject = Harness.NewProject();
        Assert.Null(h.State.LoopPendingStartMs);

        // ...so an end punched in the NEW song cannot build a region reaching
        // back into the old one; with no armed start it is simply refused.
        Assert.False(h.Loop.SetEnd(2000));
        Assert.Null(h.Loop.Region);
    }

    [Fact]
    public void OpeningAnotherProject_EndsAnAuditionThatWasStillRunning()
    {
        var h = new Harness();
        Assert.True(h.Loop.BeginAudition(1000, 3000));
        Assert.True(h.State.LoopIsAudition);

        h.State.ActiveProject = Harness.NewProject();

        Assert.False(h.State.LoopIsAudition);
        Assert.Null(h.Loop.Region);
    }

    /// <summary>
    ///     Re-assigning the SAME project raises nothing, so the loop stays — an
    ///     undo of a whole-project tool restores in place
    ///     (<c>ChartProjectRestorer</c>) and must not throw away the region the
    ///     user is practising against.
    /// </summary>
    [Fact]
    public void ReassigningTheSameProject_KeepsTheLoop()
    {
        var h = new Harness();
        h.Loop.SetStart(1000);
        h.Loop.SetEnd(3000);

        h.State.ActiveProject = h.State.ActiveProject;

        Assert.Equal(new LivePlayLoop(1000, 3000), h.Loop.Region);
        Assert.True(h.Loop.IsEnabled);
    }
}
