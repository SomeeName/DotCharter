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

    /// <summary>Written beside the launcher (or in %TEMP%) when it cannot start the app.</summary>
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

    /// <summary>The command line the app is started with: its quoted path, then the tail.</summary>
    public static string ForApp(string appExePath, string argumentTail)
        => argumentTail.Length == 0 ? $"\"{appExePath}\"" : $"\"{appExePath}\" {argumentTail}";

    /// <summary>The one line the failure log gets, so a user has something to send.</summary>
    public static string FailureLine(DateTime now, string appExePath, string reason)
        => $"{now:yyyy-MM-dd HH:mm:ss} DotCharter launcher: could not start '{appExePath}': {reason}. " +
           "The install is incomplete or damaged - extract the release zip again (the app lives in its app folder).";
}
