namespace DotCharter.Launcher;

/// <summary>
///     The launcher's decisions, pure so the tests can pin them without starting
///     a process (<c>DotCharter.Tests</c> compiles this file in by link).
/// </summary>
public static class LaunchCommandLine
{
    /// <summary>The folder beside the launcher that holds the app.</summary>
    public const string AppFolderName = "app";

    /// <summary>The app's own executable inside <see cref="AppFolderName" />.</summary>
    public const string AppExeName = "DotCharter.exe";

    /// <summary>Written beside the launcher (or, if that is read-only, to a uniquely named file in %TEMP%) when it cannot start the app.</summary>
    public const string FailureLogName = "dotcharter-launcher.log";

    /// <summary>Exit code: <c>app\DotCharter.exe</c> is not there.</summary>
    public const int MissingAppExitCode = 0x4443_0001;

    /// <summary>Exit code: Windows refused to start it.</summary>
    public const int StartFailedExitCode = 0x4443_0002;

    /// <summary>Where the app is, for a launcher in <paramref name="launcherDirectory" />.</summary>
    public static string AppExePath(string launcherDirectory)
        => Path.Combine(launcherDirectory, AppFolderName, AppExeName);

    /// <summary>
    ///     Everything after the program name in a raw Windows command line,
    ///     byte for byte. The arguments are passed on UNPARSED, so whatever quoting
    ///     the caller used reaches the app exactly as it would have reached it
    ///     without a launcher. The program name follows the
    ///     <c>CommandLineToArgvW</c> rule for argv[0]: a quoted name ends at the
    ///     next quote (no escapes), an unquoted one at the first space or tab.
    /// </summary>
    public static string ArgumentTail(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return string.Empty;

        int end;
        if (commandLine[0] == '"')
        {
            var close = commandLine.IndexOf('"', 1);
            end = close < 0 ? commandLine.Length : close + 1;
        }
        else
        {
            end = commandLine.IndexOfAny([' ', '\t']);
            if (end < 0) end = commandLine.Length;
        }

        while (end < commandLine.Length && commandLine[end] is ' ' or '\t') end++;
        return commandLine[end..];
    }

    /// <summary>
    ///     The app's working directory: its own folder, never the caller's. Windows
    ///     searches the working directory for DLLs, so inheriting a Downloads folder
    ///     would let a file planted there load into the app.
    /// </summary>
    public static string WorkingDirectory(string appExePath)
        => Path.GetDirectoryName(appExePath) ?? throw new ArgumentException("No folder in the app path.", nameof(appExePath));

    /// <summary>The command line the app is started with: its quoted path, then the tail.</summary>
    public static string ForApp(string appExePath, string argumentTail)
        => argumentTail.Length == 0 ? $"\"{appExePath}\"" : $"\"{appExePath}\" {argumentTail}";

    /// <summary>The name of a fallback failure log in the temp folder for <paramref name="token" />.</summary>
    public static string FallbackFailureLogName(string token) => $"dotcharter-launcher-{token}.log";

