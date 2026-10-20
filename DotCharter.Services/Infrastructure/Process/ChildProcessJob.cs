using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotCharter.Services.Infrastructure.Process;

/// <summary>
///     Guarantees that nothing DotCharter spawns outlives DotCharter.
///     <para>
///         <b>The problem.</b> The app shuts down through
///         <c>App.TerminateNow</c>, which calls <c>TerminateProcess</c> on itself
///         — that exists to dodge a VST3 teardown access violation, and it is not
///         negotiable. But <c>TerminateProcess</c> runs no managed code: no
///         <c>finally</c>, no <c>ProcessExit</c>, no <c>Dispose</c>. Every child
///         the app started — <c>juce_vst_host.exe</c>, ffmpeg, yt-dlp, demucs,
///         the video encoder — is simply orphaned, and an orphaned audio host
///         keeps the sound device. This repo has already lost 32 hours of a
///         machine's audio to exactly that failure with a stray test host.
///     </para>
///     <para>
///         <b>The fix, and why it needs no change at any spawn site.</b> A Windows
///         child process INHERITS its parent's job object. So assigning the app
///         itself to one job with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes
///         every descendant a member automatically — including the ones spawned
///         from native C++, which managed cleanup could never have reached. When
///         the app dies for ANY reason (clean exit, crash, TerminateProcess, Task
///         Manager), the kernel closes the last job handle and terminates every
///         member atomically. Nothing has to remember to register.
///     </para>
///     <para>
///         <b>The updater is the one child that MUST survive</b> — it copies the
///         new build over the install dir after the app exits. So the job is
///         created with <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> and the updater is
///         launched through <see cref="StartDetached" /> with
///         <c>CREATE_BREAKAWAY_FROM_JOB</c>. **Containment is only ever enabled
///         when breakaway is also available** (see
///         <see cref="EnsureCurrentProcessIsContained" />): a job we could not set
///         BREAKAWAY_OK on would silently kill every future self-update, which is
///         a worse bug than the one being fixed.
///     </para>
/// </summary>
public static partial class ChildProcessJob
{
    private static readonly Lock Gate = new();
    private static bool _attempted;

    /// <summary>True when the app is inside a kill-on-close job, so children cannot outlive it.</summary>
    public static bool IsContained { get; private set; }

    /// <summary>Why containment is off, for the diagnostics line. Empty when contained.</summary>
    public static string Status { get; private set; } = "not attempted";

