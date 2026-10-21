using System.Runtime.InteropServices;

namespace DotCharter.Launcher;

/// <summary>
///     Starts <c>app\DotCharter.exe</c> in its own folder with this process's own
///     command line, desktop, show state and standard handles, waits for it
///     and exits with its exit code. It never shows a window or a message box: a
///     failure is one line in <see cref="LaunchCommandLine.FailureLogName" />.
/// </summary>
internal static unsafe partial class Program
{
    private const uint Infinite = 0xFFFFFFFF;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private static int Main()
    {
        var launcherDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var appExe = LaunchCommandLine.AppExePath(launcherDirectory);
        if (!File.Exists(appExe))
            return Fail(launcherDirectory, appExe, "the file does not exist", LaunchCommandLine.MissingAppExitCode);

        var commandLine = LaunchCommandLine.ForApp(appExe,
            LaunchCommandLine.ArgumentTail(Marshal.PtrToStringUni(GetCommandLineW())));

        // The caller's STARTUPINFO is passed on as it is: the desktop it chose
        // (a hidden one included), "run minimised" from a shortcut, and
        // redirected standard handles all reach the app as if it had been
        // started directly. Handles are inherited only when they were redirected.
        StartupInfo startup;
        GetStartupInfoW(&startup);
        startup.Cb = sizeof(StartupInfo);
        var inheritHandles = (startup.Flags & StartfUseStdHandles) != 0;

        // The app's own folder, never the caller's: Windows searches the working
        // directory for DLLs, so a launch from a Downloads folder would otherwise
        // let a DLL planted there load into the app.
        var workingDirectory = LaunchCommandLine.WorkingDirectory(appExe);
        var buffer = (commandLine + "\0").ToCharArray();
        ProcessInformation info;
        fixed (char* application = appExe)
        fixed (char* command = buffer)
        fixed (char* directory = workingDirectory)
        {
            if (CreateProcessW(application, command, null, null, inheritHandles ? 1 : 0, CreateUnicodeEnvironment,
                    null, directory, &startup, &info) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return Fail(launcherDirectory, appExe, $"CreateProcess failed with Win32 error {error}",
                    LaunchCommandLine.StartFailedExitCode);
            }
        }

        CloseHandle(info.Thread);
        WaitForSingleObject(info.Process, Infinite);
        var exitCode = 0u;
        GetExitCodeProcess(info.Process, &exitCode);
        CloseHandle(info.Process);
        return unchecked((int)exitCode);
    }

    private static int Fail(string launcherDirectory, string appExe, string reason, int exitCode)
    {
        var line = LaunchCommandLine.FailureLine(DateTime.Now, appExe, reason) + Environment.NewLine;
        LaunchCommandLine.TryWriteFailureLog(launcherDirectory, Path.GetTempPath(), line);
        return exitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCommandLineW();

    [LibraryImport("kernel32.dll")]
    private static partial void GetStartupInfoW(StartupInfo* startupInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreateProcessW(char* applicationName, char* commandLine, void* processAttributes,
        void* threadAttributes, int inheritHandles, uint creationFlags, void* environment, char* currentDirectory,
        StartupInfo* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static partial int GetExitCodeProcess(nint process, uint* exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
