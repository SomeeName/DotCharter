using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     The "Match model" state machine as Settings ▸ AI shows it. The tone
///     panel's one-click offer over the same downloader is
///     <see cref="Tests.UI.ToneDesignMatchOfferTests" />.
///     <para>
///         <b>What this lane got wrong before it existed is the reason for every
///         case here.</b> Nothing outside <c>RigEstimatorOnnx</c> and the python
///         tools named <c>ai-match\rig-estimator</c>, so
///         <c>IRigEstimator.IsAvailable</c> was permanently false, the matcher
///         silently stayed search-only, and the only way to populate the folder
///         was to run <c>export_onnx.py</c> by hand. The settings block is what
///         makes the state VISIBLE; the panel row is what makes it FIXABLE from
///         where the user noticed.
///     </para>
/// </summary>
public sealed class MatchModelViewModelTests
{
    private sealed class StubEstimator(bool available) : IRigEstimator
    {
        public bool IsAvailable => available;

        public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct) =>
            throw new NotSupportedException("these cases never estimate");
    }

    private static MatchModelViewModel Vm(
        IRigEstimatorModelDownloadService? downloads, Func<IRigEstimator?>? estimator = null)
    {
        var vm = new MatchModelViewModel(FakeLocalization.English(), downloads, estimator);
        vm.Refresh();
        return vm;
    }

    /// <summary>
    ///     #349: the installer reports its later steps from a pool thread; the bound detail lines
    ///     must only be written on the UI thread.
    /// </summary>
    [Fact]
    public async Task ProgressReportedOffTheUiThread_IsWrittenOnTheUiThread()
    {
        var ui = new PumpedDispatcher();
        var downloads = new FakeRigEstimatorModelDownloadService { ReportOnPoolThread = true };
        var vm = new MatchModelViewModel(FakeLocalization.English(), downloads, ui: ui);
        vm.Refresh();
        var writers = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MatchModelViewModel.ModelDetail) or nameof(MatchModelViewModel.LibraryDetail))) return;
            // Only the progress steps: the caller's own final write lands wherever its await resumed.
            var detail = e.PropertyName == nameof(MatchModelViewModel.ModelDetail) ? vm.ModelDetail : vm.LibraryDetail;
            if (detail is "Downloading…" or "Unpacking…") writers.Add(ui.CheckAccess());
        };

        // The gate holds each download open while its steps are pumped: a step that lands after the
        // download returned is dropped by design.
        foreach (var command in new[] { vm.DownloadModelCommand, vm.DownloadLibraryCommand })
        {
            var gate = new TaskCompletionSource();
            downloads.Gate = gate;
            var before = writers.Count;
            var run = command.ExecuteAsync(null);
            while (writers.Count < before + 2 && !run.IsCompleted)
            {
                ui.Pump();
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }

            gate.SetResult();
            await run;
            ui.Pump();
        }

        Assert.NotEmpty(writers);
        Assert.All(writers, onUi => Assert.True(onUi));
    }

    /// <summary>A UI thread that is the test's own: posted work waits until <see cref="Pump" />.</summary>
    private sealed class PumpedDispatcher : DotCharter.Tone.Diagnostics.IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        private volatile int _uiThread = -1;
        public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThread;
        public void Post(Action action) => _queue.Enqueue(action);
        public void PostNormal(Action action) => _queue.Enqueue(action);

        public void Pump()
        {
            // Whichever thread pumps is the UI thread for the duration (the test body hops threads across awaits).
            _uiThread = Environment.CurrentManagedThreadId;
            while (_queue.TryDequeue(out var action)) action();
            _uiThread = -1;
        }

        public Task InvokeAsync(Action action) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(Func<T> func) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => throw new NotSupportedException();
    }

    // ---- the tone library row -------------------------------------------------

    [Fact]
    public void TheLibraryRowOffersTheDownloadAndNamesItsSize_WhenMissing()
    {
        var vm = Vm(new FakeRigEstimatorModelDownloadService { LibraryApproximateMegabytes = 9 });

        Assert.False(vm.LibraryInstalled);
        Assert.True(vm.CanDownloadLibrary);
        Assert.Contains("9", vm.LibraryStatus, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.LibraryDetail);
    }

    [Fact]
    public void TheLibraryRowReportsInstalled_AndWithdrawsTheOffer()
    {
        var vm = Vm(new FakeRigEstimatorModelDownloadService { IsLibraryInstalled = true });

        Assert.True(vm.LibraryInstalled);
        Assert.False(vm.CanDownloadLibrary);
        Assert.NotEqual(string.Empty, vm.LibraryStatus);
    }

    [Fact]
    public void WithNoDownloaderTheLibraryRowSaysNothing()
    {
        var vm = Vm(null);

        Assert.False(vm.CanDownloadLibrary);
        Assert.Equal(string.Empty, vm.LibraryStatus);
    }

    /// <summary>The library and the model are two downloads: fetching one
    /// touches neither the other's state nor its call count.</summary>
    [Fact]
    public async Task ASuccessfulLibraryDownloadFlipsOnlyTheLibraryRow()
    {
        var downloads = new FakeRigEstimatorModelDownloadService();
        var vm = Vm(downloads);

        await vm.DownloadLibraryCommand.ExecuteAsync(null);

        Assert.Equal(1, downloads.LibraryCalls);
        Assert.Equal(0, downloads.Calls);
        Assert.True(vm.LibraryInstalled);
        Assert.False(vm.IsLibraryDownloading);
        Assert.False(vm.CanDownloadLibrary);
        Assert.False(vm.LibraryIsError);
        Assert.Equal(string.Empty, vm.LibraryDetail);
        Assert.False(vm.ModelInstalled);
        Assert.True(vm.CanDownloadModel);
    }

    [Fact]
    public async Task AFailedLibraryDownloadShowsTheReasonAndStaysOffered()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { FailWith = "the download failed its SHA-256 check" };
        var vm = Vm(downloads);

        await vm.DownloadLibraryCommand.ExecuteAsync(null);

        Assert.False(vm.LibraryInstalled);
        Assert.True(vm.LibraryIsError);
        Assert.Contains("SHA-256", vm.LibraryDetail, StringComparison.Ordinal);
        Assert.True(vm.CanDownloadLibrary);
    }

    [Fact]
    public async Task ACancelledLibraryDownloadSaysSoAndIsNotAnError()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { ThrowCancelled = true };
        var vm = Vm(downloads);

        await vm.DownloadLibraryCommand.ExecuteAsync(null);

        Assert.False(vm.LibraryInstalled);
        Assert.False(vm.LibraryIsError);
        Assert.NotEqual(string.Empty, vm.LibraryDetail);
        Assert.True(vm.CanDownloadLibrary);
    }

    [Fact]
    public async Task WhileTheLibraryDownloadsTheOfferIsWithdrawnAndCancelIsLive()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { Gate = new TaskCompletionSource() };
        var vm = Vm(downloads);

        var run = vm.DownloadLibraryCommand.ExecuteAsync(null);

        Assert.True(vm.IsLibraryDownloading);
        Assert.False(vm.CanDownloadLibrary);
        Assert.True(vm.CancelLibraryDownloadCommand.CanExecute(null));
        Assert.True(vm.CanDownloadModel, "the model's own offer is untouched by a library download");

        downloads.Gate.SetResult();
        await run;

        Assert.False(vm.IsLibraryDownloading);
        Assert.True(vm.LibraryInstalled);
    }

    [Fact]
    public void WithNoDownloaderTheBlockIsHiddenAndSaysNothing()
    {
        var vm = Vm(null);

        Assert.False(vm.IsGroupVisible);
        Assert.False(vm.CanDownloadModel);
        Assert.Equal(string.Empty, vm.ModelStatus);
    }

    /// <summary>
    ///     The not-installed line names the SIZE, because a 200 MB download
    ///     behind an unlabelled button is the thing a user cancels halfway.
    /// </summary>
    [Fact]
    public void NotInstalledOffersTheDownloadAndNamesItsSize()
    {
        var vm = Vm(new FakeRigEstimatorModelDownloadService { ApproximateMegabytes = 214 });

        Assert.True(vm.IsGroupVisible);
        Assert.False(vm.ModelInstalled);
        Assert.True(vm.CanDownloadModel);
        Assert.False(vm.ModelIsError);
        Assert.Contains("214", vm.ModelStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledReportsTheModelIdEpochAndSize()
    {
        var vm = Vm(new FakeRigEstimatorModelDownloadService
        {
            IsInstalled = true,
            Info = new RigEstimatorModelInfo(
                "rig-estimator-clap-v2-20260913", 42, "clap", "v2",
                TrainedOnCrudeSmokeSet: false, BytesOnDisk: 201 * 1024 * 1024)
        });

        Assert.True(vm.ModelInstalled);
        Assert.False(vm.CanDownloadModel);
        Assert.False(vm.ModelIsError);
        Assert.Contains("rig-estimator-clap-v2-20260913", vm.ModelStatus, StringComparison.Ordinal);
        Assert.Contains("42", vm.ModelStatus, StringComparison.Ordinal);
        Assert.Contains("201", vm.ModelStatus, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>Installed and USABLE are two different questions.</b> A model
    ///     exported against a different label space installs perfectly and
    ///     refuses to load; a page that reported only "installed" would send
    ///     that user looking for a bug in the matcher.
    /// </summary>
    [Fact]
    public void InstalledButUnloadableIsReportedAsAnError()
    {
        var vm = Vm(
            new FakeRigEstimatorModelDownloadService { IsInstalled = true },
            () => new StubEstimator(available: false));

        Assert.True(vm.ModelInstalled);
        Assert.True(vm.ModelIsError);
        Assert.NotEqual(string.Empty, vm.ModelStatus);
    }

    /// <summary>
    ///     Asking the estimator builds its ONNX session: 3.8 s the first time Settings opened on
    ///     the owner's machine, on the UI thread. With a dispatcher the question goes to a pool
    ///     thread, the row says "installed" at once, and a NO still arrives.
    /// </summary>
    [Fact]
    public async Task WithADispatcher_TheLoadProbeNeverRunsInsideRefresh_AndANoStillArrives()
    {
        // The stub records whether it was asked by the very call to Refresh: while Refresh runs,
        // on the thread running it. A probe on a pool thread, early or late, is neither; nothing
        // here depends on how soon a continuation is scheduled. The probe then holds its "no"
        // until the test has read the row; the 60 s wait is only a deadlock detector.
        using var release = new ManualResetEventSlim();
        var refreshThread = Environment.CurrentManagedThreadId;
        var insideRefresh = 0;
        var askedInsideRefresh = false;
        var released = false;
        var vm = new MatchModelViewModel(FakeLocalization.English(),
            new FakeRigEstimatorModelDownloadService { IsInstalled = true },
            () =>
            {
                askedInsideRefresh |= Volatile.Read(ref insideRefresh) == 1
                    && Environment.CurrentManagedThreadId == refreshThread;
                BoundedWait.For(release, "the model probe release");
                released = true;
                return new StubEstimator(available: false);
            },
            ui: new TestUiDispatcher());
        Volatile.Write(ref insideRefresh, 1);
        vm.Refresh();
        Volatile.Write(ref insideRefresh, 0);

        Assert.False(askedInsideRefresh, "the estimator was asked inside Refresh, i.e. in the frame");
        Assert.False(vm.ModelIsError);
        release.Set();
        await BoundedWait.ForAsync(vm.PendingProbe!, "the model probe completion", TestContext.Current.CancellationToken);
        Assert.True(released, "the model probe release never arrived; its failure was caught by the view model");
        Assert.False(askedInsideRefresh, "the estimator was asked inside Refresh, i.e. in the frame");
        Assert.True(vm.ModelIsError);
    }

    [Fact]
    public void InstalledAndLoadableIsNotAnError()
    {
        var vm = Vm(
            new FakeRigEstimatorModelDownloadService { IsInstalled = true },
            () => new StubEstimator(available: true));

        Assert.False(vm.ModelIsError);
    }

    /// <summary>The smoke-set flag is SURFACED: a user whose guesses are
    /// nonsense should be able to see why without reading a log.</summary>
    [Fact]
    public void ASmokeSetExportSaysSoOnScreen()
    {
        var plain = Vm(new FakeRigEstimatorModelDownloadService
        {
            IsInstalled = true,
            Info = new RigEstimatorModelInfo("m", 1, null, null, false, 1024)
        });
        var smoke = Vm(new FakeRigEstimatorModelDownloadService
        {
            IsInstalled = true,
            Info = new RigEstimatorModelInfo("m", 1, null, null, true, 1024)
        });

        Assert.True(smoke.ModelStatus.Length > plain.ModelStatus.Length,
            "a smoke-set export must add its warning to the status line");
    }

    /// <summary>
    ///     The expensive probe is NEVER taken on a machine with no model: asking
    ///     <c>IsAvailable</c> builds an ONNX session, and the settings page is
    ///     composed on the UI thread.
    /// </summary>
    [Fact]
    public void TheEstimatorIsNotProbedWhileNothingIsInstalled()
    {
        var probes = 0;
        _ = Vm(new FakeRigEstimatorModelDownloadService(), () =>
        {
            probes++;
            return new StubEstimator(available: true);
        });

        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task ASuccessfulDownloadFlipsTheBlockToInstalled()
    {
        var downloads = new FakeRigEstimatorModelDownloadService();
        var vm = Vm(downloads);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.Equal(1, downloads.Calls);
        Assert.True(vm.ModelInstalled);
        Assert.False(vm.IsModelDownloading);
        Assert.False(vm.CanDownloadModel);
        Assert.False(vm.ModelIsError);
        Assert.Equal(string.Empty, vm.ModelDetail);
    }

    /// <summary>
    ///     A failure is non-throwing and carries its REASON to the screen —
    ///     "could not download" with no cause is the message that makes a user
    ///     file a bug the log already answers.
    /// </summary>
    [Fact]
    public async Task AFailedDownloadShowsTheReasonAndStaysOffered()
    {
        var downloads = new FakeRigEstimatorModelDownloadService
        {
            FailWith = "the download failed its SHA-256 check"
        };
        var vm = Vm(downloads);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.False(vm.ModelInstalled);
        Assert.True(vm.ModelIsError);
        Assert.Contains("SHA-256", vm.ModelDetail, StringComparison.Ordinal);
        Assert.True(vm.CanDownloadModel, "a failed download must still be retryable");
    }

    [Fact]
    public async Task ACancelledDownloadSaysSoAndIsNotAnError()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { ThrowCancelled = true };
        var vm = Vm(downloads);

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.False(vm.ModelInstalled);
        Assert.False(vm.ModelIsError);
        Assert.NotEqual(string.Empty, vm.ModelDetail);
    }

    /// <summary>
    ///     While a download runs the offer is withdrawn and Cancel is live.
    ///     Both are CanExecute gates, which is the half that silently stops
    ///     working when a NotifyCanExecuteChangedFor is missed.
    /// </summary>
    [Fact]
    public async Task WhileDownloadingTheOfferIsWithdrawnAndCancelIsLive()
    {
        var gate = new TaskCompletionSource();
        var downloads = new FakeRigEstimatorModelDownloadService { Gate = gate };
        var vm = Vm(downloads);

        Assert.False(vm.CancelModelDownloadCommand.CanExecute(null));

        var running = vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.True(vm.IsModelDownloading);
        Assert.False(vm.CanDownloadModel);
        Assert.False(vm.DownloadModelCommand.CanExecute(null));
        Assert.True(vm.CancelModelDownloadCommand.CanExecute(null));
        Assert.NotEqual(string.Empty, vm.ModelDetail);

        gate.SetResult();
        await running;

        Assert.False(vm.IsModelDownloading);
        Assert.False(vm.CancelModelDownloadCommand.CanExecute(null));
    }

    /// <summary>Progress steps reach the line under the bar.</summary>
    [Fact]
    public async Task ProgressStepsAreShown()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { Steps = ["Unpacking the model…"] };
        var vm = Vm(downloads);
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ModelDetail)) seen.Add(vm.ModelDetail);
        };

        await vm.DownloadModelCommand.ExecuteAsync(null);

        Assert.Contains("Unpacking the model…", seen);
    }

    /// <summary>The open-folder link points where the estimator READS, so a
    /// hand-placed export lands somewhere that works.</summary>
    [Fact]
    public void TheFolderLinkPointsAtTheModelDirectory()
    {
        var downloads = new FakeRigEstimatorModelDownloadService { ModelDirectory = @"C:\x\rig-estimator" };

        Assert.Equal(@"C:\x\rig-estimator", Vm(downloads).ModelDirectory);
    }
}
