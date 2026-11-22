using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.State;
using DotCharter.Localization;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>
///     Writes every save companion — the Charter <c>.rscp</c>, the DTRS XML bundle, the
///     <c>.rs2dlc</c>, the <c>.eof</c> and the <c>.psarc</c> — DETACHED from the save that
///     asked for it: a save is done once <c>project.dcproj</c> is atomically replaced, and
///     this queue runs afterwards. ONE worker, so two writes of the same companion never
///     overlap. It coalesces: a save landing while a pass is in flight replaces the queued
///     snapshot of the SAME project path (the newest wins) instead of queueing another pass;
///     a snapshot for ANOTHER path (Save As) is kept, so nothing is lost. A companion
///     failure is logged and shown in the status bar. <see cref="Stop" /> (app exit) still
///     lets the queued cheap companions finish, bounded, and only abandons the psarc.
/// </summary>
internal sealed class DetachedCompanionBuilder
{
    /// <summary>Stable id so a burst of saves shows one quiet pill, not one per save.</summary>
    private const string CompanionTaskId = "project-companions";

    /// <summary>How long app exit waits for the queued companions before giving up on them.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(15);

    private readonly ISaveCompanionService _saveCompanions;
    private readonly IProjectDocumentService _documents;
    private readonly IStatusBarService? _statusBar;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ProjectState _projectState;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly Func<int> _volumeTargetLufs;

    private readonly Lock _gate = new();
    private readonly List<CompanionWork> _pending = [];
    private bool _running;
    private bool _stopped;
    private CancellationTokenSource? _cts;

    public DetachedCompanionBuilder(
        ISaveCompanionService saveCompanions,
        IProjectDocumentService documents,
        IUiDispatcher uiDispatcher,
        ProjectState projectState,
        IAppLogger logger,
        ILocalizationService localization,
        IStatusBarService? statusBar,
        Func<int>? volumeTargetLufs = null)
    {
        _saveCompanions = saveCompanions;
        _documents = documents;
        _statusBar = statusBar;
        _uiDispatcher = uiDispatcher;
        _projectState = projectState;
        _logger = logger;
        _localization = localization;
        _volumeTargetLufs = volumeTargetLufs ?? (() => RocksmithAutoVolume.DefaultTargetLufs);
    }

    internal Task? RunningDrain { get; private set; }

    /// <summary>
    ///     Queues the companions of one save. Coalescing, not queueing: while a pass runs,
    ///     further saves of the same project replace the pending snapshot, so holding Ctrl+S
    ///     does not spawn a backlog — the last save wins and is written once.
    /// </summary>
    public void Schedule(string projectPath, ChartProject snapshot, double? audioLengthMs)
    {
        lock (_gate)
        {
            _pending.RemoveAll(w => string.Equals(w.Path, projectPath, StringComparison.OrdinalIgnoreCase));
            _pending.Add(new CompanionWork(projectPath, snapshot, audioLengthMs,
                ProjectVolumeAdoption.Capture(_projectState, projectPath, snapshot)));
            if (_running) return; // the running drain will pick this up
            _running = true;
            RunningDrain = Task.Run(DrainAsync);
        }
    }

    /// <summary>Completes once every companion queued so far has been written (tests, and callers that must see the files).</summary>
    internal async Task FlushAsync()
    {
        while (RunningDrain is { IsCompleted: false } drain) await drain;
    }

