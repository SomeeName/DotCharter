using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Update;

/// <summary>
/// Generates the small Windows batch script that finishes a self-update
/// after the app exits: wait for our PID to go away, robocopy the staged
/// payload over the install directory, restart DotCharter.exe and clean up
/// (staging dir, downloaded zip, the pending-apply state file, the script
/// itself).
///
/// <para>
///     <b>Two rules this file exists to hold, both learned the hard way.</b>
/// </para>
/// <para>
///     <b>1. No pipelines.</b> The wait loop must not be
///     <c>tasklist … | findstr …</c>. That is a deadlock when cmd.exe has no
///     console (which is what <c>DETACHED_PROCESS</c> gave it — see
///     <c>ChildProcessJob.DetachedProcess</c>), and the hang was total: every
///     update staged its payload, closed the app and then sat at 0 % CPU
///     forever. The launch flags are fixed, but the script stays pipeline-free
///     as well, because a script that can only work under one particular set of
///     creation flags is a trap for whoever changes them next. <c>tasklist</c>
///     writes a probe file; <c>findstr</c> reads it.
/// </para>
/// <para>
///     <b>2. The wait is BOUNDED and everything is LOGGED.</b> An unbounded spin
///     on a PID that may never die is indefensible however the process got
///     there, and a total failure that leaves no file behind is undiagnosable.
///     On timeout the script copies NOTHING — a half-copied install is the
///     unbootable host/runtime mix <c>AppPayload</c> exists to prevent — and
///     leaves the staging tree, this script and the pending-state file in place
///     so the app offers to finish or discard on next start.
/// </para>
/// </summary>
public static class UpdaterScriptBuilder
{
    /// <summary>The FIXED script name older versions wrote beside the
    /// install. Never written any more; still removed when found, and still refused
    /// inside a payload (<see cref="PayloadReceiptFilter" />).</summary>
    public const string ScriptFileName = "dotcharter-update.cmd";

    /// <summary>Prefix of the randomly named script in <see cref="DefaultScriptDirectory" />.</summary>
    public const string ScriptFilePrefix = "dotcharter-update-";
    public const string StagingDirName = "update-staging";

    /// <summary>Human-readable transcript of the last update attempt, written
    /// beside the install. This is the ONE file to ask a user for when an update
    /// did not happen.</summary>
    public const string LogFileName = "dotcharter-update.log";

    /// <summary>What was staged, so a launch that never ran (or a wait that
    /// timed out) can be resumed or discarded on the next start instead of
    /// leaving the user to copy files by hand. Deleted by the script on the
    /// success path only.</summary>
    public const string PendingStateFileName = "dotcharter-update-pending.json";

    /// <summary>Where <c>tasklist</c>'s output lands so the wait loop needs no
    /// pipeline. Removed before the copy.</summary>
    public const string WaitProbeFileName = "dotcharter-update-wait.tmp";

    /// <summary>How long the script waits for the app's PID to disappear before
    /// giving up. Generous — a clean shutdown takes seconds — but finite.</summary>
    public const int DefaultWaitTimeoutSeconds = 300;

    /// <summary>Script exit code: robocopy reported a failure, the install is
    /// untouched and the old build was restarted.</summary>
    public const int ExitCopyFailed = 1;

    /// <summary>Script exit code: the app never exited. Nothing was copied and
    /// nothing was deleted.</summary>
    public const int ExitWaitTimedOut = 2;

    /// <summary>Script exit code: the copy failed AND the files it had replaced
    /// could not all be put back. The install is a mix of old and new files; the
    /// backup, staging and pending state were kept.</summary>
    public const int ExitRestoreFailed = 3;

    /// <summary>Where the files a payload will overwrite are saved before the
    /// copy, inside the install dir. Removed on success and after a restore.</summary>
    public const string BackupDirName = "update-backup";

    /// <summary>UTF-8, no BOM; see <see cref="CreateScriptFile" />.</summary>
    public static readonly Encoding ScriptEncoding = new UTF8Encoding(false);

