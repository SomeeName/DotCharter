using System.Diagnostics;
using DotCharter.Localization;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing;
using DotCharter.Services.Export;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Services.State;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>
///     What a save needs from the session that owns it: the folder Save As
///     starts in, and the session-side bookkeeping a saved path triggers.
///     <see cref="ProjectSessionService" /> implements it explicitly.
/// </summary>
internal interface IProjectSaveHost
{
    /// <summary>Where the Save As picker starts for a session with no path yet.</summary>
    string? LastRscpDirectory { get; }

    /// <summary>A save landed at <paramref name="projectPath" />: remember it as the
    /// last project, record it in the MRU, then restart the auto-backup interval.</summary>
    void RecordSavedPath(string projectPath, string? songName);

    /// <summary>A Save As landed in <paramref name="directory" />; the next picker starts there.</summary>
    void RememberSaveAsDirectory(string directory);
}

/// <summary>
///     The project save pipeline: the re-entrancy guard, the Save As picker and
///     path move, and the write itself — assemble + deep clone on the UI thread,
///     then spacing enforcement, the atomic <c>.rscp</c> swap and the derived
///     companions on a worker, then the detached <c>.psarc</c>. The ORDER is the
///     feature; see <c>docs/agents/MAP.md</c> § Save / load / export pipeline.
/// </summary>
internal sealed class ProjectSaver
{
    private readonly IConfigurationService _configService;
    private readonly IProjectDocumentService _documentService;
    private readonly ISaveCompanionService _saveCompanions;
    private readonly ICrashRecoveryService _crashRecovery;
    private readonly IFileStorageService _storageService;
    private readonly ProjectState _projectState;
    private readonly IEditorTransport _transport;
    private readonly IChartEditorSurface _chartEditor;
    private readonly ProjectDirtyTracker _dirtyTracker;
    private readonly DetachedCompanionBuilder _detachedCompanions;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IMessagePrompt? _messageDialog;
    private readonly IStatusBarService? _statusBar;
    private readonly IProjectSaveHost _host;
    private readonly SongToneEditStore _songToneEdits;
    private readonly ICheckpointService? _checkpoints;
    private bool _saveInProgress;

    public ProjectSaver(
        IConfigurationService configService,
        IProjectDocumentService documentService,
        ISaveCompanionService saveCompanions,
        ICrashRecoveryService crashRecovery,
        IFileStorageService storageService,
        ProjectState projectState,
        IEditorTransport transport,
        IChartEditorSurface chartEditor,
        ProjectDirtyTracker dirtyTracker,
        DetachedCompanionBuilder detachedCompanions,
        IUiDispatcher uiDispatcher,
        IAppLogger logger,
        ILocalizationService localization,
        IMessagePrompt? messageDialog,
        IStatusBarService? statusBar,
        IProjectSaveHost host,
        SongToneEditStore songToneEdits,
        ICheckpointService? checkpoints = null)
    {
        _configService = configService;
        _documentService = documentService;
        _saveCompanions = saveCompanions;
        _crashRecovery = crashRecovery;
        _storageService = storageService;
        _projectState = projectState;
        _transport = transport;
        _chartEditor = chartEditor;
        _dirtyTracker = dirtyTracker;
        _detachedCompanions = detachedCompanions;
        _uiDispatcher = uiDispatcher;
        _logger = logger;
        _localization = localization;
        _messageDialog = messageDialog;
        _statusBar = statusBar;
        _host = host;
        _songToneEdits = songToneEdits;
        _checkpoints = checkpoints;
    }

    /// <summary>Stable id so holding Ctrl+S replaces the pill instead of
    /// stacking one per save.</summary>
    private const string SaveTaskId = "project-save";

