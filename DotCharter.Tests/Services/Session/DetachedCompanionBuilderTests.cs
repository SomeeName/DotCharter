using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Export;
using DotCharter.Services.Session;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Services.Session;

public sealed class DetachedCompanionBuilderTests
{
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_save_builds_the_psarc_from_its_snapshot()
    {
        var companions = new RecordingCompanions();
        var builder = NewBuilder(companions);
        var snapshot = new ChartProject();

        builder.Schedule("song.rscp", snapshot, 1234);
        await Drained(builder);

        Assert.Equal(["song.rscp"], companions.PsarcPaths);
        Assert.Same(snapshot, companions.PsarcSnapshots.Single());
    }

    [Fact]
    public async Task Saves_landing_during_a_build_coalesce_to_the_last_one()
    {
        var companions = new RecordingCompanions { HoldFirstPsarc = true };
        var builder = NewBuilder(companions);

        builder.Schedule("first.rscp", new ChartProject(), null);
        await companions.FirstPsarcStarted.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        var drain = builder.RunningDrain;

        var second = new ChartProject();
        var third = new ChartProject();
        builder.Schedule("first.rscp", second, null);
        builder.Schedule("first.rscp", third, null);
        companions.ReleaseFirstPsarc();
        await Drained(builder);

        Assert.Same(drain, builder.RunningDrain);
        // 3 saves, ONE queued pass after the running one, carrying the latest snapshot.
        Assert.Equal(["first.rscp", "first.rscp"], companions.PsarcPaths);
        Assert.Same(third, companions.PsarcSnapshots[1]);
        Assert.DoesNotContain(second, companions.PsarcSnapshots);
        Assert.Equal(2, companions.EofSnapshots.Count);
    }

    [Fact]
    public async Task The_project_path_decides_what_coalesces_so_a_Save_As_loses_nothing()
    {
        var companions = new RecordingCompanions { HoldFirstPsarc = true };
        var builder = NewBuilder(companions);

        builder.Schedule("old.dcproj", new ChartProject(), null);
        await companions.FirstPsarcStarted.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        builder.Schedule("new.dcproj", new ChartProject(), null);
        companions.ReleaseFirstPsarc();
        await Drained(builder);

        Assert.Equal(["old.dcproj", "new.dcproj"], companions.PsarcPaths);
    }

    [Fact]
    public async Task The_cheap_companions_are_written_in_order_before_the_psarc()
    {
        var companions = new RecordingCompanions();
        var builder = NewBuilder(companions);

        builder.Schedule("song.dcproj", new ChartProject(), null);
        await builder.FlushAsync();

        Assert.Equal([".rscp", "bundle", ".rs2dlc", ".eof", "psarc"], companions.Order);
    }

    [Fact]
    public async Task A_failing_companion_is_logged_shown_and_does_not_stop_the_rest()
    {
        var companions = new RecordingCompanions { EofFailure = new IOException("locked") };
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var builder = new DetachedCompanionBuilder(
            companions, companions.Documents, new TestUiDispatcher(), state, logger, FakeLocalization.English(),
            new RecordingStatusBarService());

        builder.Schedule("song.dcproj", new ChartProject(), null);
        await builder.FlushAsync();

        Assert.True(logger.HasEntryContaining("ERROR", ".eof companion failed"));
        Assert.Equal(StatusSeverity.Error, state.MainDisplaySeverity);
        Assert.Equal([".rscp", "bundle", ".rs2dlc", ".eof", "psarc"], companions.Order);
    }

    [Fact]
    public async Task Stop_lets_the_queued_cheap_companions_finish_but_builds_no_psarc()
    {
        var companions = new RecordingCompanions { HoldFirstPsarc = true };
        var builder = NewBuilder(companions);

        builder.Schedule("first.dcproj", new ChartProject(), null);
        await companions.FirstPsarcStarted.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        builder.Schedule("second.dcproj", new ChartProject(), null);

        builder.Stop();

        Assert.Equal(["first.dcproj", "second.dcproj"], companions.EofPaths);
        Assert.Equal(["first.dcproj"], companions.PsarcPaths);
    }

    [Fact]
    public async Task Stop_drains_queued_files_even_when_a_status_dispatch_is_waiting_for_the_closing_ui()
    {
        var companions = new RecordingCompanions { PsarcResult = "song_p.psarc" };
        var dispatcher = new HeldUiDispatcher();
        var builder = NewBuilder(companions, dispatcher);
        builder.Schedule("first.dcproj", new ChartProject(), null);
        await dispatcher.Entered.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        builder.Schedule("second.dcproj", new ChartProject(), null);

        try
        {
            builder.Stop();

            Assert.Equal(["first.dcproj", "second.dcproj"], companions.EofPaths);
            Assert.True(builder.RunningDrain!.IsCompleted);
        }
        finally
        {
            dispatcher.Release.TrySetResult();
            await Drained(builder);
        }
    }

    private sealed class HeldUiDispatcher : IUiDispatcher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CheckAccess() => false;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();

