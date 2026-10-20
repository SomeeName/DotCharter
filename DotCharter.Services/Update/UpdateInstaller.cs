using DotCharter.Services.Infrastructure.Process;

namespace DotCharter.Services.Update;

/// <summary>
///     The install half of the self-updater: whether this install can be updated
///     in place, expanding a downloaded package into the staging tree, the
///     resumable pending-apply record, and launching the updater script that
///     copies the payload over the install once the app has exited.
///     <para>
///         <see cref="GitHubUpdateService" /> owns the release check and the
///         downloads and forwards its <see cref="IAppUpdateService" /> install
///         calls here. Every apply path — the GitHub download and the LAN peer's
///         package — funnels through <see cref="ApplyDownloadedUpdate" />, so this
///         is the one place an update archive is expanded and checked.
///     </para>
/// </summary>
public sealed class UpdateInstaller
{
    private readonly string _installDir;
    private readonly IAppLogger? _logger;
    private readonly IReadOnlyList<string> _pluginFiles;

    /// <param name="installDir">The install's TOP, not <c>app\</c> — the
    /// folder the payload goes over.</param>
    /// <param name="logger">Where every refusal and the launch are logged.</param>
    /// <param name="pluginFiles">The plugin files the running app declares
    /// (<see cref="PluginUpdateGuard" />); none by default.</param>
    public UpdateInstaller(string installDir, IAppLogger? logger = null, IReadOnlyList<string>? pluginFiles = null)
    {
        _installDir = installDir;
        _logger = logger;
        _pluginFiles = pluginFiles ?? [];
    }

    /// <summary>Overrides the extraction budget <see cref="ApplyDownloadedUpdate" />
    /// enforces. Test seam only: the real ceiling floors at 512 MiB, and proving
    /// the budget bites through it would cost the gate half a gigabyte of disk
    /// writes. The FORMULA is pinned separately and purely on
    /// <see cref="AppPayload.InflationCeiling" />.</summary>
    internal long? ExtractionBudgetOverrideForTests { get; set; }

    public UpdateApplyBlockReason GetApplyBlockReason()
    {
        if (!File.Exists(Path.Combine(_installDir, AppPayload.ExeName)))
            return UpdateApplyBlockReason.NotAReleaseInstall;
        if (AppPayload.ClassifyDirectory(_installDir) == InstallLayout.Unknown)
            return UpdateApplyBlockReason.UnknownInstallLayout;
        if (!IsDirectoryWritable(_installDir))
            return UpdateApplyBlockReason.InstallDirNotWritable;
        return UpdateApplyBlockReason.None;
    }

    public void ApplyDownloadedUpdate(string zipPath)
    {
        var block = GetApplyBlockReason();
        if (block != UpdateApplyBlockReason.None)
            throw new InvalidOperationException($"Cannot apply update in place: {block}.");

        // Non-Unknown, because GetApplyBlockReason just said so.
        var installLayout = AppPayload.ClassifyDirectory(_installDir);

        var stagingRoot = Path.Combine(_installDir, UpdaterScriptBuilder.StagingDirName);

        // A pending record from an earlier attempt describes a payload INSIDE the
        // tree the next line destroys, so it stops being true here — whether or
        // not the rest of this method succeeds. Leaving it behind is how one
        // unrelated failed download (a refused archive, contraband, a layout
        // mismatch — every one of which throws below) left the app reporting on
        // every start that "an update was staged" and that "the staged payload is
        // gone", with Discard as the only honest offer. A successful apply writes
        // the new record at the end.
        BestEffortDelete.File(UpdaterScriptBuilder.PendingStatePathFor(_installDir));

        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);