    /// <summary>Where older versions wrote the script; only ever deleted now.</summary>
    public static string LegacyScriptPathFor(string installDir) => Path.Combine(TrimDir(installDir), ScriptFileName);

    /// <summary>The per-user folder the script is written to: under the user's
    /// own temp folder, which only that user can write.</summary>
    public static string DefaultScriptDirectory() => Path.Combine(Path.GetTempPath(), "DotCharter-updater");

    /// <summary>
    ///     Writes <paramref name="script" /> to a NEW file with an unpredictable
    ///     name in <paramref name="scriptDir" /> and returns its path. CreateNew, so
    ///     a file somebody planted under that name is never reused or overwritten.
    ///     UTF-8 without a byte-order mark: the script's second line is
    ///     <c>chcp 65001</c>, so cmd.exe reads every later line (the install, staging
    ///     and log paths) as UTF-8 and a non-ASCII folder name survives. A BOM would
    ///     put three bytes in front of <c>@echo off</c>, so none is written.
    /// </summary>
    public static string CreateScriptFile(string scriptDir, string script)
    {
        Directory.CreateDirectory(scriptDir);
        var path = Path.Combine(scriptDir, ScriptFilePrefix + RandomNumberGenerator.GetHexString(32, lowercase: true) + ".cmd");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(ScriptEncoding.GetBytes(script));
        return path;
    }

    /// <summary>Removes scripts an earlier run left in <paramref name="scriptDir" />
    /// (a successful run deletes its own).</summary>
    public static void DeleteStaleScripts(string scriptDir)
    {
        if (!Directory.Exists(scriptDir)) return;
        foreach (var stale in Directory.EnumerateFiles(scriptDir, ScriptFilePrefix + "*.cmd"))
            BestEffortDelete.File(stale);
    }
    public static string LogPathFor(string installDir) => Path.Combine(TrimDir(installDir), LogFileName);
    public static string StagingRootFor(string installDir) => Path.Combine(TrimDir(installDir), StagingDirName);

    public static string PendingStatePathFor(string installDir)
        => Path.Combine(TrimDir(installDir), PendingStateFileName);

