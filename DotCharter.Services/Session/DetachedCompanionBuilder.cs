using DotCharter.Services.Export;
using DotCharter.Services.State;
using DotCharter.Localization;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>
///     Builds the slow save companion — the <c>.psarc</c> — DETACHED from the
///     save that asked for it. The build is
///     slow (SNG conversion + copying the audio), so it runs on a worker and
///     coalesces: a save landing while one is in flight replaces the queued
///     snapshot instead of queueing a build.
/// </summary>
internal sealed class DetachedCompanionBuilder
{
    private readonly ISaveCompanionService _saveCompanions;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ProjectState _projectState;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    private readonly Lock _gate = new();
    private (string Path, ChartProject Snapshot, double? AudioLengthMs)? _pending;
    private bool _running;
    private bool _stopped;
    private CancellationTokenSource? _cts;

    public DetachedCompanionBuilder(
        ISaveCompanionService saveCompanions,
        IUiDispatcher uiDispatcher,
        ProjectState projectState,
        IAppLogger logger,
        ILocalizationService localization)
    {
        _saveCompanions = saveCompanions;
        _uiDispatcher = uiDispatcher;
        _projectState = projectState;
        _logger = logger;
        _localization = localization;
    }

    internal Task? RunningDrain { get; private set; }

    /// <summary>
    ///     Queues the DETACHED companion — the psarc build. Coalescing, not queueing: while one runs, further saves
    ///     replace the pending snapshot, so holding Ctrl+S does not spawn a backlog
    ///     — the last save wins and is built once.
    /// </summary>
    public void Schedule(string projectPath, ChartProject snapshot, double? audioLengthMs)
    {
        lock (_gate)
        {
            _pending = (projectPath, snapshot, audioLengthMs);
            if (_running) return; // the running drain will pick this up
            _running = true;
        }

        RunningDrain = Task.Run(DrainAsync);
    }

    /// <summary>
    ///     Abandons any in-flight build and anything queued. A build stages to a
    ///     temp file, so the previous psarc survives untouched. A schedule after
    ///     this builds nothing.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _pending = null;
            _cts?.Cancel();
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
    ///         fast path, so the <c>.psarc</c> was
    ///         silently never built again for the life of the process, with
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
            _logger.Error(AppLogCategory.Project,
                "The detached save-companion drain faulted. The project itself saved fine; the psarc " +
                "for that save was not written. The drain has been unlatched, so the " +
                "next save will retry.", ex);
        }
    }

    private async Task DrainCoreAsync()
    {
        while (true)
        {
            (string Path, ChartProject Snapshot, double? AudioLengthMs) work;
            CancellationToken token;
            lock (_gate)
            {
                if (_pending is null || _stopped)
                {
                    _running = false;
                    return;
                }

                work = _pending.Value;
                _pending = null;

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                token = _cts.Token;
            }

            try
            {
                var path = await _saveCompanions.BuildPsarcAsync(
                    work.Path, work.Snapshot, work.AudioLengthMs, token);
                if (path is not null)
                {
                    await _uiDispatcher.InvokeAsync(() =>
                        _projectState.SetMessage(
                            _localization.Format("Status_CompanionPsarcBuilt", Path.GetFileName(path)),
                            StatusSeverity.Success));
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer save, or shutting down. The previous psarc
                // is untouched (the build stages to a temp file), so there is
                // nothing to report and nothing to clean up.
            }
            catch (Exception ex)
            {
                _logger.Error(AppLogCategory.Project,
                    $"Companion psarc build failed for '{work.Path}' (the project itself saved fine).", ex);
                await _uiDispatcher.InvokeAsync(() =>
                    _projectState.SetMessage(
                        _localization.Format("Status_CompanionPsarcFailed", ex.Message),
                        StatusSeverity.Error));
            }
        }
    }
}