        string payloadDir;
        IReadOnlyList<string> deletions;
        try
        {
            // BOTH apply paths land here — the GitHub download AND the LAN peer's
            // package — so this is the one place the archive is expanded, and it
            // must not be a bare ZipFile.ExtractToDirectory with no member-count
            // and no decompressed-byte budget at all. Untrusted archives go
            // through BoundedZipExtractor; that rule has no exception for "the
            // file is called an update".
            var bounds = AppPayload.CheckArchiveBounds(zipPath);
            if (bounds != null)
            {
                throw new InvalidDataException(
                    $"Refusing to apply '{Path.GetFileName(zipPath)}': {bounds}.");
            }

            var budget = ExtractionBudgetOverrideForTests ?? AppPayload.InflationCeiling(new FileInfo(zipPath).Length);
            var extraction = BoundedZipExtractor.ExtractBounded(
                zipPath, stagingRoot, budget, AppPayload.MaxArchiveEntries);
            if (!extraction.Succeeded)
            {
                throw new InvalidDataException(
                    $"Refusing to apply '{Path.GetFileName(zipPath)}': the archive was rejected " +
                    $"({extraction.Refusal}) after {extraction.BytesWritten} bytes in " +
                    $"{extraction.FilesWritten} file(s).");
            }

            payloadDir = ResolvePayloadRoot(stagingRoot);

            // The updater copies this tree over the install dir WITHOUT purging, so
            // a payload of a different runtime layout leaves an unbootable mix of
            // host files behind. Once the script is launched nothing can stop it —
            // this is the last point where we can still refuse.
            // An install with plugins beside the app never takes a payload without them:
            // a plugin would stop loading, or be deleted by a partial update.
            var problem = AppPayload.ValidateDirectory(payloadDir, installLayout)
                          ?? AppPayload.CheckShape(_installDir, payloadDir)
                          ?? PluginUpdateGuard.Check(PluginUpdateGuard.MissingFromPayload(
                              payloadDir, PluginUpdateGuard.InstalledPlugins(_installDir, _pluginFiles)));
            if (problem != null)
            {
                throw new InvalidDataException(
                    $"Refusing to apply '{Path.GetFileName(zipPath)}': {problem}.");
            }

            // …and ValidateDirectory is a PRESENCE check: it asks whether the exe,
            // the runtimeconfig and the host pair are there, never what else came
            // along. A zip that was not built by our release pipeline could ship
            // the victim's own binaries plus one appconfig.json and own their
            // settings (on a portable install that file also DEFINES the config
            // root). PayloadReceiptFilter is the list that keeps user data and
            // credential stores out: a package is filtered on RECEIPT, never
            // trusted because whoever built it filtered. Every apply path lands
            // here.
            var contraband = PayloadReceiptFilter.FindRefusal(payloadDir);
            if (contraband != null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Refused update payload '{Path.GetFileName(zipPath)}': {contraband}.");
                throw new InvalidDataException(
                    $"Refusing to apply '{Path.GetFileName(zipPath)}': {contraband}.");
            }

            // No purge means a file the new version dropped outlives it unless
            // the script removes it. Reading this also takes it OUT of the
            // payload, so the list itself is never copied into the install.
            deletions = PayloadDeletions.TakeFrom(payloadDir);
            // Only a pre-0.63 updater needs these; here they would be clutter.
            LegacyUpdaterShim.RemoveFrom(payloadDir);
            if (deletions.Count > 0)
            {
                _logger?.Info(AppLogCategory.Application,
                    $"Update payload removes {deletions.Count} file(s) from the install.");
            }
        }
        catch
        {
            BestEffortDelete.Directory(stagingRoot);
            throw;
        }

        // Written BEFORE the launch, so a launch that never happens still leaves
        // a resumable record rather than an orphaned staging tree nobody knows
        // about. The script deletes it on its success path only.
        var state = new UpdateApplyState
        {
            PayloadDir = payloadDir,
            StagingRoot = stagingRoot,
            ZipPath = zipPath,
            Deletions = deletions,
            StagedUtc = DateTimeOffset.UtcNow,
            TargetVersion = AppPayload.TryReadVersion(payloadDir),
        };
        // Atomic + flushed: a crash or power cut between this write and the
        // updater's first step must not leave a half-written record, because
        // the record is what makes the staged tree resumable at all.
        AtomicFile.WriteAllText(UpdaterScriptBuilder.PendingStatePathFor(_installDir), state.ToJson());

