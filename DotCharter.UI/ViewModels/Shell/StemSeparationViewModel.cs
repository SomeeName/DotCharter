// Stem separation (htdemucs): the never-regenerate cache-first run, the
// keep/replace backup dance around an existing stem set, and registering the
// finished stems on the project and in the durable cache. Also the app's
// ISongStemSeparation, so a screen (chart from audio) drives THIS run.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Session;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Library;

namespace DotCharter.UI.ViewModels.Shell;

public sealed partial class StemSeparationViewModel : ViewModelBase, ISongStemSeparation
{
    private readonly ProjectState _projectState;
    private readonly IProjectSessionService _projectSession;
    private readonly StemMixerViewModel _stemMixer;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IStemSeparationService? _stemSeparation;
    private readonly IStatusBarService? _statusBarService;
    private readonly StemCacheStore? _stemCache;
    private readonly StemCacheCoordinator? _stemCacheCoordinator;
    private readonly IStemDefaultModelProvisioner? _defaultModels;

    public StemSeparationViewModel(
        ProjectState projectState,
        IProjectSessionService projectSession,
        StemMixerViewModel stemMixer,
        IAppDialogService dialogs,
        IUiDispatcher ui,
        IAppLogger logger,
        ILocalizationService localization,
        IStemSeparationService? stemSeparation = null,
        IStatusBarService? statusBarService = null,
        StemCacheStore? stemCache = null,
        StemCacheCoordinator? stemCacheCoordinator = null,
        IStemDefaultModelProvisioner? defaultModels = null)
    {
        _projectState = projectState;
        _projectSession = projectSession;
        _stemMixer = stemMixer;
        _dialogs = dialogs;
        _ui = ui;
        _logger = logger;
        _localization = localization;
        _stemSeparation = stemSeparation;
        _statusBarService = statusBarService;
        _stemCache = stemCache;
        _stemCacheCoordinator = stemCacheCoordinator;
        _defaultModels = defaultModels;

        // Tools ▸ Separate Stems over a song that already HAS stems overwrites
        // them, and "stems are never regenerated" is a hard rule — so the
        // re-separate stays explicit. A menu
        // verb takes the app-wide prompt, and the message NAMES what is lost.
        ConfirmStemReplaceAsync = (plan, backend) => _dialogs.Message.ShowAsync(
            _localization["Dialog_StemRegenerate_Title"],
            BuildStemReplaceMessage(plan, _localization),
            MessageDialogKind.Warning,
            details: _localization.Format("Dialog_StemRegenerate_ModelFmt", backend),
            confirmation: true);

        // A user who never chose a model is asked ONCE, naming the model and its size,
        // before the first separation downloads it. Never silently. A user who already
        // separates with a default is asked once about the better one, and keeps theirs
        // on a no.
        ConfirmDefaultDownloadAsync = offer => _dialogs.Message.ShowAsync(
            _localization[offer.IsUpgrade ? "Dialog_StemsDefaultUpgrade_Title" : "Dialog_StemsDefaultDownload_Title"],
            BuildDefaultDownloadMessage(offer, _localization),
            MessageDialogKind.Info,
            confirmation: true);
    }

    /// <summary>Raised on the UI thread once a stem set is on the project and the
    /// mixer rebuilt, so the shell can show the mixer.</summary>
    public event Action? StemsApplied;

