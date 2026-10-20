using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using DotCharter.Localization;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Services.Session;

/// <summary>
///     The project session's timed zip backups: the 30 s tick on the UI thread,
///     the dirty gate (<see cref="AutoBackupGate" />) that keeps an untouched
///     project from writing the same zip every interval, and the writer that
///     snapshots on the UI thread and serializes + zips on a worker into
///     <c>&lt;project folder&gt;/backups/</c>.
/// </summary>
internal sealed class ProjectAutoBackup : IDisposable
{
    private readonly IConfigurationService _configService;
    private readonly ProjectDirtyTracker _dirtyTracker;
    private readonly IProjectDocumentService _documentService;
    private readonly ProjectState _projectState;
    private readonly IEditorTransport _transport;
    private readonly IChartEditorSurface _chartEditor;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    private IDisposable? _timer;
    private bool _inProgress;
    private DateTimeOffset _lastBackupUtc = DateTimeOffset.UtcNow;

    /// <summary>The project content the last auto-backup captured
    /// (<c>ProjectDirtyTracker.ChangeStamp</c>), so a dirty-but-IDLE project
    /// backs up once instead of once per interval. Forgotten on a project
    /// switch — see <see cref="ResetSchedule" />.</summary>
    private long _lastBackupStamp = AutoBackupGate.NoBackupStamp;

    public ProjectAutoBackup(
        IConfigurationService configService,
        ProjectDirtyTracker dirtyTracker,
        IProjectDocumentService documentService,
        ProjectState projectState,
        IEditorTransport transport,
        IChartEditorSurface chartEditor,
        IUiDispatcher uiDispatcher,
        IAppLogger logger,
        ILocalizationService localization)
    {
        _configService = configService;
        _dirtyTracker = dirtyTracker;
        _documentService = documentService;
        _projectState = projectState;
        _transport = transport;
        _chartEditor = chartEditor;
        _uiDispatcher = uiDispatcher;
        _logger = logger;
        _localization = localization;
    }

    /// <summary>Arms the periodic tick once: Background priority on the UI thread
    /// (<c>IUiDispatcher.SchedulePeriodic</c>), as the DispatcherTimer it
    /// replaces defaulted to.</summary>
    public void Start() => _timer ??= _uiDispatcher.SchedulePeriodic(TimeSpan.FromSeconds(30), Tick);

