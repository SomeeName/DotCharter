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

        builder.Schedule("second.rscp", new ChartProject(), null);
        builder.Schedule("third.rscp", new ChartProject(), null);
        companions.ReleaseFirstPsarc();
        await Drained(builder);

        Assert.Same(drain, builder.RunningDrain);
        Assert.Equal(["first.rscp", "third.rscp"], companions.PsarcPaths);
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
    public async Task Stop_cancels_the_build_in_flight_and_drops_the_queued_save()
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
        return new DetachedCompanionBuilder(
            companions, dispatcher ?? new TestUiDispatcher(), new ProjectState(), logger ?? new FakeAppLogger(),
            FakeLocalization.English());
    }

    private static async Task Drained(DetachedCompanionBuilder builder)
    {
        Assert.NotNull(builder.RunningDrain);
        await builder.RunningDrain!.WaitAsync(Detector, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingCompanions : ISaveCompanionService
    {
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

        public string? WriteEof(string projectPath, ChartProject snapshot) => null;

        public string? WriteDlcBuilderProject(string projectPath, ChartProject snapshot, double? audioLengthMs) => null;

        public async Task<string?> BuildPsarcAsync(string projectPath, ChartProject snapshot, double? audioLengthMs,
            CancellationToken cancellationToken)
        {
            var isFirst = PsarcPaths.Count == 0;
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