    /// <summary>The Ctrl+S / Save As entry: hops to the UI thread, holds the
    /// <c>_saveInProgress</c> guard across the picker and the write, and routes a
    /// path-less session through the picker.</summary>
    public async Task<bool> SaveWithGuardAsync(bool saveAs)
    {
        // Assemble reads (and the enforcer mutates) the live project, so the
        // preparation phase must run on the UI thread no matter who calls us.
        if (!_uiDispatcher.CheckAccess())
            return await _uiDispatcher.InvokeAsync(() => SaveWithGuardAsync(saveAs));

        // The guard covers the picker phase too so a double Ctrl+S / repeated
        // Save As can't stack two pickers or two concurrent writes.
        if (_saveInProgress) return false;
        _saveInProgress = true;
        try
        {
            var projectPath = _projectState.CurrentProjectPath;
            if (saveAs || string.IsNullOrEmpty(projectPath))
            {
                // Real Save As / first save: the user picks a location — no
                // more silently writing a RELATIVE "project.rscp" into
                // whatever the process working directory happens to be.
                var picked = await PickSaveAsPathAsync();
                if (string.IsNullOrEmpty(picked))
                {
                    _logger.Info(AppLogCategory.Project, "Save As canceled by user.");
                    return false;
                }

                return await SaveToPickedPathAsync(picked);
            }

            return await SaveProjectCoreAsync(projectPath);
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    /// <summary>
    ///     The Save As tail: write FIRST, then move the session onto the new path.
    ///     <para>
    ///         <c>CurrentProjectPath</c> and <c>LastRscpDir</c> are not assigned
    ///         the moment the picker returns, before the write is attempted: a
    ///         Save As that then FAILED — no permission, disk full, the path taken
    ///         by something else — would still repoint the whole session at a file that
    ///         does not exist: the title bar would name it, the next plain Ctrl+S would write
    ///         there without asking, the recent list would learn it, and the project the
    ///         user actually had open would not be reachable from anywhere in the
    ///         app. The failure dialog would say the save failed; nothing would say the
    ///         session had moved.
    ///     </para>
    ///     <para>Internal for the test that pins the ordering — the public entry
    ///     point hops through <c>Dispatcher.UIThread.InvokeAsync</c>, which never
    ///     completes from a plain xunit thread.</para>
    /// </summary>
    internal async Task<bool> SaveToPickedPathAsync(string picked)
    {
        // BEFORE the write: the audio reference is resolved relative to the
        // project FOLDER, and Save As is the one operation that changes it.
        RebaseAudioReferenceForSaveAs(picked);
        var checkpointsFrom = CheckpointIdentity.For(_projectState.CurrentProjectPath, _projectState.CurrentSongIdentity);
        var chatsFrom = string.IsNullOrEmpty(_projectState.CurrentProjectPath) ? null : Path.GetDirectoryName(_projectState.CurrentProjectPath);

        if (!await SaveProjectCoreAsync(picked)) return false;

        _projectState.CurrentProjectPath = picked;
        await CarryCheckpointsAsync(checkpointsFrom, picked);
        await CarryAiChatsAsync(chatsFrom, picked);
        var dir = Path.GetDirectoryName(picked);
        // …through the host's config update, because a bare assignment to the
        // CACHED AppConfig instance arms no write. File ▸ Open persists its own
        // directory (SaveAppConfig, see OpenProject) and this one did not, so a
        // Save As into a new folder was forgotten on the next launch — and it is
        // worse than a plain omission here: SaveProjectCoreAsync has just
        // REPLACED the session's AppConfig with a fresh load, so the assignment landed on
        // an object nothing was going to write.
        if (!string.IsNullOrEmpty(dir)) _host.RememberSaveAsDirectory(dir);
        return true;
    }

    /// <summary>
    /// Save As keeps the project's checkpoints: they are keyed by its path, so without a
    ///     copy the new file opened with an empty Checkpoints list. Best-effort; the save has
    ///     already succeeded.
    /// </summary>
    private async Task CarryCheckpointsAsync(string from, string to)
    {
        if (_checkpoints is null) return;
        try
        {
            var copied = await Task.Run(() => _checkpoints.CopyTo(from, to));
            if (copied > 0)
                _logger.Info(AppLogCategory.Project, $"Save As carried {copied} checkpoint(s) to the new project path.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(AppLogCategory.Project, $"Save As could not carry the checkpoints over: {ex.Message}");
        }
    }

    /// <summary>
    /// Save As keeps the chart's AI chats: they live beside the project, so a Save As into
    ///     another folder opened with an empty chat history. COPIED, so the old project keeps its own;
    ///     best-effort like the checkpoints, and a chat that fails to copy stays where it was.
    /// </summary>
    private async Task CarryAiChatsAsync(string? fromFolder, string picked)
    {
        var chartId = _projectState.ActiveProject?.ProjectId;
        try
        {
            var result = await Task.Run(() => AiChatSaveAsCarry.Carry(fromFolder, Path.GetDirectoryName(picked), chartId));
            if (result.Copied > 0 || result.Skipped > 0)
            {
                _logger.Info(AppLogCategory.Project,
                    $"Save As carried {result.Copied} AI chat(s) to the new project folder; {result.Skipped} already there were left as they are.");
            }

            foreach (var failure in result.Failures)
                _logger.Warning(AppLogCategory.Project, $"Save As could not carry an AI chat over; it stays in the old folder. {failure}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.Warning(AppLogCategory.Project, $"Save As could not carry the AI chats over: {ex.Message}");
        }
    }

    /// <summary>
    ///     Save As into a DIFFERENT folder: promote a relative audio reference
    ///     to the absolute file it currently resolves to, so the copy that lands
    ///     in the new folder still knows where its song is.
    ///     <para>
    ///         <c>musicFileName</c> is a bare file name for every normal project
    ///         and is resolved against the project's own folder (and its
    ///         parents) by <see cref="ProjectAudioLocator" />. Save As wrote the
    ///         new <c>.rscp</c> carrying that same bare name — and the audio was
    ///         still back in the OLD folder, which the new one is not under. The
    ///         result opened with no audio at all: no waveform, no playback, no
    ///         stems, no message naming the cause. The chart survived; everything
    ///         that makes it playable did not.
    ///     </para>
    ///     <para>
    ///         Only when it would otherwise BREAK. Saving next to the audio (the
    ///         common "Save As a variant name" case) resolves from the new path
    ///         too, and keeps its portable relative name. An audio reference we
    ///         cannot find from the OLD path either is left exactly as it is —
    ///         there is nothing to promote it to, and inventing a path would only
    ///         bake in a lie.
    ///     </para>
    /// </summary>
    internal void RebaseAudioReferenceForSaveAs(string picked)
    {
        var audio = _projectState.CurrentAudioFileName;
        if (string.IsNullOrWhiteSpace(audio) || Path.IsPathRooted(audio)) return;

        var current = _projectState.CurrentProjectPath;
        if (string.IsNullOrEmpty(current)) return; // first save: nothing to rebase FROM

        var resolved = ProjectAudioLocator.TryResolve(current, audio);
        if (resolved == null) return; // already unresolvable; do not invent one
        if (ProjectAudioLocator.TryResolve(picked, audio) != null) return; // still reachable from the new home

        _projectState.CurrentAudioFileName = resolved;
        _logger.Info(AppLogCategory.Project,
            "Save As moved the project out of its audio's folder; the audio reference was made absolute " +
            "so the saved project can still find it.");
    }

    private async Task<string?> PickSaveAsPathAsync()
    {
        var current = _projectState.CurrentProjectPath;
        var suggested = !string.IsNullOrEmpty(current)
            ? Path.GetFileName(current)
            : SafeFileName.Make(_projectState.ActiveProject?.Chart?.SongName ?? "") is { Length: > 0 } song
                ? song + ".rscp"
                : "project.rscp";
        var startDir = !string.IsNullOrEmpty(current) ? Path.GetDirectoryName(current) : _host.LastRscpDirectory;

        var path = await _storageService.SaveFilePickerAsync(
            L("Picker_SaveProjectAs"), suggested,
            new[] { new FilePickerFilter(L("Picker_ProjectFiles"), new[] { "*.rscp" }) },
            startDir);
        if (string.IsNullOrEmpty(path)) return null;
        if (!path.EndsWith(".rscp", StringComparison.OrdinalIgnoreCase)) path += ".rscp";
        return path;
    }

    /// <summary>
    ///     Where each phase of a save ran and how long it took. Raised once per
    ///     successful <see cref="SaveProjectCoreAsync" />, after the write. A test
    ///     seam AND the source of the timing line in the log: the structural
    ///     guarantee is that <see cref="EnforceThreadId" /> is a worker, not
    ///     <see cref="SnapshotThreadId" /> — the UI thread does one clone and
    ///     nothing else heavy (<c>ProjectSessionSaveThreadingTests</c>).
    /// </summary>
    internal readonly record struct ProjectSaveTiming(
        int SnapshotThreadId, int EnforceThreadId,
        double UiThreadMs, double EnforceMs, double WriteMs);

    /// <inheritdoc cref="ProjectSaveTiming" />
    internal event Action<ProjectSaveTiming>? SaveTimed;

    /// <summary>
    ///     The save itself, minus the picker and the <c>_saveInProgress</c> guard.
    ///     <c>internal</c> only so tests can drive it: the public entry point
    ///     marshals through <c>Dispatcher.UIThread.InvokeAsync</c>, which never
    ///     completes in a test host with no dispatcher loop.
    /// </summary>
    internal async Task<bool> SaveProjectCoreAsync(string projectPath)
    {
        // Ctrl+S has a UI-thread stall — the assemble and the deep clone run
        // here, before the hand-off to the worker (the enforcer is on the worker, see below). The pill is begun BEFORE that
        // stall so it describes the whole of it.
        _statusBar?.BeginTask(SaveTaskId, L("Status_SavingProject"));
        try
        {
            _logger.Info(AppLogCategory.Project, $"Saving project to '{projectPath}'.");

            // …and this is what makes the pill VISIBLE for it. See the method.
            await DrainToFirstPaintAsync();

            var uiStopwatch = Stopwatch.StartNew();
            var snapshotThreadId = Environment.CurrentManagedThreadId;
            var config = _configService.LoadAppConfig();
            var project = _documentService.AssembleProject(_projectState.ActiveProject, _transport.Bpm,
                _projectState.CurrentAudioFileName, _chartEditor.LyricPhrases);
            _documentService.RefreshArrangementFileManifest(project);

            // Deep-snapshot on the UI thread so the worker below serializes
            // an immutable copy — an edit landing during the write can no
            // longer tear the saved file. The .rscp additionally goes to a
            // temp file and is swapped in atomically, so even a failed save
            // never corrupts the existing project on disk.
            //
            // The clone is the ONLY heavy step left on the UI thread. The
            // spacing/compatibility pass (ChartSpacingEnforcer.EnforceProject)
            // must not run HERE, over the live project, before the clone — on a
            // big project that would be hundreds of milliseconds between Ctrl+S and
            // the next frame, for a pass whose only consumer is the file. It
            // runs on the worker over the snapshot: the file it produces is
            // identical, the live project is left alone (it is re-enforced on
            // load and after every committed edit anyway), and the UI-thread
            // cost of a save is one clone. SaveTimed pins that shape.
            var snapshot = ChartProjectCloner.Clone(project);
            // The tone renames this snapshot carries, read with it: the player's tone
            // edits and instruments live OUTSIDE the project, keyed by the saved file's keys.
            var toneEditFollow = SongToneEditKeyFollow.Capture(_projectState.ActiveProject,
                _projectState.CurrentProjectPath, projectPath, snapshot);
            var uiMs = uiStopwatch.Elapsed.TotalMilliseconds;

            // Read on the UI thread with the snapshot, for the same reason the
            // snapshot exists: ProjectState is UI-thread state and the worker
            // below must not observe it mid-change. ChartProject carries no
            // duration, so this is the only place the save path can learn it —
            // the exported songLength is min(chart, audio) - 1 ms, and a chart
            // longer than its audio must clamp to the audio or it never reaches
            // an end in-game. 0 means "no audio decoded yet" => chart length.
            var audioLengthMs = _projectState.TotalAudioLengthMs > 0
                ? _projectState.TotalAudioLengthMs
                : (double?)null;

            double enforceMs = 0, writeMs = 0;
            var enforceThreadId = -1;
            var toneEditsFollowed = false;
            await Task.Run(() =>
            {
                // Over the SNAPSHOT, on the worker — see the note above the clone.
                var workerStopwatch = Stopwatch.StartNew();
                enforceThreadId = Environment.CurrentManagedThreadId;
                ChartSpacingEnforcer.EnforceProject(snapshot, config);
                enforceMs = workerStopwatch.Elapsed.TotalMilliseconds;

                workerStopwatch.Restart();
                var tempPath = projectPath + ".saving";
                try
                {
                    _documentService.SaveChart(tempPath, snapshot, audioLengthMs);
                    // The bounded retry matters most HERE: a transient AV/indexer
                    // hold on the user's own project file otherwise reports "save
                    // failed" and orphans the .saving file beside it.
                    AtomicFile.ReplaceWithRetry(tempPath, projectPath);
                }
                catch
                {
                    // A serialize that threw part-way leaves a HALF-WRITTEN
                    // stage next to the project — a file the user reads as a
                    // newer save and nothing ever opens. ReplaceWithRetry sweeps
                    // its own; this covers the write that never reached it.
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.Warning(AppLogCategory.Project,
                            $"Could not remove the staged save '{tempPath}' after a failed write.", cleanupEx);
                    }

                    throw;
                }
                writeMs = workerStopwatch.Elapsed.TotalMilliseconds;

                // Everything below is DERIVED and the .rscp is already safely on
                // disk, so a failure here must NOT be reported as "the project
                // failed to save" — that is what the outer catch would otherwise
                // do, and it would also leave the project dirty and skip
                // NotifyProjectSaved for a save that actually succeeded.
                try
                {
                    // This sat ABOVE the comment above, unguarded: a read-only,
                    // locked or AV-held sibling .xml made File.Delete throw into
                    // the outer catch, so a fully successful save reported
                    // Status_SaveError, stayed dirty, never cleared the crash
                    // snapshot and never scheduled the psarc. It is cleanup of a
                    // legacy artifact, not part of the save.
                    DeleteLegacyProjectXmlIfPresent(projectPath);
                }
                catch (Exception ex)
                {
                    _logger.Error(AppLogCategory.Project,
                        $"Could not delete the legacy sibling XML export beside '{projectPath}' " +
                        "(the project itself saved fine).", ex);
                }

                try
                {
                    _documentService.SaveDlcBuilderXmlBundle(projectPath, snapshot, audioLengthMs);
                }
                catch (Exception ex)
                {
                    _logger.Error(AppLogCategory.Project,
                        $"DTRS XML bundle failed for '{projectPath}' (the project itself saved fine).", ex);
                }

                try
                {
                    // AFTER the bundle above: it writes INTO the folder that call
                    // swaps into place, and it names the files that call wrote.
                    _saveCompanions.WriteDlcBuilderProject(projectPath, snapshot, audioLengthMs);
                }
                catch (Exception ex)
                {
                    _logger.Error(AppLogCategory.Project,
                        $".rs2dlc companion failed for '{projectPath}' (the project itself saved fine).", ex);
                }

                try
                {
                    _saveCompanions.WriteEof(projectPath, snapshot);
                }
                catch (Exception ex)
                {
                    _logger.Error(AppLogCategory.Project,
                        $"EOF companion failed for '{projectPath}' (the project itself saved fine).", ex);
                }

                try
                {
                    // AFTER the .rscp swap: the store follows the keys the file now HAS.
                    toneEditsFollowed = toneEditFollow.Apply(_songToneEdits, _logger);
                }
                catch (Exception ex)
                {
                    _logger.Error(AppLogCategory.Project,
                        $"Song tone edits did not follow the renamed tone keys for '{projectPath}' (the project itself saved fine).", ex);
                }
            });

            if (toneEditsFollowed) toneEditFollow.Settle();

            // A save(-as) is the moment a NEW project gains a path: the session
            // records it as the last project, feeds the MRU exactly like an open
            // does and restarts the auto-backup interval, in that order.
            _host.RecordSavedPath(projectPath, snapshot.Chart?.SongName);
            // The saved file now contains everything the crash snapshot did —
            // clear it so a later hard kill isn't misread as lost work.
            _crashRecovery.NotifyProjectSaved(projectPath);
            var xmlDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))
                                            ?? Directory.GetCurrentDirectory(), "DTRS XML");
            _logger.Info(AppLogCategory.Project,
                $"Project saved. Rscp='{projectPath}', DtrsXml='{xmlDirectory}', arrangements={project.Arrangements.Count}, vocals={project.VocalTracks.Count}, showlights={project.Showlights.Count}, beats={project.Beats.Count}, bpm={project.Bpm:0.###}, " +
                $"uiThreadMs={uiMs:0.#} (assemble+clone), workerEnforceMs={enforceMs:0.#}, workerWriteMs={writeMs:0.#}.");
            SaveTimed?.Invoke(new ProjectSaveTiming(snapshotThreadId, enforceThreadId, uiMs, enforceMs, writeMs));
            _projectState.SetMessage(L("Status_ProjectSavedTo", projectPath), StatusSeverity.Success);
            _dirtyTracker.MarkClean();

            // Detached on purpose: building a psarc converts every arrangement to
            // SNG and copies the whole audio track. Awaiting it here would put
            // seconds of latency on Ctrl+S.
            _detachedCompanions.Schedule(projectPath, snapshot, audioLengthMs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, $"Project save failed for '{projectPath}'.", ex);
            // Save As .xml of play-only content reads as the guard's own message.
            _projectState.SetMessage(L("Status_SaveError",
                ex is PlayOnlyContentException ? L(PlayOnlyExportGuard.MessageKey) : ex.Message), StatusSeverity.Error);
            // A failed save leaves the session DIRTY and the user believing
            // their work is on disk — the one outcome that must not be a faint
            // line in the status rail. Reuses the app-wide error dialog (already
            // this service's dependency for the replace-audio prompt); no new
            // affordance is invented. Deliberately NOT awaited: the save has
            // already failed and there is nothing to sequence after the
            // acknowledgement, and awaiting would hang any host without a
            // dispatcher loop.
            _ = _messageDialog?.ShowAsync(
                L("Dialog_SaveFailed_Title"),
                L("Dialog_SaveFailed_Message", projectPath),
                MessageDialogKind.Error,
                ex.ToString());
            return false;
        }
        finally
        {
            _statusBar?.CompleteTask(SaveTaskId);
        }
    }