    /// <summary>
    ///     Folds what this separation really cost into the machine's stored rate,
    ///     which is the ONLY thing the pre-play screen's "~N s" estimate is
    ///     derived from. A run that says nothing usable (too short, absurdly
    ///     slow) is discarded rather than averaged in — see
    ///     <see cref="StemSeparationCost" />.
    /// </summary>
    private void RecordStemSeparationCost(double elapsedSeconds)
    {
        var audioSeconds = _projectState.TotalAudioLengthMs / 1000.0;
        var measured = StemSeparationCost.RateFrom(elapsedSeconds, audioSeconds);
        if (measured is not { } rate)
        {
            _logger.Info(AppLogCategory.Audio,
                $"Stem separation cost not recorded (elapsed={elapsedSeconds:0.0}s, audio={audioSeconds:0.0}s).");
            return;
        }

        var blended = StemSeparationCost.Blend(_projectSession.AppConfig.StemSeparationSecondsPerAudioSecond, rate);
        _projectSession.AppConfig.StemSeparationSecondsPerAudioSecond = blended;
        _projectSession.SaveAppConfig();
        _logger.Info(AppLogCategory.Audio,
            $"Stem separation cost: {rate:0.00}x realtime this run, {blended:0.00}x stored.");
    }

    /// <summary>0 = idle, 1 = a separation is in flight. Guards ALL entry
    /// points (menu AsyncRelayCommand, Play Mode event, future callers) —
    /// the command's no-concurrency default only protects its own path, and
    /// two concurrent runs mean double ONNX inference for minutes plus a
    /// shared status pill whose cancel hook the second run steals.</summary>
    private int _stemSeparationInFlight;

    /// <summary>The in-flight run once it knows its audio; null otherwise.</summary>
    private SongStemSeparationRun? _running;

    /// <summary>Tools ▸ Separate Stems: the run below, its outcome reported through the log and the status bar.</summary>
    [RelayCommand]
    public Task SeparateStemsAsync() => SeparateAsync(null, CancellationToken.None);

    /// <inheritdoc />
    public SongStemSeparationRun? Running => Volatile.Read(ref _running);

