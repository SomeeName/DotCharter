using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #497: the Issues tab's "Whole project" block carries what the Chart quality report dialog showed
///     across ALL arrangements: the verdict, the total counts and one row per arrangement. The rules run
///     off the UI thread over a copy; the refresh itself only posts; clicking a row makes its arrangement active.
/// </summary>
public sealed class WholeProjectReportTests
{
    [Fact]
    public async Task TheRefresh_OnlyPosts_TheRulesRunOffTheCallingThread_AndEveryArrangementGetsARow()
    {
        var (h, rules, ui, report) = Create();
        // The caller stays BLOCKED while the check runs, so the worker cannot be it.
        report.ProjectReportOffThread = work =>
        {
            var run = Task.Run(work);
            run.Wait(TestContext.Current.CancellationToken);
            return run;
        };

        report.RefreshChartReport();

        Assert.Equal(0, rules.Calls); // nothing ran in the command: the copy waits for the dispatcher
        Assert.True(report.IsProjectChecking);
        var caller = Environment.CurrentManagedThreadId;
        ui.RunPosted(); // the copy is taken, the worker starts
        await Until(() => ui.Count > 0);
        ui.RunPosted(); // the result lands

        await report.ProjectReportPending;
        Assert.False(report.IsProjectChecking);
        Assert.DoesNotContain(caller, rules.Threads);
        Assert.Equal(["Lead", "Rhythm"], report.ProjectArrangements.Select(r => r.Name));
        Assert.True(report.HasProjectReport);
        Assert.NotEmpty(report.ProjectCounts);
        var lead = report.ProjectArrangements[0];
        var rhythm = report.ProjectArrangements[1];
        Assert.False(rhythm.IsClean);
        Assert.NotEqual(lead.Counts, rhythm.Counts);
        Assert.Equal(h.Project.Arrangements[1].Name, rhythm.Name);
    }

    [Fact]
    public async Task AClickOnARow_MakesThatArrangementActive()
    {
        var (h, _, ui, report) = Create();
        report.RefreshChartReport();
        await Settle(ui, report);

        report.SelectProjectArrangementCommand.Execute(report.ProjectArrangements[1]);

        Assert.Same(h.Project.Arrangements[1], h.Editor.ActiveArrangement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRows_AddUpToTheTotals_WithASongWideRowForTheLyrics(bool withInstruments)
    {
        var (h, _, ui, report) = Create();
        if (!withInstruments) h.Project.Arrangements.Clear();
        var phrase = new DotCharter.Core.Domain.Vocals.LyricPhrase { Position = new FractionalPosition(2) };
        phrase.Lyrics.Add(new DotCharter.Core.Domain.Vocals.LyricEvent { Text = "hold", Position = new FractionalPosition(2) });
        phrase.Lyrics.Add(new DotCharter.Core.Domain.Vocals.LyricEvent
        {
            Text = "on to", Position = new FractionalPosition(3), IsPhraseEnd = true
        });
        var track = new DotCharter.Core.Domain.Vocals.VocalTrack();
        track.Phrases.Add(phrase);
        h.Project.VocalTracks.Add(track);

        report.RefreshChartReport();
        await Settle(ui, report);

        var songWide = Assert.Single(report.ProjectArrangements, row => row.IsSongWide);
        Assert.NotEmpty(songWide.Name);
        Assert.True(songWide.FindingCount >= 2, $"the two lyric findings, got {songWide.FindingCount}");
        Assert.Equal(report.ProjectFindingCount, report.ProjectArrangements.Sum(row => row.FindingCount));

        var active = h.Editor.ActiveArrangement;
        var opened = 0;
        report.ProjectArrangementSelected = () => opened++;
        report.SelectProjectArrangementCommand.Execute(songWide);
        Assert.Equal(1, opened);
        Assert.Same(active, h.Editor.ActiveArrangement);
    }

    [Fact]
    public async Task NoSongWideFindings_NoSongWideRow()
    {
        var (_, _, ui, report) = Create();
        report.RefreshChartReport();
        await Settle(ui, report);

        Assert.DoesNotContain(report.ProjectArrangements, row => row.IsSongWide);
        Assert.Equal(report.ProjectFindingCount, report.ProjectArrangements.Sum(row => row.FindingCount));
    }

    [Fact]
    public async Task ACheckThatLandsAfterANewerOne_IsDropped()
    {
        var (_, rules, ui, report) = Create();
        report.RefreshChartReport();
        report.RefreshChartReport(); // the first post is now stale
        await Settle(ui, report);

        Assert.Equal(2, rules.Calls); // one pass: two arrangements
    }

    [Fact]
    public void NoProject_ClearsTheBlock()
    {
        var (h, _, _, report) = Create();
        h.State.ActiveProject = null;

        report.RefreshChartReport();

        Assert.False(report.HasProjectReport);
        Assert.Empty(report.ProjectArrangements);
    }

    private static async Task Settle(QueueDispatcher ui, ExportReadinessViewModel report)
    {
        ui.RunPosted();
        await Until(() => ui.Count > 0);
        ui.RunPosted();
        await report.ProjectReportPending;
    }

    private static async Task Until(Func<bool> ready)
    {
        // Hang detector, not a timing assertion.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        while (!ready()) await Task.Delay(5, cts.Token);
    }

    private static (ChartEditHarness H, FakeRules Rules, QueueDispatcher Ui, ExportReadinessViewModel Report) Create()
    {
        var h = new ChartEditHarness();
        var rhythm = new Arrangement { Name = "Rhythm" };
        rhythm.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        // The export gate's "no show lights" warning is song-wide and would add a row: this chart has them.
        h.Project.Showlights.Add(new DotCharter.Core.Domain.Events.Showlight());
        h.Project.Arrangements.Add(rhythm);
        var rules = new FakeRules();
        var ui = new QueueDispatcher();
        var report = new ExportReadinessViewModel(h.State, new CheckpointsViewModelTests.FakeDocumentService(), h.Transport,
            h.Editor, FakeLocalization.English(), RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(), new ChartQualityReporter(rules), ui);
        return (h, rules, ui, report);
    }

    /// <summary>One issue for "Rhythm", none for the others.</summary>
    private sealed class FakeRules : IArrangementValidationService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<int> Threads { get; } = [];

        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null)
        {
            Interlocked.Increment(ref _calls);
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            return arrangement!.Name == "Rhythm" ? [new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "no end phrase", 500)] : [];
        }
    }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posted = new();

        public int Count
        {
            get
            {
                lock (_posted) return _posted.Count;
            }
        }

        public bool CheckAccess() => false;

        public void Post(Action action)
        {
            lock (_posted) _posted.Enqueue(action);
        }

        public void PostNormal(Action action) => Post(action);

        public Task InvokeAsync(Action action)
        {
            Post(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public void RunPosted()
        {
            Action[] batch;
            lock (_posted)
            {
                batch = [.. _posted];
                _posted.Clear();
            }

            foreach (var action in batch) action();
        }
    }
}