    /// <summary>
    /// Builds the script content. All paths are embedded quoted; trailing
    /// directory separators are stripped because a trailing backslash
    /// inside a quoted cmd argument escapes the closing quote.
    /// </summary>
    /// <param name="processId">PID of the running app the script waits on.</param>
    /// <param name="payloadDir">Directory whose contents get copied over the
    /// install dir (the staging root, or its single subfolder when the zip
    /// wraps everything in one).</param>
    /// <param name="stagingRoot">The staging root directory to delete afterwards.</param>
    /// <param name="installDir">The install directory (receives the copy).</param>
    /// <param name="exeName">Executable to restart, e.g. "DotCharter.exe".</param>
    /// <param name="zipPath">Downloaded update zip to delete afterwards.</param>
    /// <param name="deletions">
    ///     Payload-relative files the new version dropped, from
    ///     <see cref="PayloadDeletions" />. Robocopy runs with NO purge — that is
    ///     what lets a patch carry only the changed files — so these would
    ///     otherwise survive in the install forever. Pass them already parsed:
    ///     each one becomes a <c>del</c> argument here.
    /// </param>
    /// <param name="waitTimeoutSeconds">Upper bound on the wait for
    /// <paramref name="processId" /> to disappear.</param>
    public static string Build(
        int processId,
        string payloadDir,
        string stagingRoot,
        string installDir,
        string exeName,
        string zipPath,
        IReadOnlyList<string>? deletions = null,
        int waitTimeoutSeconds = DefaultWaitTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(waitTimeoutSeconds, 1);

        var payload = TrimDir(payloadDir);
        var staging = TrimDir(stagingRoot);
        var install = TrimDir(installDir);
        var backupLength = payload.Length + 1;
        var exePath = Esc(install + "\\" + exeName);
        var logPath = Esc(LogPathFor(install));
        var probePath = Esc(Path.Combine(install, WaitProbeFileName));
        var statePath = Esc(PendingStatePathFor(install));
        var backupDir = Esc(Path.Combine(install, BackupDirName));
        var listPath = Esc(Path.Combine(install, "dotcharter-update-files.tmp"));
        var newPath = Esc(Path.Combine(install, "dotcharter-update-new.tmp"));
        payload = Esc(payload);
        staging = Esc(staging);
        install = Esc(install);
        zipPath = Esc(zipPath);

        // Re-validated here rather than trusted: this is the last place before
        // the paths become `del` arguments, and Parse is what keeps the script
        // plain ASCII and free of anything cmd.exe would re-interpret.
        // `del /F /Q <dir>` deletes every FILE INSIDE that directory, without
        // prompting — so a deletion entry naming a folder wipes it. On a portable
        // install ConfigRoot IS the install dir, which makes `phrases`, `profiles`
        // or `checkpoints` a one-line way to destroy the charter's riff library,
        // player profiles or snapshots. `if exist "path\"` is true only for a
        // directory, so this refuses one and deletes only real files.
        var parsedDeletions = PayloadDeletions.Parse(string.Join('\n', deletions ?? []));
        var removals = string.Concat(
            parsedDeletions
                .Select(relative => install + "\\" + relative.Replace('/', '\\'))
                // `if exist "x\*"` is true only for a directory with contents.
                // Deliberately NOT the usual `if exist "x\"` idiom: a backslash
                // immediately before the closing quote escapes it, which is the
                // quote-safety rule this whole builder is written around.
                .Select(path =>
                    $"if not exist \"{path}\\*\" del /F /Q \"{path}\" >nul 2>&1\r\n"));

        // Notes:
        //  - tasklist /NH prints the PID space-padded inside its own column, so
        //    " {pid} " cannot match a longer PID as a substring. Its output goes
        //    to a FILE, never through a pipe — see the type summary.
        //  - Every counter compare sits on its own line reached by `goto`, never
        //    inside a `( ... )` block: cmd expands %VAR% when it parses a block,
        //    so a loop written with parentheses would compare the value the
        //    counter had on entry, forever.
        //  - robocopy exit codes 0..7 mean success (8+ = failures).
        //  - /IS /IT /IM include same, tweaked and modified files (different NTFS
        //    change times). Size and last-write time do not prove equal content,
        //    for either the payload or the restore; all three classes must copy.
        //  - On copy failure the old exe is restarted anyway (best effort) and
        //    staging is left behind for diagnosis.
        //  - Redirection is written BEFORE the command (`>>"%DCLOG%" echo …`) so
        //    a message that happens to end in a digit cannot be parsed as a
        //    handle redirect.
        //  - "(goto) 2>nul & del ..." is the canonical batch self-delete, and it
        //    ends the script, so control never falls into the failure label.
        var removalNote = parsedDeletions.Count > 0
            ? $">>\"%DCLOG%\" echo [%TIME%] removed {parsedDeletions.Count} file/s the new version dropped.\r\n"
            : string.Empty;

        return "@echo off\r\n"
               + "chcp 65001 >nul\r\n"
               + "setlocal EnableExtensions DisableDelayedExpansion\r\n"
               + "rem DotCharter self-updater (generated). Waits for the app to exit,\r\n"
               + "rem copies the staged update over the install dir and restarts.\r\n"
               + "rem Every step is recorded in the log file below.\r\n"
               + $"set \"DCLOG={logPath}\"\r\n"
               + $"set \"DCPROBE={probePath}\"\r\n"
               + ">\"%DCLOG%\" echo [%DATE% %TIME%] DotCharter updater started.\r\n"
               + $">>\"%DCLOG%\" echo   waiting for pid {processId}, giving up after {waitTimeoutSeconds} seconds\r\n"
               + $">>\"%DCLOG%\" echo   payload  \"{payload}\"\r\n"
               + $">>\"%DCLOG%\" echo   install  \"{install}\"\r\n"
               + "set DCWAIT=0\r\n"
               + ":wait_for_exit\r\n"
               + $"tasklist /FI \"PID eq {processId}\" /NH >\"%DCPROBE%\" 2>nul\r\n"
               + $"findstr /C:\" {processId} \" \"%DCPROBE%\" >nul\r\n"
               + "if errorlevel 1 goto app_exited\r\n"
               + "set /A DCWAIT+=1\r\n"
               + $"if %DCWAIT% GEQ {waitTimeoutSeconds} goto wait_timeout\r\n"
               + "timeout /T 1 /NOBREAK >nul\r\n"
               + "goto wait_for_exit\r\n"
               + ":wait_timeout\r\n"
               + $">>\"%DCLOG%\" echo [%TIME%] TIMEOUT - pid {processId} was still running after {waitTimeoutSeconds} seconds.\r\n"
               + ">>\"%DCLOG%\" echo   Nothing was copied and nothing was deleted; the install is untouched.\r\n"
               + ">>\"%DCLOG%\" echo   The staged payload was kept - DotCharter offers to finish it on next start.\r\n"
               + "del /F /Q \"%DCPROBE%\" >nul 2>&1\r\n"
               + $"exit /B {ExitWaitTimedOut}\r\n"
               + ":app_exited\r\n"
               + ">>\"%DCLOG%\" echo [%TIME%] app exited after %DCWAIT% seconds; copying the payload.\r\n"
               + "del /F /Q \"%DCPROBE%\" >nul 2>&1\r\n"
               + $"set \"DCBACKUP={backupDir}\"\r\n"
               + $"set \"DCLIST={listPath}\"\r\n"
               + $"set \"DCNEW={newPath}\"\r\n"
               + "rd /S /Q \"%DCBACKUP%\" >nul 2>&1\r\n"
               + "type nul >\"%DCNEW%\"\r\n"
               + "set DCBKFAIL=0\r\n"
               + $"dir /S /B /A-D \"{payload}\" >\"%DCLIST%\" 2>nul\r\n"
               + "for /F \"usebackq delims=\" %%F in (\"%DCLIST%\") do (\r\n"
               + "set \"DCABS=%%F\"\r\n"
               + "call :backup_one\r\n"
               + ")\r\n"
               + "del /F /Q \"%DCLIST%\" >nul 2>&1\r\n"
               + "if %DCBKFAIL% EQU 1 goto backup_failed\r\n"
               + ">>\"%DCLOG%\" echo [%TIME%] backed up the files the payload replaces.\r\n"
               + $"robocopy \"{payload}\" \"{install}\" /E /IS /IT /IM /R:5 /W:2 /NFL /NDL /NP >>\"%DCLOG%\" 2>&1\r\n"
               + "set DCRC=%ERRORLEVEL%\r\n"
               + ">>\"%DCLOG%\" echo [%TIME%] robocopy exit code %DCRC% - 0 to 7 mean success, 8 and above mean failure.\r\n"
               + "if %DCRC% GEQ 8 goto copy_failed\r\n"
               + removals
               + removalNote
               + "rd /S /Q \"%DCBACKUP%\" >nul 2>&1\r\n"
               + "del /F /Q \"%DCNEW%\" >nul 2>&1\r\n"
               + $"rd /S /Q \"{staging}\" >nul 2>&1\r\n"
               + $"del /F /Q \"{zipPath}\" >nul 2>&1\r\n"
               + $"del /F /Q \"{statePath}\" >nul 2>&1\r\n"
               + $">>\"%DCLOG%\" echo [%TIME%] update applied; restarting \"{Esc(exeName)}\".\r\n"
               + $"start \"\" \"{exePath}\"\r\n"
               + "(goto) 2>nul & del \"%~f0\"\r\n"
               + FailureLabels(install, exePath, backupLength);
    }

