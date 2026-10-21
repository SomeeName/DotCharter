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
    private readonly string? _downloadDir;
    private readonly string _scriptDir;

    /// <param name="installDir">The install's TOP, not <c>app\</c> — the
    /// folder the payload goes over.</param>
    /// <param name="logger">Where every refusal and the launch are logged.</param>
    /// <param name="pluginFiles">The plugin files the running app declares
    /// (<see cref="PluginUpdateGuard" />); none by default.</param>
    /// <param name="downloadDir">Where downloaded update zips live. The ONLY place
    /// a pending record's zip may point (and so the only place Discard deletes a
    /// zip from); null means a record may name no zip at all.</param>
    /// <param name="scriptDir">The per-user folder the updater script is written
    /// to under a random name; <see cref="UpdaterScriptBuilder.DefaultScriptDirectory" />
    /// by default.</param>
    public UpdateInstaller(
        string installDir,
        IAppLogger? logger = null,
        IReadOnlyList<string>? pluginFiles = null,
        string? downloadDir = null,
        string? scriptDir = null)
    {
        _installDir = installDir;
        _logger = logger;
        _pluginFiles = pluginFiles ?? [];
        _downloadDir = downloadDir;
        _scriptDir = scriptDir ?? UpdaterScriptBuilder.DefaultScriptDirectory();
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

    /// <summary>
    ///     Stages <paramref name="zipPath" /> and launches the updater.
    /// </summary>
    /// <param name="zipPath">The downloaded update.</param>
    /// <param name="expectedSha256">The SHA-256 the zip had when it was verified
    /// against the signed release digests. The zip is re-hashed here, with a
    /// handle that keeps it from being rewritten until it is staged, and refused
    /// on any difference: it sat in a user-writable folder in between.</param>
    public void ApplyDownloadedUpdate(string zipPath, string expectedSha256)
    {
        var block = GetApplyBlockReason();
        if (block != UpdateApplyBlockReason.None)
            throw new InvalidOperationException($"Cannot apply update in place: {block}.");

        // FileShare.Read: nobody may open the zip for writing between this hash
        // and the end of the extraction below, which reads it by path.
        using var pinned = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pinned)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Refused update '{Path.GetFileName(zipPath)}': it changed after it was verified " +
                $"(expected {expectedSha256}, now {actual}).");
            throw new InvalidDataException(
                $"Refusing to apply '{Path.GetFileName(zipPath)}': the file changed after it was verified " +
                "against the signed release digests.");
        }

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

            // Every path in the record is DATA in a user-writable file, and Resume
            // copies from it while Discard deletes it. Only paths inside the
            // canonical staging and download folders are believed.
            var outside = DescribeForeignPath(state);
            if (outside != null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Ignoring the staged-update record: {outside}.");
                return new PendingUpdateApply(
                    new UpdateApplyState
                    {
                        StagingRoot = UpdaterScriptBuilder.StagingRootFor(_installDir),
                        StagedUtc = state.StagedUtc,
                        TargetVersion = state.TargetVersion,
                    },
                    CanResume: false,
                    Problem: outside,
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
    /// why the update did not happen. Only the CANONICAL staging root is deleted,
    /// and the zip only when the record's path sits in the download folder: the
    /// record is a user-writable file, not an authority on what to delete.</summary>
    public void DiscardPendingUpdateApply()
    {
        var state = GetPendingUpdateApply()?.State;

        BestEffortDelete.Directory(UpdaterScriptBuilder.StagingRootFor(_installDir));
        BestEffortDelete.File(UpdaterScriptBuilder.LegacyScriptPathFor(_installDir));
        UpdaterScriptBuilder.DeleteStaleScripts(_scriptDir);
        BestEffortDelete.File(Path.Combine(_installDir, UpdaterScriptBuilder.WaitProbeFileName));
        BestEffortDelete.File(UpdaterScriptBuilder.PendingStatePathFor(_installDir));
        if (!string.IsNullOrWhiteSpace(state?.ZipPath) && IsInDownloadDir(state!.ZipPath))
            BestEffortDelete.File(state.ZipPath);

        _logger?.Info(AppLogCategory.Application, "Discarded the staged update at the user's request.");
    }

    private string? DescribePendingProblem(UpdateApplyState state)
    {
        if (!Directory.Exists(state.PayloadDir)) return "the staged payload is gone";
        var layout = AppPayload.ClassifyDirectory(_installDir);
        if (layout == InstallLayout.Unknown) return "this install's layout is not recognised";
        // The same receipt filter the apply ran: the staged tree sat on disk since,
        // and a resume must not copy user data or credential stores over the install.
        return AppPayload.ValidateDirectory(state.PayloadDir, layout)
               ?? PayloadReceiptFilter.FindRefusal(state.PayloadDir);
    }

    /// <summary>Why a record's paths are not believed, or null: the staging root
    /// must BE the canonical one, the payload must sit inside it, and the zip (if
    /// any) inside the download folder.</summary>
    private string? DescribeForeignPath(UpdateApplyState state)
    {
        var staging = UpdaterScriptBuilder.StagingRootFor(_installDir);
        if (!SamePath(state.StagingRoot, staging))
            return "the staged-update record names a staging folder that is not this install's";
        if (!SamePath(state.PayloadDir, staging) && !IsUnder(state.PayloadDir, staging))
            return "the staged-update record names a payload outside this install's staging folder";
        if (!string.IsNullOrWhiteSpace(state.ZipPath) && !IsInDownloadDir(state.ZipPath))
            return "the staged-update record names a download outside the update download folder";
        return null;
    }

    private bool IsInDownloadDir(string path) => _downloadDir != null && IsUnder(path, _downloadDir);

    private static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool SamePath(string? a, string b)
        => Canonical(a) is { } full && string.Equals(full, Canonical(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string? path, string root)
        => Canonical(path) is { } full && Canonical(root) is { } top &&
           full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
        // A RANDOM name in a per-user folder, created with CreateNew: a
        // fixed name beside the install is a file anyone who can write there can
        // pre-plant or swap between this write and cmd.exe reading it. A leftover
        // from an older version is removed so it cannot be mistaken for ours.
        BestEffortDelete.File(UpdaterScriptBuilder.LegacyScriptPathFor(_installDir));
        var scriptPath = UpdaterScriptBuilder.CreateScriptFile(_scriptDir, script);

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