    /// <summary>
    ///     App exit: abandons the in-flight psarc build (it stages to a temp file, so the
    ///     previous psarc survives) but lets the queued cheap companions finish, waiting at
    ///     most <see cref="ShutdownWait" />. A schedule after this builds no psarc.
    /// </summary>
    public void Stop()
    {
        Task? drain;
        lock (_gate)
        {
            _stopped = true;
            _cts?.Cancel();
            drain = _running ? RunningDrain : null;
        }

        try
        {
            drain?.Wait(ShutdownWait);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Project, "Waiting for the save companions at shutdown failed.", ex);
        }
    }

    /// <summary>
    ///     <b>The outer catch is load-bearing and this method had none.</b>
    ///     <para>
    ///         Each companion below carries its own <c>catch</c> so one cannot
    ///         take the other down — but the error path itself ends in
    ///         <c>Dispatcher.UIThread.InvokeAsync</c>, which throws once the
    ///         dispatcher is gone. That is reachable exactly when a psarc build
    ///         is cancelled at shutdown. Such an exception escaped the
    ///         <c>while</c>, escaped the un-awaited <c>Task.Run</c> in
    ///         <see cref="Schedule" /> completely unobserved, and —
    ///         worse — left the running flag latched <c>true</c>. Every
    ///         later save then hit the <c>if (_running) return;</c>
    ///         fast path, so the companions were
    ///         silently never written again for the life of the process, with
    ///         nothing in the log.
    ///     </para>
    ///     <para>
    ///         The flag is cleared on exactly one path per exit — the normal
    ///         <c>return</c> below clears it under the lock and never reaches the
    ///         catch. A <c>finally</c> would be wrong here: it would also fire
    ///         after that normal exit, and a save arriving in between would have
    ///         started a second drain whose flag we then cleared, letting a third
    ///         run concurrently on the same path.
    ///     </para>
    /// </summary>
    private async Task DrainAsync()
    {
        try
        {
            await DrainCoreAsync();
        }
        catch (Exception ex)
        {
            lock (_gate) { _running = false; }
            _statusBar?.CompleteTask(CompanionTaskId);
            _logger.Error(AppLogCategory.Project,
                "The detached save-companion drain faulted. The project itself saved fine; the companions " +
                "for that save were not (all) written. The drain has been unlatched, so the " +
                "next save will retry.", ex);
        }
    }

    private async Task DrainCoreAsync()
    {
        var progressShown = false;
        while (true)
        {
            CompanionWork work;
            CancellationToken token;
            bool stopped;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    break;
                }

                work = _pending[0];
                _pending.RemoveAt(0);
                stopped = _stopped;

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                token = _cts.Token;
            }

            // Once stopped the UI thread may be blocked waiting on us: never marshal to it.
            if (!stopped && !progressShown)
            {
                _statusBar?.BeginTask(CompanionTaskId, _localization["Status_WritingCompanions"], true);
                progressShown = true;
            }

            var failed = WriteDerivedArtifacts(work);
            if (failed.Count > 0 && !stopped)
            {
                await ReportAsync(() =>
                    _projectState.SetMessage(
                        _localization.Format("Status_CompanionsFailed", string.Join(", ", failed)),
                        StatusSeverity.Error), token);
            }

            if (!stopped && !token.IsCancellationRequested) await BuildPsarcAsync(work, token);
        }

        if (progressShown) _statusBar?.CompleteTask(CompanionTaskId);
    }

    private async Task BuildPsarcAsync(CompanionWork work, CancellationToken token)
    {
        try
        {
            var path = await _saveCompanions.BuildPsarcAsync(
                work.Path, work.Snapshot, work.AudioLengthMs, token);
            if (path is not null)
            {
                await ReportAsync(() =>
                    _projectState.SetMessage(
                        _localization.Format("Status_CompanionPsarcBuilt", Path.GetFileName(path)),
                        StatusSeverity.Success), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by shutting down. The previous psarc is untouched (the
            // build stages to a temp file), so there is nothing to report.
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project,
                $"Companion psarc build failed for '{work.Path}' (the project itself saved fine).", ex);
            await ReportAsync(() =>
                _projectState.SetMessage(
                    _localization.Format("Status_CompanionPsarcFailed", ex.Message),
                    StatusSeverity.Error), token);
        }
        finally
        {
            var targetLufs = _volumeTargetLufs();
            if (work.Adoption is { } adoption && adoption.SourceMatches(work.Snapshot, _logger, targetLufs))
            {
                await ReportAsync(() =>
                {
                    if (_volumeTargetLufs() == targetLufs) adoption.Apply(_projectState, work.Snapshot);
                }, token);
            }
        }
    }

    private async Task ReportAsync(Action report, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        try
        {
            // Stop may run on the UI thread. Cancelling the wait lets the worker
            // drain the remaining files without needing that thread to pump messages.
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) report();
            }).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Everything cheap a save derives once the project file is on disk, in order: the
    ///     legacy sibling XML cleanup, the Charter <c>.rscp</c>, the DTRS XML bundle, the
    ///     <c>.rs2dlc</c> that names the bundle's files (so it must follow the bundle), and the
    ///     <c>.eof</c>. Each step catches its own failure, logs it and is reported by name.
    /// </summary>
    private List<string> WriteDerivedArtifacts(CompanionWork work)
    {
        var failed = new List<string>();
        var (projectPath, snapshot, audioLengthMs) = (work.Path, work.Snapshot, work.AudioLengthMs);

        Step(failed, ".rscp", projectPath, () => _saveCompanions.WriteCharterProject(projectPath, snapshot));
        Step(failed, "DTRS XML", projectPath, () => _documents.SaveDlcBuilderXmlBundle(projectPath, snapshot, audioLengthMs));
        // AFTER the bundle: it writes INTO the folder that call swaps into place.
        Step(failed, ".rs2dlc", projectPath, () => _saveCompanions.WriteDlcBuilderProject(projectPath, snapshot, audioLengthMs));
        Step(failed, ".eof", projectPath, () => _saveCompanions.WriteEof(projectPath, snapshot));
        return failed;
    }

    private void Step(List<string> failed, string name, string projectPath, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            failed.Add(name);
            _logger.Error(AppLogCategory.Project,
                $"{name} companion failed for '{projectPath}' (the project itself saved fine).", ex);
        }
    }


    private readonly record struct CompanionWork(string Path, ChartProject Snapshot, double? AudioLengthMs, ProjectVolumeAdoption? Adoption);
}