    /// <summary>
    ///     Writes <paramref name="line" /> to the failure log and returns the file written, or
    ///     null when nowhere took it. First choice is <see cref="FailureLogName" /> beside the
    ///     launcher (appended to). When that folder is read-only the note goes to
    ///     <paramref name="tempDirectory" /> in a file whose name carries a random token and
    ///     which is created exclusively (<see cref="FileMode.CreateNew" />): there is no fixed,
    ///     guessable path under the temp folder for anyone to pre-create or point elsewhere,
    ///     and a file that already sits on the chosen name is never opened.
    ///     Self-contained on purpose: the launcher references nothing first-party.
    /// </summary>
    public static string? TryWriteFailureLog(
        string launcherDirectory, string tempDirectory, string line, Func<string>? token = null)
    {
        try
        {
            Directory.CreateDirectory(launcherDirectory);
            var beside = Path.Combine(launcherDirectory, FailureLogName);
            File.AppendAllText(beside, line);
            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Read-only install folder: fall through to the temp folder.
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var name = FallbackFailureLogName(token?.Invoke() ?? Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(tempDirectory, name);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(line);
                return path;
            }
            catch (IOException) when (File.Exists(Path.Combine(tempDirectory, name)))
            {
                // That name is taken (guessed, or planted): never touch it, pick another.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The one line the failure log gets, so a user has something to send.</summary>
    public static string FailureLine(DateTime now, string appExePath, string reason)
        => $"{now:yyyy-MM-dd HH:mm:ss} DotCharter launcher: could not start '{appExePath}': {reason}. " +
           "The install is incomplete or damaged - extract the release zip again (the app lives in its app folder).";

    /// <summary>A nonzero exit within this many milliseconds of the start counts as a startup crash.</summary>
    public const int QuickExitMilliseconds = 5000;

    /// <summary>
    ///     The text of the one message box a failed start shows: the reason, the exit code
    ///     and where the log is (<paramref name="logPath" /> null when nothing took the note).
    /// </summary>
    public static string FailureMessage(string reason, int exitCode, string? logPath)
        => $"DotCharter could not start.{Environment.NewLine}{Environment.NewLine}" +
           $"Reason: {reason}{Environment.NewLine}Exit code: 0x{exitCode:X8}{Environment.NewLine}" +
           (logPath is null ? "No log file could be written." : $"Log: {logPath}");

    /// <summary>The message for an app that exited with a nonzero code right after it started.</summary>
    public static string QuickExitMessage(int exitCode)
        => $"DotCharter closed right after starting (exit code 0x{unchecked((uint)exitCode):X8}).{Environment.NewLine}{Environment.NewLine}" +
           "Its log folder (logs beside DotCharter.exe, or %APPDATA%\\DotCharter\\logs) says why.";

    /// <summary>A dialog is only for a person at a desktop: never when the caller redirected the standard handles or chose a desktop (scripted and hidden-desktop launches).</summary>
    public static bool ShouldShowDialog(bool standardHandlesRedirected, bool desktopChosen)
        => !standardHandlesRedirected && !desktopChosen;

    /// <summary>A nonzero exit this soon after the start is a startup crash worth telling the user about.</summary>
    public static bool IsQuickCrash(int exitCode, long elapsedMilliseconds)
        => exitCode != 0 && elapsedMilliseconds < QuickExitMilliseconds;

    /// <summary>
    ///     Writes the failure note, then shows the message through <paramref name="show" />
    ///     when <paramref name="showDialog" /> is true. The reporter is a seam so the tests
    ///     never open a real dialog. Returns <paramref name="exitCode" />.
    /// </summary>
    public static int ReportFailure(
        string launcherDirectory, string tempDirectory, string appExePath, string reason, int exitCode,
        bool showDialog, Action<string> show, DateTime? now = null)
    {
        var line = FailureLine(now ?? DateTime.Now, appExePath, reason) + Environment.NewLine;
        var logPath = TryWriteFailureLog(launcherDirectory, tempDirectory, line);
        if (showDialog) show(FailureMessage(reason, exitCode, logPath));
        return exitCode;
    }

    /// <summary>
    ///     The missing-app failure path. It takes the caller's STARTUPINFO flags, not a
    ///     decided policy, so it cannot run before they were read: a redirected or
    ///     hidden-desktop launch writes the note and shows nothing. Null when the app is there.
    /// </summary>
    public static int? ReportIfAppMissing(
        string launcherDirectory, string tempDirectory, string appExePath, bool appExists,
        bool standardHandlesRedirected, bool desktopChosen, Action<string> show, DateTime? now = null)
        => appExists
            ? null
            : ReportFailure(launcherDirectory, tempDirectory, appExePath, "the file does not exist", MissingAppExitCode,
                ShouldShowDialog(standardHandlesRedirected, desktopChosen), show, now);
}