    /// <summary>Restarts the auto-backup interval from now.</summary>
    /// <param name="forgetLastBackup">
    ///     Also drop the stamp of the last backup's content. True for a project
    ///     SWITCH — <c>ChangeStamp</c> is only comparable within one history, so
    ///     the previous project's stamp could collide with this one's and skip
    ///     the new project's first backup. False for the tick's own
    ///     "nothing changed" arm, which is precisely a claim ABOUT that stamp.
    /// </param>
    public void ResetSchedule(bool forgetLastBackup = true)
    {
        _lastBackupUtc = DateTimeOffset.UtcNow;
        if (forgetLastBackup)
        {
            _lastBackupStamp = AutoBackupGate.NoBackupStamp;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>One timer tick; internal so a test can drive it without a clock.</summary>
    internal void Tick()
    {
        var config = _configService.LoadAppConfig();
        var interval = TimeSpan.FromMinutes(Math.Max(1, config.AutoBackupIntervalMinutes));
        // The dirty gate (AutoBackupGate): an untouched project must not pay the
        // whole-project assemble + deep clone on the UI thread every interval
        // and write one more identical zip, forever.
        switch (AutoBackupGate.Decide(config.AutoBackupEnabled, _inProgress, _dirtyTracker.IsDirty,
                    _dirtyTracker.ChangeStamp, _lastBackupStamp,
                    DateTimeOffset.UtcNow, _lastBackupUtc, interval))
        {
            case AutoBackupGate.Verdict.Skip:
                return;
            case AutoBackupGate.Verdict.ResetSchedule:
                // NOT the stamp: this arm covers "nothing has changed since the
                // last backup", and forgetting the stamp here would make the very
                // next tick write that same zip again.
                ResetSchedule(forgetLastBackup: false);
                return;
        }

        _lastBackupUtc = DateTimeOffset.UtcNow;
        // Captured BEFORE the write, from the same state CreateBackupAsync is
        // about to assemble: an edit made while the zip is being written must
        // count as a change the next backup still owes.
        _lastBackupStamp = _dirtyTracker.ChangeStamp;
        // Fire-and-forget is safe: CreateBackupAsync catches everything
        // internally and _inProgress prevents overlap.
        _ = CreateBackupAsync();
    }

    /// <summary>Writes one backup zip now; internal so a test can await it.</summary>
    internal async Task CreateBackupAsync()
    {
        if (_inProgress)
        {
            return;
        }

        if (_projectState.ActiveProject == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_projectState.CurrentProjectPath))
        {
            return;
        }

        _inProgress = true;
        string? tempPath = null;

        try
        {
            var projectPath = Path.ChangeExtension(_projectState.CurrentProjectPath, ".rscp");
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                projectDirectory = Directory.GetCurrentDirectory();
            }

            var backupDirectory = Path.Combine(projectDirectory, "backups");

            var baseName = SafeFileName.Make(Path.GetFileNameWithoutExtension(projectPath));
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "project";
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var entryName = $"{baseName}_{timestamp}.rscp";

            // Capture phase (UI thread): assemble exactly what a manual save
            // would persist, then deep-clone it. The worker below serializes
            // the CLONE — handing it the live mutable project raced against
            // concurrent edits and could write a torn/corrupt backup (same
            // protection as SaveProjectAsync / CrashRecoveryService).
            var assembled = _documentService.AssembleProject(_projectState.ActiveProject, _transport.Bpm,
                _projectState.CurrentAudioFileName, _chartEditor.LyricPhrases);
            var snapshot = ChartProjectCloner.Clone(assembled);

            tempPath = Path.Combine(Path.GetTempPath(), $"DotCharter_Backup_{Guid.NewGuid():N}.rscp");
            var backupTempPath = tempPath;

            // The tick runs on the UI thread (DispatcherTimer); serializing
            // the chart and zipping it there caused a visible hitch every
            // backup interval, growing with chart size. Run both on a worker —
            // and the directory probe + the unique-name File.Exists loop with
            // them: the project folder is routinely a network share, and a
            // filesystem call on the UI thread is the rule UiThreadIoDeferralTests
            // exists for.
            var archivePath = await Task.Run(() =>
            {
                Directory.CreateDirectory(backupDirectory);
                var path = UniqueArchivePath(backupDirectory, baseName, timestamp);
                _documentService.SaveChart(backupTempPath, snapshot);

                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(backupTempPath, entryName, CompressionLevel.Optimal);

                // Only after a successful write, and only this project's own zips.
                PruneBackups(backupDirectory, baseName, _configService.LoadAppConfig().AutoBackupKeepCount, _logger);
                return path;
            });

            _logger.Info(AppLogCategory.Project,
                $"Auto backup saved. Archive='{archivePath}', entry='{entryName}', intervalMinutes={_configService.LoadAppConfig().AutoBackupIntervalMinutes}.");
            _projectState.SetMessage(_localization.Format("Status_AutoBackupSaved", Path.GetFileName(archivePath)),
                StatusSeverity.Success);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Auto backup failed.", ex);
            _projectState.SetMessage(_localization.Format("Status_AutoBackupFailed", ex.Message), StatusSeverity.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(AppLogCategory.Project,
                        $"Could not delete temporary auto backup file '{tempPath}'. Message='{ex.Message}'.");
                }
            }

            _inProgress = false;
        }
    }

    /// <summary>
    ///     Deletes this project's oldest auto-backups beyond <paramref name="keep" />.
    /// Only files named exactly <c>&lt;base&gt;_&lt;yyyyMMdd&gt;_&lt;HHmmss&gt;[_n].rscp.zip</c>
    ///     count, so another project's zips in the same folder (<c>song</c> vs
    ///     <c>song_live</c>) and anything the user put there are never touched.
    ///     <paramref name="keep" /> &lt;= 0 keeps everything. A file that cannot be
    ///     deleted is logged and skipped. Returns how many were deleted.
    /// </summary>
    internal static int PruneBackups(string backupDirectory, string baseName, int keep, IAppLogger? logger = null)
    {
        if (keep <= 0 || !Directory.Exists(backupDirectory)) return 0;

        var pattern = new Regex("^" + Regex.Escape(baseName) + @"_(\d{8}_\d{6})(?:_(\d+))?\.rscp\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var ours = new List<(string Path, string Stamp, int Suffix)>();
        foreach (var path in Directory.EnumerateFiles(backupDirectory, "*.rscp.zip"))
        {
            var match = pattern.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            var suffix = match.Groups[2].Success
                ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                : 0;
            ours.Add((path, match.Groups[1].Value, suffix));
        }

        var deleted = 0;
        foreach (var old in ours
                     .OrderByDescending(b => b.Stamp, StringComparer.Ordinal)
                     .ThenByDescending(b => b.Suffix)
                     .Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warning(AppLogCategory.Project, $"Could not prune old auto backup '{old.Path}': {ex.Message}");
            }
        }

        if (deleted > 0)
            logger?.Info(AppLogCategory.Project, $"Pruned {deleted} old auto backup(s) of '{baseName}', keeping {keep}.");
        return deleted;
    }

    /// <summary>The first free <c>&lt;base&gt;_&lt;timestamp&gt;[_n].rscp.zip</c> in the folder.</summary>
    internal static string UniqueArchivePath(string backupDirectory, string baseName, string timestamp)
    {
        var archivePath = Path.Combine(backupDirectory, $"{baseName}_{timestamp}.rscp.zip");
        var suffix = 1;

        while (File.Exists(archivePath))
        {
            archivePath = Path.Combine(backupDirectory, $"{baseName}_{timestamp}_{suffix}.rscp.zip");
            suffix++;
        }

        return archivePath;
    }
}