        LaunchUpdater(state);
    }

    public PendingUpdateApply? GetPendingUpdateApply()
    {
        try
        {
            var statePath = UpdaterScriptBuilder.PendingStatePathFor(_installDir);
            if (!File.Exists(statePath)) return null;

            var state = UpdateApplyState.TryParse(AtomicFile.ReadAllText(statePath));
            if (state == null)
            {
                // Unreadable, but it still names a staging tree we may be sitting
                // on. Report it as unresumable so the user can discard it.
                return new PendingUpdateApply(
                    new UpdateApplyState { StagingRoot = UpdaterScriptBuilder.StagingRootFor(_installDir) },
                    CanResume: false,
                    Problem: "the staged-update record could not be read",
                    LogPath: ExistingLogPath(),
                    LogTail: ReadLogTail());
            }

            var problem = DescribePendingProblem(state);
            return new PendingUpdateApply(
                state, problem == null, problem, ExistingLogPath(), ReadLogTail());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Could not read the staged-update record: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Re-launches the updater for a payload that is already staged. The
    ///     script is REGENERATED rather than re-run: the one on disk waits on the
    ///     PREVIOUS run's process id, which is dead — and worse, may have been
    ///     reused by an unrelated process, in which case the old script would wait
    ///     on a stranger. The caller must close the application right after this
    ///     returns, exactly as for <see cref="ApplyDownloadedUpdate" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is staged, or the
    /// staged payload does not apply any more.</exception>
    /// <exception cref="UpdaterLaunchException">The updater could not be started.</exception>
    public void ResumePendingUpdateApply()
    {
        var pending = GetPendingUpdateApply()
                      ?? throw new InvalidOperationException("There is no staged update to finish.");
        if (!pending.CanResume)
        {
            throw new InvalidOperationException(
                $"The staged update cannot be finished: {pending.Problem}.");
        }

        _logger?.Info(AppLogCategory.Application,
            $"Resuming the update staged at {pending.State.StagedUtc:u} from '{pending.State.PayloadDir}'.");
        LaunchUpdater(pending.State);
    }

    /// <summary>Throws away a staged update: the payload, the script, the probe
    /// file and the record. The transcript is KEPT — it is the only evidence of
    /// why the update did not happen.</summary>
    public void DiscardPendingUpdateApply()
    {
        var state = GetPendingUpdateApply()?.State;
        var staging = string.IsNullOrWhiteSpace(state?.StagingRoot)
            ? UpdaterScriptBuilder.StagingRootFor(_installDir)
            : state!.StagingRoot;

        BestEffortDelete.Directory(staging);
        BestEffortDelete.File(UpdaterScriptBuilder.ScriptPathFor(_installDir));
        BestEffortDelete.File(Path.Combine(_installDir, UpdaterScriptBuilder.WaitProbeFileName));
        BestEffortDelete.File(UpdaterScriptBuilder.PendingStatePathFor(_installDir));
        if (!string.IsNullOrWhiteSpace(state?.ZipPath)) BestEffortDelete.File(state!.ZipPath);

        _logger?.Info(AppLogCategory.Application, "Discarded the staged update at the user's request.");
    }

    private string? DescribePendingProblem(UpdateApplyState state)
    {
        if (!Directory.Exists(state.PayloadDir)) return "the staged payload is gone";
        var layout = AppPayload.ClassifyDirectory(_installDir);
        if (layout == InstallLayout.Unknown) return "this install's layout is not recognised";
        return AppPayload.ValidateDirectory(state.PayloadDir, layout);
    }

    private string? ExistingLogPath()
    {
        var path = UpdaterScriptBuilder.LogPathFor(_installDir);
        return File.Exists(path) ? path : null;
    }

    private string ReadLogTail()
    {
        try
        {
            var path = UpdaterScriptBuilder.LogPathFor(_installDir);
            if (!File.Exists(path)) return string.Empty;
            var lines = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, lines.TakeLast(8)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void LaunchUpdater(UpdateApplyState state)
    {
        var script = UpdaterScriptBuilder.Build(
            Environment.ProcessId, state.PayloadDir, state.StagingRoot,
            _installDir, AppPayload.ExeName, state.ZipPath, state.Deletions);
        var scriptPath = UpdaterScriptBuilder.ScriptPathFor(_installDir);
        // Plain ASCII content — safe with cmd.exe's default codepage.
        File.WriteAllText(scriptPath, script);

        // Hidden, cwd OUTSIDE the install dir (a cwd inside it would keep handles
        // that block the staging cleanup).
        //
        // This is the ONE child that must OUTLIVE the app — it waits for our PID
        // to disappear and only then copies the payload over the install dir. The
        // app runs inside a kill-on-close job object so nothing else can be
        // orphaned, so this one has to break out of it explicitly; otherwise
        // exiting to apply the update would kill the updater with us and the
        // update would silently never happen.
        int updaterPid;
        try
        {
            updaterPid = ChildProcessJob.StartDetached(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"cmd.exe /D /C \"\"{scriptPath}\"\"",
                Path.GetTempPath());
        }
        catch (Exception ex)
        {
            // The staged payload and the pending record are deliberately LEFT ON
            // DISK: the download is expensive, the payload is valid, and the next
            // start can finish it. What must not happen is the app exiting anyway
            // — the caller sees this and keeps the session alive.
            _logger?.Error(AppLogCategory.Application,
                $"The updater process could not be started; the update is staged but NOT applied. " +
                $"Script '{scriptPath}', staging '{state.StagingRoot}'.", ex);
            throw new UpdaterLaunchException(
                $"The updater process could not be started ({ex.Message}). " +
                "The downloaded update is still staged and can be finished from Settings → Updates.",
                ex);
        }

        _logger?.Info(AppLogCategory.Application,
            $"Updater launched (pid {updaterPid}, script '{scriptPath}', staging '{state.StagingRoot}', " +
            $"log '{UpdaterScriptBuilder.LogPathFor(_installDir)}'). Application should now exit.");
    }

    /// <summary>Release zips may wrap everything in a single top-level
    /// folder; copy from that folder when it is the one holding the exe.</summary>
    internal static string ResolvePayloadRoot(string stagingRoot)
    {
        if (File.Exists(Path.Combine(stagingRoot, AppPayload.ExeName)))
            return stagingRoot;

        var directories = Directory.GetDirectories(stagingRoot);
        var files = Directory.GetFiles(stagingRoot);
        if (files.Length == 0 && directories.Length == 1 &&
            File.Exists(Path.Combine(directories[0], AppPayload.ExeName)))
        {
            return directories[0];
        }

        return stagingRoot;
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var probe = Path.Combine(path, $".dc-update-probe-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