    /// <summary>The failure labels and the per-file backup subroutine: restore on a failed copy, keep everything on a failed restore or backup.</summary>
    private static string FailureLabels(string install, string exePath, int backupLength)
        => ":copy_failed\r\n"
            + ">>\"%DCLOG%\" echo [%TIME%] COPY FAILED - robocopy exit code %DCRC%. Some files may already be new; restoring the backup.\r\n"
            + $"robocopy \"%DCBACKUP%\" \"{install}\" /E /IS /IT /IM /R:3 /W:1 /NFL /NDL /NP >>\"%DCLOG%\" 2>&1\r\n"
            + "set DCRRC=%ERRORLEVEL%\r\n"
            + ">>\"%DCLOG%\" echo [%TIME%] restore robocopy exit code %DCRRC%.\r\n"
            + "if %DCRRC% GEQ 8 goto restore_failed\r\n"
            + $"for /F \"usebackq delims=\" %%N in (\"%DCNEW%\") do del /F /Q \"{install}\\%%~N\" >nul 2>&1\r\n"
            + "rd /S /Q \"%DCBACKUP%\" >nul 2>&1\r\n"
            + "del /F /Q \"%DCNEW%\" >nul 2>&1\r\n"
            + ">>\"%DCLOG%\" echo [%TIME%] RESTORED - every replaced file is back and files the update added were removed; the install is on the old version.\r\n"
            + ">>\"%DCLOG%\" echo   Staging, the pending state and this script were kept; restarting the old build.\r\n"
            + $"start \"\" \"{exePath}\"\r\n"
            + $"exit /B {ExitCopyFailed}\r\n"
            + ":restore_failed\r\n"
            + ">>\"%DCLOG%\" echo [%TIME%] RESTORE FAILED - the install is a MIX of old and new files.\r\n"
            + ">>\"%DCLOG%\" echo   The backup was kept in \"%DCBACKUP%\"; staging and the pending state were kept so the update can be finished.\r\n"
            + $"start \"\" \"{exePath}\"\r\n"
            + $"exit /B {ExitRestoreFailed}\r\n"
            + ":backup_failed\r\n"
            + ">>\"%DCLOG%\" echo [%TIME%] BACKUP FAILED - nothing was copied; the install is untouched on the old version.\r\n"
            + ">>\"%DCLOG%\" echo   Staging, the pending state and this script were kept; restarting the old build.\r\n"
            + "rd /S /Q \"%DCBACKUP%\" >nul 2>&1\r\n"
            + $"start \"\" \"{exePath}\"\r\n"
            + $"exit /B {ExitCopyFailed}\r\n"
            // Subroutine: copies ONE file the payload is about to replace into the
            // backup, or records it as new. No arguments: `call` would expand a
            // percent sign in a path a second time, so the path travels in DCABS.
            + ":backup_one\r\n"
            + $"set \"DCREL=%DCABS:~{backupLength}%\"\r\n"
            + $"if exist \"{install}\\%DCREL%\" goto backup_copy\r\n"
            + ">>\"%DCNEW%\" echo \"%DCREL%\"\r\n"
            + "exit /B 0\r\n"
            + ":backup_copy\r\n"
            + "for %%D in (\"%DCBACKUP%\\%DCREL%\") do if not exist \"%%~dpD.\" mkdir \"%%~dpD\" >nul 2>&1\r\n"
            + $"copy /Y \"{install}\\%DCREL%\" \"%DCBACKUP%\\%DCREL%\" >nul 2>&1\r\n"
            + "if errorlevel 1 set DCBKFAIL=1\r\n"
            + "exit /B 0\r\n";

    private static string TrimDir(string path) => path.TrimEnd('\\', '/');

    /// <summary>Makes a path safe to embed in the script: cmd expands <c>%NAME%</c>
    /// in a batch file, so a literal percent sign is written <c>%%</c>. The
    /// result MUST stay inside double quotes, with delayed expansion disabled.
    /// Quotes and line breaks cannot occur in Windows paths and would escape
    /// that boundary, so refuse them rather than generating executable text.</summary>
    private static string Esc(string text)
    {
        if (text.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A batch path cannot contain quotes, line breaks or null characters.", nameof(text));
        return text.Replace("%", "%%", StringComparison.Ordinal);
    }
}