    /// <summary>
    ///     Lets the dispatcher run a full frame before returning.
    ///     <para>
    ///         <b>Measured, not assumed:</b> without this the pill EXISTS but
    ///         never paints during the stall it exists to explain.
    ///         <c>StatusBarService.BeginTask</c> inserts inline when called on the
    ///         UI thread, but <c>StatusBarViewModel</c> only picks the change up
    ///         through a Background-priority post — and
    ///         <see cref="SaveProjectCoreAsync" /> then runs enforce + assemble +
    ///         clone straight through with no yield, so the dispatcher's first
    ///         chance to do anything is the <c>await</c> on the worker, by which
    ///         time the stall is over.
    ///     </para>
    ///     <para>
    ///         <c>IUiDispatcher.Post</c> is Background priority, which in Avalonia
    ///         sits BELOW Render — so a continuation resumed from here runs after
    ///         the frame that drew the pill. A plain <c>Task.Yield()</c> would
    ///         not do: its continuation goes to the dispatcher at Normal, ABOVE
    ///         Render, and would resume before any frame.
    ///     </para>
    ///     <para>
    ///         This changes nothing about the save PIPELINE: enforce still
    ///         precedes assemble still precedes the clone, all on the UI thread.
    ///         It only moves the whole sequence one dispatcher turn later, and the
    ///         <c>_saveInProgress</c> guard already covers that window. A test
    ///         host whose dispatcher runs posts inline drains at once.
    ///     </para>
    /// </summary>
    private Task DrainToFirstPaintAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiDispatcher.Post(() => drained.TrySetResult());
        return drained.Task;
    }

    private void DeleteLegacyProjectXmlIfPresent(string projectPath)
    {
        if (!string.Equals(Path.GetExtension(projectPath), ".rscp", StringComparison.OrdinalIgnoreCase)) return;

        var legacyXmlPath = Path.ChangeExtension(projectPath, ".xml");
        if (string.Equals(legacyXmlPath, projectPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacyXmlPath))
            return;

        File.Delete(legacyXmlPath);
        _logger.Info(AppLogCategory.Project,
            $"Deleted legacy sibling XML export '{legacyXmlPath}' after moving exports to DTRS XML.");
    }

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);
}
