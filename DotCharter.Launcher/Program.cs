using System.Runtime.InteropServices;

namespace DotCharter.Launcher;

/// <summary>
///     Starts <c>app\DotCharter.exe</c> in its own folder with this process's own
///     command line, desktop, show state and standard handles, waits for it
///     and exits with its exit code. A failed start is one line in
///     <see cref="LaunchCommandLine.FailureLogName" /> plus one native message box,
///     unless the caller redirected the standard handles or chose a desktop.
/// </summary>
internal static unsafe partial class Program
{
    private const uint Infinite = 0xFFFFFFFF;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;

    private static int Main()
    {
        var launcherDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var appExe = LaunchCommandLine.AppExePath(launcherDirectory);

        // The caller's STARTUPINFO is passed on as it is: the desktop it chose
        // (a hidden one included), "run minimised" from a shortcut, and
        // redirected standard handles all reach the app as if it had been
        // started directly. Handles are inherited only when they were redirected.
        // Read before any failure path: a silent launch must never get a dialog,
        // not even for a missing app.
        StartupInfo startup;
        GetStartupInfoW(&startup);
        startup.Cb = sizeof(StartupInfo);
        var inheritHandles = (startup.Flags & StartfUseStdHandles) != 0;
        var desktopChosen = startup.Desktop != 0;
        var showDialog = LaunchCommandLine.ShouldShowDialog(inheritHandles, desktopChosen);

        var missing = LaunchCommandLine.ReportIfAppMissing(launcherDirectory, Path.GetTempPath(), appExe, File.Exists(appExe),
            inheritHandles, desktopChosen, ShowMessage);
        if (missing is { } missingExitCode) return missingExitCode;

        var commandLine = LaunchCommandLine.ForApp(appExe,
            LaunchCommandLine.ArgumentTail(Marshal.PtrToStringUni(GetCommandLineW())));

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
                    LaunchCommandLine.StartFailedExitCode, showDialog);
            }
        }

        CloseHandle(info.Thread);
        var started = Environment.TickCount64;
        WaitForSingleObject(info.Process, Infinite);
        var exitCode = 0u;
        GetExitCodeProcess(info.Process, &exitCode);
        CloseHandle(info.Process);
        var code = unchecked((int)exitCode);
        if (showDialog && LaunchCommandLine.IsQuickCrash(code, Environment.TickCount64 - started))
            ShowMessage(LaunchCommandLine.QuickExitMessage(code));
        return code;
    }

    private static int Fail(string launcherDirectory, string appExe, string reason, int exitCode, bool showDialog)
        => LaunchCommandLine.ReportFailure(
            launcherDirectory, Path.GetTempPath(), appExe, reason, exitCode, showDialog, ShowMessage);

    private static void ShowMessage(string text)
    {
        fixed (char* body = text)
        fixed (char* title = "DotCharter")
            MessageBoxW(0, body, title, MbOk | MbIconError);
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

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW")]
    private static partial int MessageBoxW(nint window, char* text, char* caption, uint type);
}