    /// <inheritdoc />
    public async Task<SongStemSeparationOutcome> SeparateAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _stemSeparationInFlight, 1, 0) != 0)
        {
            _logger.Info(AppLogCategory.Audio,
                "Stem separation already running; ignoring the duplicate request.");
            return Outcome(SongStemSeparationStatus.AlreadyRunning, _localization["StemSeparation_AlreadyRunning"]);
        }

        // The run belongs to the app, not to the screen that asked for it: a screen
        // closed mid-run leaves it going, and one opened mid-run follows `Running`.
        SongStemSeparationRun? run = null;
        var outcome = Outcome(SongStemSeparationStatus.Failed);
        try
        {
            outcome = await SeparateStemsCoreAsync(progress, audio =>
            {
                run = new SongStemSeparationRun(audio, ct);
                Volatile.Write(ref _running, run);
                return run;
            }).ConfigureAwait(false);
            return outcome;
        }
        catch (Exception ex)
        {
            outcome = Outcome(ex is OperationCanceledException ? SongStemSeparationStatus.Cancelled : SongStemSeparationStatus.Failed,
                ex.Message);
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, null);
            Interlocked.Exchange(ref _stemSeparationInFlight, 0);
            run?.Complete(outcome);
        }
    }

    /// <inheritdoc />
    public async Task<SongStemSeparationAvailability> GetAvailabilityAsync(string audioPath, CancellationToken ct = default)
    {
        if (_stemSeparation == null) return SongStemSeparationAvailability.Blocked(_localization["StemSeparation_NotInstalled"]);
        if (Volatile.Read(ref _stemSeparationInFlight) != 0)
            return SongStemSeparationAvailability.Blocked(_localization["StemSeparation_AlreadyRunning"]);

        var projectPath = _projectState.CurrentProjectPath;
        var audioFile = _projectState.CurrentAudioFileName;
        if (_projectState.ActiveProject == null || string.IsNullOrWhiteSpace(audioFile))
            return SongStemSeparationAvailability.Blocked(_localization["StemSeparation_NoSongAudio"]);

        // The run separates the SONG's audio, so a stem helps only the file it was
        // separated from. Resolving probes the disk: off the UI thread.
        var songAudio = await Task.Run(() => ProjectAudioLocator.TryResolve(projectPath, audioFile), ct).ConfigureAwait(false);
        if (songAudio == null) return SongStemSeparationAvailability.Blocked(_localization["StemSeparation_NoSongAudio"]);
        if (!SongStemSeparationRun.IsSameFile(songAudio, audioPath))
            return SongStemSeparationAvailability.Blocked(_localization["StemSeparation_NotSongAudio"]);

        // An offered default download is a live button: the run asks before it downloads.
        if (_defaultModels != null && await _defaultModels.GetOfferAsync(ct).ConfigureAwait(false) != null)
            return SongStemSeparationAvailability.Ready;
        return await _stemSeparation.IsAvailableAsync(ct).ConfigureAwait(false)
            ? SongStemSeparationAvailability.Ready
            : SongStemSeparationAvailability.Blocked(_localization["Dialog_StemsNoModel_Message"]);
    }

    private static SongStemSeparationOutcome Outcome(SongStemSeparationStatus status, string reason = "") =>
        new(status, reason);

    /// <param name="externalProgress">The caller's progress, beside the status bar's.</param>
    /// <param name="begin">Publishes the run once its audio is known; its token is the caller's cancel.</param>
    private async Task<SongStemSeparationOutcome> SeparateStemsCoreAsync(IProgress<double>? externalProgress,
        Func<string, SongStemSeparationRun> begin)
    {
        if (_stemSeparation == null)
        {
            _logger.Warning(AppLogCategory.Audio, "Stem separation service is not registered.");
            return Outcome(SongStemSeparationStatus.Unavailable, _localization["StemSeparation_NotInstalled"]);
        }

        var project = _projectState.ActiveProject;
        var projectPath = _projectState.CurrentProjectPath;
        var audioFile = _projectState.CurrentAudioFileName;
        if (project == null || string.IsNullOrWhiteSpace(audioFile))
        {
            _logger.Warning(AppLogCategory.Audio, "Stem separation requires an open project with audio.");
            return Outcome(SongStemSeparationStatus.Unavailable, _localization["StemSeparation_NoSongAudio"]);
        }

        // PSARC-loaded sessions (and any flow that loads audio without a saved
        // project on disk) store the absolute audio path in CurrentAudioFileName,
        // and the stems output then belongs next to the audio rather than next
        // to a project file that does not exist. Probing ONLY the
        // project folder for a relative name would fail a chart in a per-arrangement
        // subfolder — which the project loader resolves by walking up — and
        // separation would end with no usable message.
        var inputAudio = ProjectAudioLocator.TryResolve(projectPath, audioFile);
        var projectDir = ProjectAudioLocator.ResolveOutputDirectory(projectPath, audioFile);
        if (inputAudio == null || string.IsNullOrEmpty(projectDir))
        {
            _logger.Warning(AppLogCategory.Audio,
                $"Stem separation could not locate the audio '{audioFile}' for project '{projectPath}'.");
            return Outcome(SongStemSeparationStatus.Unavailable, _localization["StemSeparation_NoSongAudio"]);
        }
        var outputDir = Path.Combine(projectDir, "stems");
        var run = begin(inputAudio);

        // Never-regenerate fast path: if this exact song + audio was separated
        // before, expand the cached archive and load it — no separation runs,
        // and no active model is even required. A genuinely corrupt archive is
        // deleted and falls through to a fresh run; an environmental decode
        // failure (e.g. Opus cache but ffmpeg went missing) also falls through
        // but KEEPS the archive for when the tool returns.
        //
        // The escape hatch (re-run to try another model) is gated on stems being
        // loaded in THIS session, not on WAVs existing on disk: a PSARC song's
        // output dir is %LocalAppData%/…/PsarcAudioCache/<song>/stems, which
        // survives a restart. Gating on the files would mean the cache is never
        // consulted after a relaunch — the exact "don't re-run the long
        // separation every launch" complaint this cache exists to fix.
        if (_stemCacheCoordinator != null && !_stemMixer.HasStems)
        {
            var cached = await _stemCacheCoordinator
                .TryLoadCachedAsync(BuildStemCacheIdentity(inputAudio), inputAudio, outputDir)
                .ConfigureAwait(false);
            if (cached is { Count: > 0 })
            {
                await RegisterAndPersistStemsAsync(project, cached, projectDir, projectPath, cached.Count);
                return Outcome(SongStemSeparationStatus.LoadedFromCache);
            }
        }

        if (_defaultModels != null && await _defaultModels.GetOfferAsync().ConfigureAwait(false) is { } offer)
        {
            var agreed = await _ui.InvokeAsync(() => ConfirmDefaultDownloadPromptAsync(offer, ConfirmDefaultDownloadAsync));
            if (!agreed)
            {
                _logger.Info(AppLogCategory.Audio, $"Default stem model download declined ('{offer.ModelId}').");
                // An upgrade declined is remembered and the installed default separates; a
                // first download declined leaves nothing to separate with.
                if (!offer.IsUpgrade) return Outcome(SongStemSeparationStatus.Declined);
                _defaultModels.DeclineUpgrade(offer);
            }
            else if (await DownloadDefaultModelAsync(offer, run.Token).ConfigureAwait(false) is { } stopped)
            {
                return stopped;
            }
        }

        if (!await _stemSeparation.IsAvailableAsync().ConfigureAwait(false))
        {
            // A real dialog (plus the status-bar red counter via the Error
            // log) — this was the "menu item silently does nothing"
            // first-run trap.
            var why = await _stemSeparation.DescribeBackendAsync().ConfigureAwait(false);
            _logger.Error(AppLogCategory.Audio,
                "Stem separation needs a model: download and activate one under " +
                $"Settings → Stem Models, then run Separate Stems again. Backend status: {why}");
            await _dialogs.Message.ShowAsync(
                _localization["Dialog_StemsNoModel_Title"],
                _localization["Dialog_StemsNoModel_Message"],
                MessageDialogKind.Warning,
                details: why);
            return Outcome(SongStemSeparationStatus.Unavailable, _localization["Dialog_StemsNoModel_Message"]);
        }

        // Pre-flight: if stems already exist on disk, the run OVERWRITES them,
        // and "stems are never regenerated" is a hard rule — so the re-separate
        // has to be explicit. A destructive verb reached from a MENU takes the
        // app-wide prompt instead, which is the rule in docs/agents/ui/dialogs.md.
        // Stems the model cannot produce are still moved aside and restored
        // afterwards, so a re-run never silently DROPS one — the earlier
        // dialog's own default for those rows, and the half worth keeping.
        var folder = StemFolderBackup.Open(outputDir, _logger);

        // We cannot reliably ask the model what it will emit ahead of time;
        // fall back to the standard 4-stem set plus whatever already exists.
        // Post-separation reconciliation uses the *actual* result names.
        var predictedNew = new[] { "drums", "bass", "other", "vocals" };

        var keepFromExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folder.Existing.Count > 0)
        {
            var plan = StemReplacePlan.For(folder.Existing.Keys, predictedNew);
            var backendDescription = await _stemSeparation.DescribeBackendAsync().ConfigureAwait(false);
            var keep = await _ui.InvokeAsync(() =>
                ConfirmStemReplacementAsync(plan, backendDescription, ConfirmStemReplaceAsync));
            if (keep == null)
            {
                _logger.Info(AppLogCategory.Audio, "Stem re-separation declined — nothing was overwritten.");
                return Outcome(SongStemSeparationStatus.Declined);
            }

            foreach (var n in keep) keepFromExisting.Add(n);
        }

        // Stage "keep" stems to a sibling folder so the separator can't clobber them.
        folder.MoveAside(keepFromExisting);

        _logger.Info(AppLogCategory.Audio, $"Stem separation requested. Input='{inputAudio}', output='{outputDir}', keep={keepFromExisting.Count}.");

        // The VM owns the status pill so it can attach the cancel hook —
        // the ONNX backend honors the token; the native demucs.cpp loop is
        // uncancellable once inside the native call (documented limitation).
        // A caller's own token (a screen's Cancel) and the published run's
        // Cancel (a screen that attached later) cancel the same run.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        const string taskId = "stem-separation";
        var fileName = Path.GetFileName(inputAudio);
        _statusBarService?.BeginTask(taskId, _localization.Format("Progress_SeparatingStemsOf", fileName),
            indeterminate: false, cancel: () => cts.Cancel());
        // Straight to the status bar (thread-safe; it posts to the UI itself). Once the
        // backend reports chunks, the label says "chunk i of N" so a slow run visibly moves.
        var progress = new StemSeparationToastProgress(
            (v, label) =>
            {
                _statusBarService?.ReportProgress(taskId, v, label);
                externalProgress?.Report(v);
                run.Report(v);
            },
            (chunk, total) => _localization.Format("Progress_SeparatingStemsChunk", fileName, chunk, total));

        // Separation runs for MINUTES. If the user opens another song meanwhile,
        // everything below this point is work for a project that is not
        // open — cancel the run instead of burning inference on it (the apply
        // site in RegisterAndPersistStemsAsync refuses a stale project anyway).
        void OnActiveProjectChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(ProjectState.ActiveProject), StringComparison.Ordinal)) return;
            if (ReferenceEquals(_projectState.ActiveProject, project)) return;
            _logger.Warning(AppLogCategory.Audio,
                "Song switched while stems were separating — cancelling the run.");
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* run already finished */ }
        }

        _projectState.PropertyChanged += OnActiveProjectChanged;

        StemSeparationResult result;
        // What separation really costs on THIS machine. It is the only source
        // the pre-play screen's estimate has — there is no honest constant, so
        // an unmeasured machine shows no estimate at all.
        var costClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await _stemSeparation.SeparateAsync(inputAudio, outputDir, progress, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(AppLogCategory.Audio, "Stem separation cancelled.");
            folder.Restore();
            return Outcome(SongStemSeparationStatus.Cancelled);
        }
        finally
        {
            _projectState.PropertyChanged -= OnActiveProjectChanged;
            _statusBarService?.CompleteTask(taskId);
        }

        if (!result.Success)
        {
            _logger.Error(AppLogCategory.Audio, $"Stem separation failed: {result.ErrorMessage}");
            // Restore backups so we don't lose the user's previous stems on failure.
            folder.Restore();
            return Outcome(SongStemSeparationStatus.Failed, result.ErrorMessage ?? "");
        }

        RecordStemSeparationCost(costClock.Elapsed.TotalSeconds);

        var finalStems = folder.ApplyKept(result.Stems);

        // Persist the freshly separated stems into the durable cache so this
        // song never has to be separated again (even across app restarts).
        // The cache lives outside the psarc/project under
        // %LocalAppData%/DotCharter/StemCache, keyed by song identity + audio
        // content hash. Best-effort: a cache write failure must not lose the
        // user's just-computed stems.
        await SaveStemsToCacheAsync(inputAudio, finalStems);

        await RegisterAndPersistStemsAsync(project, finalStems, projectDir, projectPath, keepFromExisting.Count);
        return Outcome(SongStemSeparationStatus.Separated);
    }

    /// <summary>Cache identity for the current session's source audio: a library
    /// song is keyed on its <c>lib:SongKey</c> identity, an own chart on its
    /// audio path — both folded with the audio content signature.</summary>
    private StemCacheIdentity BuildStemCacheIdentity(string inputAudio)
    {
        var songId = _projectState.CurrentSongIdentity;
        return LibrarySongIdentity.IsLibrary(songId)
            ? StemCacheIdentity.ForLibrary(songId, inputAudio)
            : StemCacheIdentity.ForAudioFile(inputAudio);
    }

    private async Task SaveStemsToCacheAsync(string inputAudio, IReadOnlyList<StemTrack> stems)
    {
        if (_stemCache == null || stems.Count == 0) return;
        try
        {
            var identity = BuildStemCacheIdentity(inputAudio);
            // StemCacheSource, same as the read side: a library song is keyed on its
            // source .wem, anything else on its audio. Its TryDerive guard is
            // the one described here: ComputeFile swallows every
            // exception and returns None, which also covers an
            // ACL-denied / AV-quarantined / exclusively-locked file. Saving under
            // the degenerate `…|b=0|h=` key writes a DUPLICATE archive (with
            // SourceBytes=0, SourceHash="" in its manifest) that the correct key can
            // never hit — and which any later transient read failure DOES hit, so
            // the user gets stale stems after replacing the audio in place. A
            // missing save costs one re-separation; a poisoned one is permanent.
            var source = StemCacheSource.TryResolve(identity, inputAudio);
            if (source == null)
            {
                _logger.Warning(AppLogCategory.Audio,
                    $"Source audio '{inputAudio}' could not be fingerprinted; not caching these stems " +
                    "(a keyless archive would be served for the wrong audio later).");
                return;
            }

            await _stemCache.SaveAsync(source.Key, identity, source.Signature, stems, modelId: string.Empty,
                appVersion: string.Empty, CancellationToken.None,
                StemAudioSignature.ComputeFullFileHash(source.SignedPath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, $"Failed to write stems to durable cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers a resolved stem set on the active project, refreshes the mixer
    /// and (for saved projects) persists the <c>&lt;stems/&gt;</c> node. Shared by
    /// the fresh-separation path and the cache-hit fast path.
    /// </summary>
    private Task RegisterAndPersistStemsAsync(
        ChartProject project, IReadOnlyList<StemTrack> finalStems,
        string audioDir, string? projectPath, int keptCount)
    {
        return _ui.InvokeAsync(() =>
        {
            // Identity check on the UI thread (the only thread that switches
            // projects): the caller captured `project` BEFORE a multi-minute
            // separation / archive expansion. If the user opened another song
            // meanwhile, applying here would add the stems to an orphaned
            // project object (never persisted — "it did nothing"), pop the mixer
            // for the wrong song, and fire an UNREQUESTED full save against the
            // newly-opened project. The stems are already in the durable cache,
            // so re-opening that song loads them instantly.
            if (!ReferenceEquals(_projectState.ActiveProject, project))
            {
                _logger.Warning(AppLogCategory.Audio,
                    "Stems finished for a song that is no longer open — not applying them to the current " +
                    "project. They are cached; re-open that song to load them.");
                return;
            }

            // For transient sessions (PSARC imports, audio-only) there is no
            // project directory to make stem paths relative to — store the
            // absolute path instead so StemMixerViewModel.Rebuild() can
            // resolve and load them via Path.IsPathRooted fast-path.
            //
            // The base is the PROJECT's own folder, NOT `audioDir`. Relativizing against
            // the audio directory (ProjectAudioLocator.ResolveOutputDirectory — where the stems are
            // WRITTEN) would disagree with StemMixerViewModel, which resolves against the project
            // folder. The two agree whenever the audio sits beside the project,
            // which is why it goes unnoticed; for a rooted AudioFileName, a PSARC
            // session or a chart in a per-arrangement subfolder they do not, and
            // every stem would then silently fail to load on the next open. The stems
            // still live beside the audio — only the base the NAME is expressed
            // in differs, and `StemFolderProbe.ResolveStemPath` also accepts
            // the audio-relative form.
            var projectDir = Path.GetDirectoryName(projectPath);
            var hasProject = !string.IsNullOrWhiteSpace(projectPath) &&
                             !string.IsNullOrWhiteSpace(projectDir);
            foreach (var s in finalStems)
            {
                var storedPath = hasProject ? Path.GetRelativePath(projectDir!, s.Path) : s.Path;
                var existing = project.Stems.FirstOrDefault(st =>
                    string.Equals(st.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Path = storedPath;
                }
                else
                {
                    project.Stems.Add(new Stem
                    {
                        Name = s.Name,
                        Path = storedPath,
                        Local = true,
                        Volume = 1.0,
                        Offset = 0.0,
                    });
                }
            }
            _logger.Info(AppLogCategory.Audio, $"Stems ready. Added/updated {finalStems.Count} stems (kept {keptCount}).");
            _stemMixer.Rebuild();
            StemsApplied?.Invoke();
            // Persist the updated Stems collection into the .rscp so that
            // reopening the project picks them up without forcing the user
            // to re-run separation. Without this the in-memory project is
            // updated but the file on disk still has the old (or empty)
            // <stems/> node, so the inspector mixer starts empty on reload.
            // Skip the auto-save for transient sessions (PSARC imports etc.)
            // that have no project file path � SaveProject would otherwise
            // write a stray "project.rscp" into the working directory.
            if (hasProject)
            {
                // SaveProjectAsync handles its own faults; fire-and-forget is
                // safe here and this sync dispatcher lambda cannot await it.
                try { _ = _projectSession.SaveProjectAsync(); }
                catch (Exception ex) { _logger.Warning(AppLogCategory.Audio, $"Failed to auto-save project after stem separation: {ex.Message}"); }
            }
            else
            {
                _logger.Info(AppLogCategory.Audio, "Stems ready for an unsaved session; skipping auto-save (no project file on disk).");
            }
        });
    }

    /// <summary>
    ///     What a re-separation would DO to the stems already on disk, as pure
    ///     data: the model overwrites every name it produces
    ///     (<see cref="Replace" />) and cannot touch the rest, so those are moved
    ///     aside and put back (<see cref="Keep" />). Splitting it out is what
    ///     lets the confirm rule be tested on its own.
    /// </summary>
    internal sealed record StemReplacePlan(IReadOnlyList<string> Replace, IReadOnlyList<string> Keep)
    {
        /// <summary>Nothing is overwritten ⇒ nothing to ask about. A prompt that
        /// fires when nothing is lost is the one people learn to click through.</summary>
        public bool OverwritesAnything => Replace.Count > 0;

        public static StemReplacePlan For(IEnumerable<string> existing, IEnumerable<string> modelWillProduce)
        {
            var produced = new HashSet<string>(modelWillProduce, StringComparer.OrdinalIgnoreCase);
            var replace = new List<string>();
            var keep = new List<string>();
            foreach (var name in existing)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                (produced.Contains(name) ? replace : keep).Add(name);
            }

            return new StemReplacePlan(replace, keep);
        }
    }

    /// <summary>
    ///     Tools ▸ Separate Stems, asked before it overwrites. Settable and
    ///     routed rather than called inline so a test can drive the confirm: a
    ///     confirm nobody can drive is a confirm nobody should trust. Wired in the
    ///     constructor to the app-wide prompt.
    /// </summary>
    internal Func<StemReplacePlan, string, Task<bool>>? ConfirmStemReplaceAsync { get; set; }

    /// <summary>
    ///     The prompt's body: it NAMES the stems it is about to overwrite, and
    ///     says which ones survive when the model cannot reproduce them. Pure,
    ///     so the sentence is assertable without a dialog.
    /// </summary>
    internal static string BuildStemReplaceMessage(StemReplacePlan plan, ILocalizationService localization)
    {
        var message = localization.Format("Dialog_StemRegenerate_Message", string.Join(", ", plan.Replace));
        return plan.Keep.Count == 0
            ? message
            : message + " " + localization.Format("Dialog_StemRegenerate_KeptFmt", string.Join(", ", plan.Keep));
    }

    /// <summary>
    ///     The rule: nothing overwritten ⇒ go ahead; UNWIRED ⇒ REFUSE ("no way
    ///     to ask" must never resolve to "do it silently", and here the silent
    ///     answer destroys files); declined ⇒ abort. Returns the names to move
    ///     aside and restore, or <c>null</c> to abandon the run.
    /// </summary>
    internal static async Task<IReadOnlyCollection<string>?> ConfirmStemReplacementAsync(
        StemReplacePlan plan, string backendDescription,
        Func<StemReplacePlan, string, Task<bool>>? confirm)
    {
        if (!plan.OverwritesAnything) return plan.Keep;
        if (confirm == null) return null;
        return await confirm(plan, backendDescription) ? plan.Keep : null;
    }

    /// <summary>
    ///     First separation for a user on the DEFAULT model: asked once, naming the
    /// model, its size and the fallback. Settable so a test can drive it;
    ///     wired in the constructor to the app-wide prompt.
    /// </summary>
    internal Func<StemDefaultDownloadOffer, Task<bool>>? ConfirmDefaultDownloadAsync { get; set; }

    /// <summary>The rule: UNWIRED ⇒ REFUSE. A 353 MB download is never the silent answer
    /// to "there was no way to ask".</summary>
    internal static async Task<bool> ConfirmDefaultDownloadPromptAsync(
        StemDefaultDownloadOffer offer, Func<StemDefaultDownloadOffer, Task<bool>>? confirm)
        => confirm != null && await confirm(offer);

    /// <summary>The prompt's body: the model, its size, and the fallback with its size — or,
    /// for an upgrade, the installed model the user keeps on a no. Pure, so the sentence is
    /// assertable without a dialog.</summary>
    internal static string BuildDefaultDownloadMessage(StemDefaultDownloadOffer offer, ILocalizationService localization)
        => offer.IsUpgrade
            ? localization.Format("Dialog_StemsDefaultUpgrade_Message",
                offer.DisplayName, Megabytes(offer.Bytes), offer.FallbackDisplayName)
            : localization.Format("Dialog_StemsDefaultDownload_Message",
                offer.DisplayName, Megabytes(offer.Bytes), offer.FallbackDisplayName, Megabytes(offer.FallbackBytes));

    private static long Megabytes(long bytes) => (bytes + 512 * 1024) / (1024 * 1024);

    /// <summary>The agreed download, on the status pill with a cancel (and the caller's token).
    /// <c>null</c> when the separation goes on; the outcome that ends it when the user cancelled,
    /// or when nothing usable arrived for a first download (the failure is logged). A failed
    /// UPGRADE goes on: the installed default the prompt named still separates.</summary>
    private async Task<SongStemSeparationOutcome?> DownloadDefaultModelAsync(StemDefaultDownloadOffer offer,
        CancellationToken externalCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        const string taskId = "stem-model-download";
        _statusBarService?.BeginTask(taskId,
            _localization.Format("Progress_DownloadingModel", offer.DisplayName, Megabytes(offer.Bytes)),
            indeterminate: false, cancel: () => cts.Cancel());
        try
        {
            var id = await _defaultModels!.DownloadAsync(offer,
                new Progress<double>(v => _statusBarService?.ReportProgress(taskId, v)), cts.Token).ConfigureAwait(false);
            if (id != null) return null;
            if (offer.IsUpgrade)
            {
                _logger.Warning(AppLogCategory.Audio,
                    $"'{offer.ModelId}' could not be downloaded; separating with '{offer.FallbackId}'.");
                return null;
            }

            _logger.Error(AppLogCategory.Audio,
                $"Neither default stem model could be downloaded ('{offer.ModelId}', '{offer.FallbackId}').");
            return Outcome(SongStemSeparationStatus.Failed, _localization["StemSeparation_DownloadFailed"]);
        }
        catch (OperationCanceledException)
        {
            _logger.Info(AppLogCategory.Audio, "Default stem model download cancelled.");
            return Outcome(SongStemSeparationStatus.Cancelled);
        }
        finally
        {
            _statusBarService?.CompleteTask(taskId);
        }
    }
}