        public async Task InvokeAsync(Action action)
        {
            Entered.TrySetResult();
            await Release.Task;
            action();
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    [Fact]
    public async Task A_failed_psarc_is_logged_and_the_next_save_builds_again()
    {
        var companions = new RecordingCompanions { PsarcFailure = new IOException("disk full") };
        var logger = new FakeAppLogger();
        var builder = NewBuilder(companions, logger: logger);

        builder.Schedule("song.rscp", new ChartProject(), null);
        await Drained(builder);

        Assert.True(logger.HasEntryContaining("ERROR", "Companion psarc build failed"));

        companions.PsarcFailure = null;
        builder.Schedule("again.rscp", new ChartProject(), null);
        await Drained(builder);
        Assert.Equal(["song.rscp", "again.rscp"], companions.PsarcPaths);
    }

    [Fact]
    public async Task Stop_cancels_the_build_in_flight_and_builds_no_queued_psarc()
    {
        var companions = new RecordingCompanions { HoldFirstPsarc = true };
        var builder = NewBuilder(companions);

        builder.Schedule("first.rscp", new ChartProject(), null);
        await companions.FirstPsarcStarted.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        builder.Schedule("second.rscp", new ChartProject(), null);

        builder.Stop();
        await Drained(builder);

        Assert.True(companions.FirstPsarcWasCancelled);
        Assert.Equal(["first.rscp"], companions.PsarcPaths);
        Assert.DoesNotContain("second.rscp", companions.PsarcPaths);
    }

    [Fact]
    public async Task A_faulted_drain_unlatches_so_the_next_save_builds_again()
    {
        var companions = new RecordingCompanions { PsarcResult = "song_p.psarc" };
        var logger = new FakeAppLogger();
        var builder = NewBuilder(companions, new ThrowingUiDispatcher(), logger);

        builder.Schedule("song.rscp", new ChartProject(), null);
        await Drained(builder);
        var firstDrain = builder.RunningDrain;

        builder.Schedule("song.rscp", new ChartProject(), null);
        await Drained(builder);

        Assert.NotSame(firstDrain, builder.RunningDrain);
        Assert.Equal(2, companions.PsarcPaths.Count);
        Assert.True(logger.HasEntryContaining("ERROR", "drain faulted"));
    }

    private static DetachedCompanionBuilder NewBuilder(
        ISaveCompanionService companions, IUiDispatcher? dispatcher = null, FakeAppLogger? logger = null)
    {
        var documents = companions is RecordingCompanions recording ? recording.Documents : new RecordingDocuments(null);
        return new DetachedCompanionBuilder(
            companions, documents, dispatcher ?? new TestUiDispatcher(), new ProjectState(), logger ?? new FakeAppLogger(),
            FakeLocalization.English(), new RecordingStatusBarService());
    }

    private static async Task Drained(DetachedCompanionBuilder builder)
    {
        Assert.NotNull(builder.RunningDrain);
        await builder.RunningDrain!.WaitAsync(Detector, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingDocuments(RecordingCompanions? owner) : IProjectDocumentService
    {
        public ChartProject LoadChart(string filePath) => throw new NotSupportedException();

        public void SaveChart(string filePath, ChartProject project, double? audioLengthMs = null) { }

        public void RefreshArrangementFileManifest(ChartProject project) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject project, double? audioLengthMs = null) =>
            owner?.Order.Add("bundle");

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) => new();
    }

    private sealed class RecordingCompanions : ISaveCompanionService
    {
        public RecordingCompanions() => Documents = new RecordingDocuments(this);

        public RecordingDocuments Documents { get; }
        public List<string> Order { get; } = [];
        public List<string> EofPaths { get; } = [];
        public List<ChartProject> EofSnapshots { get; } = [];
        public Exception? EofFailure { get; init; }

        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldFirstPsarc { get; init; }
        public Exception? PsarcFailure { get; set; }
        public string? PsarcResult { get; init; }

        public TaskCompletionSource FirstPsarcStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstPsarcWasCancelled { get; private set; }
        public List<string> PsarcPaths { get; } = [];
        public List<ChartProject> PsarcSnapshots { get; } = [];

        public void ReleaseFirstPsarc() => _releaseFirst.TrySetResult();

        public string? WriteCharterProject(string projectPath, ChartProject snapshot)
        {
            Order.Add(".rscp");
            return null;
        }

        public string? WriteEof(string projectPath, ChartProject snapshot)
        {
            Order.Add(".eof");
            EofPaths.Add(projectPath);
            EofSnapshots.Add(snapshot);
            return EofFailure is null ? null : throw EofFailure;
        }

        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs)
        {
            Order.Add(".rs2dlc");
            return null;
        }

        public async Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot, double? audioLengthMs,
            CancellationToken cancellationToken)
        {
            var isFirst = PsarcPaths.Count == 0;
            Order.Add("psarc");
            PsarcPaths.Add(projectPath);
            PsarcSnapshots.Add(snapshot);

            if (isFirst && HoldFirstPsarc)
            {
                FirstPsarcStarted.TrySetResult();
                try
                {
                    await _releaseFirst.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstPsarcWasCancelled = true;
                    throw;
                }
            }

            if (PsarcFailure is not null)
                throw PsarcFailure;

            return PsarcResult;
        }
    }

    private sealed class ThrowingUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => false;

        public void Post(Action action) => throw new InvalidOperationException("the dispatcher is gone");

        public void PostNormal(Action action) => throw new InvalidOperationException("the dispatcher is gone");

        public Task InvokeAsync(Action action) => throw new InvalidOperationException("the dispatcher is gone");

        public Task<T> InvokeAsync<T>(Func<T> func) => throw new InvalidOperationException("the dispatcher is gone");

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => throw new InvalidOperationException("the dispatcher is gone");

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick) => throw new InvalidOperationException("the dispatcher is gone");
    }
}