    /// <summary>
    ///     Puts this process — and therefore everything it spawns from now on — into
    ///     a kill-on-close job. Idempotent, Windows-only, and never throws: failing
    ///     to contain leaves exactly today's behaviour, so this can be called
    ///     unconditionally on the startup path.
    /// </summary>
    public static bool EnsureCurrentProcessIsContained(IAppLogger? logger = null)
    {
        lock (Gate)
        {
            if (_attempted) return IsContained;
            _attempted = true;

            if (!OperatingSystem.IsWindows())
            {
                Status = "not Windows";
                return false;
            }

            try
            {
                IsContained = Contain(out var status);
                Status = status;
            }
            catch (Exception ex)
            {
                Status = "failed: " + ex.Message;
                IsContained = false;
            }

            if (IsContained)
            {
                logger?.Info(AppLogCategory.Application,
                    "Child processes are contained in a job object; none can outlive the app.");
            }
            else
            {
                logger?.Warning(AppLogCategory.Application,
                    $"Child-process containment is OFF ({Status}). A crash may orphan ffmpeg/VST host processes.");
            }

            return IsContained;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Contain(out string status)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            status = "CreateJobObject failed";
            return false;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation =
            {
                // KillOnJobClose is the containment; BreakawayOk is what keeps the
                // updater launchable. Both or neither -- see the type summary.
                LimitFlags = JobLimit.KillOnJobClose | JobLimit.BreakawayOk,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                CloseHandle(job);
                status = "SetInformationJobObject failed";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            // Typically because we are already inside a job that forbids nesting
            // (some CI agents, some debuggers). Today's behaviour, no worse.
            CloseHandle(job);
            status = $"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})";
            return false;
        }

        // The handle is deliberately LEAKED for the process lifetime: the job dies
        // -- and takes its members with it -- when the last handle closes, which is
        // exactly what process exit should do. Closing it here would disband the
        // job immediately and kill us.
        status = string.Empty;
        return true;
    }

    /// <summary>
    ///     Starts a process that must OUTLIVE this one — today only the update
    ///     script. Breaks out of the job so it is not killed when the app exits,
    ///     and runs window-less.
    ///     <para>
    ///         Falls back to an ordinary <see cref="System.Diagnostics.Process" />
    ///         start when containment is off, in which case there is no job to
    ///         break out of and the plain start already survives.
    ///     </para>
    /// </summary>
    /// <returns>The child's process id, or 0 when it is not knowable.</returns>
    /// <exception cref="Win32Exception">The process could not be started at all.</exception>
    public static int StartDetached(string fileName, string arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!IsContained || !OperatingSystem.IsWindows())
        {
            // CreateNoWindow WITHOUT a console detach, for the reason spelled out
            // on UpdaterCreationFlags: this starts cmd.exe, and a console-less
            // cmd.exe deadlocks on the first pipeline in the script.
            using var plain = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return plain?.Id ?? 0;
        }

        return StartBrokenOut(fileName, arguments, workingDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static int StartBrokenOut(string fileName, string arguments, string workingDirectory)
    {
        var startup = new Startupinfo { cb = Marshal.SizeOf<Startupinfo>() };

        // CreateProcess MAY modify the command line in place, so it must be a
        // writable buffer; the exe goes in lpApplicationName so a quoted path
        // cannot be mis-parsed.
        var commandLine = new char[arguments.Length + 1];
        arguments.CopyTo(0, commandLine, 0, arguments.Length);

        if (!CreateProcessW(
                fileName, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                UpdaterCreationFlags,
                IntPtr.Zero, workingDirectory, ref startup, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not start '{fileName}' outside the job object.");
        }

        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        return info.dwProcessId;
    }

    // ---- interop ----------------------------------------------------------

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint CreateBreakawayFromJob = 0x0100_0000;
    private const uint CreateNoWindow = 0x0800_0000;

    /// <summary>
    ///     <b>Never launch the updater with this.</b> <c>DETACHED_PROCESS</c>
    ///     gives the child NO console at all — and a console-less
    ///     <c>cmd.exe</c> running a batch file <b>deadlocks on the first
    ///     pipeline</b>: it hangs at 0 % CPU, forever, with no error and no exit
    ///     code. Measured on Windows 11 26200, 3/3 runs, against the exact
    ///     command line <c>GitHubUpdateService</c> uses; the same script under
    ///     <see cref="UpdaterCreationFlags" /> ran to completion every time.
    ///     <para>
    ///         That is the whole "self-update silently does nothing" bug: the
    ///         updater script's very first statement is a pipeline
    ///         (<c>tasklist … | findstr …</c>), so every update since this flag
    ///         was introduced staged its payload, exited the app and then hung
    ///         before copying a single byte. Kept as a named constant so the
    ///         reason survives, and pinned by
    ///         <c>UpdaterBreakawayGuardTests.TheUpdaterIsNeverLaunchedWithoutAConsole</c>.
    ///     </para>
    /// </summary>
    internal const uint DetachedProcess = 0x0000_0008;

    /// <summary>
    ///     Creation flags for the one child that must outlive the app.
    ///     <c>CREATE_NO_WINDOW</c> already gives a console application a console
    ///     with no visible window, which is all "run hidden" ever needed —
    ///     see <see cref="DetachedProcess" /> for what adding the detach cost.
    /// </summary>
    internal const uint UpdaterCreationFlags = CreateBreakawayFromJob | CreateNoWindow;

    [Flags]
    private enum JobLimit : uint
    {
        BreakawayOk = 0x0000_0800,
        KillOnJobClose = 0x0000_2000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobLimit LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Startupinfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref Startupinfo startupInfo,
        out ProcessInformation processInformation);
}
