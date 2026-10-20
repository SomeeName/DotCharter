# Starts a program on a separate, never-shown Win32 desktop (CreateDesktop +
# STARTUPINFO.lpDesktop) so it can NEVER take the foreground (#419). A minimised
# or no-activate launch is not enough: DotCharter activates its own window when
# it shows, and 9 of 42 such launches on 2026-10-07 ended in the foreground.
#
# Every scripted launch of the app under tools/ goes through
# Start-HiddenDesktopProcess (Infrastructure/ScriptedAppLaunchLintTests). The
# child starts suspended at idle priority, gets the affinity mask (default CPUs
# 8-15, intersected with what the machine has), then resumes. The launcher in
# DotCharter.exe passes its STARTUPINFO on, so app\DotCharter.exe lands on the
# same hidden desktop.
#
# The returned handle HOLDS the desktop until Stop-HiddenDesktopProcess: closing
# it before the child has connected destroys the desktop and the child dies in
# user32 initialisation. Environment variables of this PowerShell process are
# inherited by the child.
Set-StrictMode -Version Latest

if (-not ('DotCharter.Tools.HiddenDesktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCharter.Tools {
    public sealed class HiddenDesktopProcess : IDisposable {
        internal IntPtr DesktopHandle;
        public string DesktopName { get; internal set; }
        public Process Process { get; internal set; }
        public int Id { get { return Process.Id; } }
        public bool DesktopHeld { get { return DesktopHandle != IntPtr.Zero; } }
        public void Dispose() {
            if (DesktopHandle == IntPtr.Zero) return;
            HiddenDesktop.CloseDesktop(DesktopHandle);
            DesktopHandle = IntPtr.Zero;
        }
    }

    public static class HiddenDesktop {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(int threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder info, int length, out int needed);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
        [DllImport("kernel32.dll")]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);
        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();
        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint GenericAll = 0x10000000;
        private const uint CreateSuspended = 0x00000004, IdlePriorityClass = 0x00000040;
        private const uint CreateUnicodeEnvironment = 0x00000400, CreateNoWindow = 0x08000000;
        private const int StartfUseStdHandles = 0x00000100, HandleFlagInherit = 0x1;

        public static HiddenDesktopProcess Start(string exe, string arguments, string workingDirectory,
            string desktopName, string stdoutPath, string stderrPath, bool idlePriority, long affinityMask) {
            var desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
            if (desktop == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop(" + desktopName + ") failed");
            FileStream stdout = null, stderr = null;
            var handedOver = false;
            try {
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.lpDesktop = desktopName;
                var inherit = false;
                if (stdoutPath != null || stderrPath != null) {
                    stdout = Inheritable(stdoutPath);
                    stderr = Inheritable(stderrPath);
                    si.dwFlags |= StartfUseStdHandles;
                    si.hStdOutput = stdout == null ? IntPtr.Zero : stdout.SafeFileHandle.DangerousGetHandle();
                    si.hStdError = stderr == null ? IntPtr.Zero : stderr.SafeFileHandle.DangerousGetHandle();
                    inherit = true;
                }
                var flags = CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow | (idlePriority ? IdlePriorityClass : 0u);
                var command = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments));
                PROCESS_INFORMATION pi;
                if (!CreateProcessW(exe, command, IntPtr.Zero, IntPtr.Zero, inherit, flags, IntPtr.Zero,
                        workingDirectory, ref si, out pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess(" + exe + ") failed");
                try {
                    UIntPtr processMask, systemMask;
                    if (affinityMask != 0 && GetProcessAffinityMask(pi.hProcess, out processMask, out systemMask)) {
                        var mask = (ulong)affinityMask & systemMask.ToUInt64();
                        if (mask != 0) SetProcessAffinityMask(pi.hProcess, new UIntPtr(mask));
                    }
                    // Opened while the child is still suspended, so its exit code
                    // stays readable however quickly it dies.
                    var process = Process.GetProcessById(pi.dwProcessId);
                    var unused = process.Handle;
                    ResumeThread(pi.hThread);
                    handedOver = true;
                    return new HiddenDesktopProcess { DesktopHandle = desktop, DesktopName = desktopName, Process = process };
                }
                catch {
                    TerminateProcess(pi.hProcess, 1);
                    throw;
                }
                finally {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
            }
            finally {
                if (stdout != null) stdout.Dispose();
                if (stderr != null) stderr.Dispose();
                if (!handedOver) CloseDesktop(desktop);
            }
        }

        private static FileStream Inheritable(string path) {
            if (path == null) return null;
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (!SetHandleInformation(stream.SafeFileHandle.DangerousGetHandle(), HandleFlagInherit, HandleFlagInherit)) {
                var error = Marshal.GetLastWin32Error();
                stream.Dispose();
                throw new Win32Exception(error, "SetHandleInformation(" + path + ") failed");
            }
            return stream;
        }

        /// <summary>The name of the desktop the calling thread runs on ("Default" on a normal launch).</summary>
        public static string ThreadDesktopName() {
            var name = new StringBuilder(256);
            int needed;
            const int UoiName = 2;
            return GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UoiName, name, name.Capacity * 2, out needed)
                ? name.ToString() : null;
        }

        public static long ForegroundWindow() { return GetForegroundWindow().ToInt64(); }

        /// <summary>The process that owns the foreground window, or 0 when there is none.</summary>
        public static int ForegroundProcessId() {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return 0;
            int processId;
            GetWindowThreadProcessId(window, out processId);
            return processId;
        }
    }
}
'@
}

function Start-HiddenDesktopProcess {
    [CmdletBinding()]
    [OutputType([DotCharter.Tools.HiddenDesktopProcess])]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string]$ArgumentList = '',
        [string]$WorkingDirectory,
        [string]$RedirectStandardOutput,
        [string]$RedirectStandardError,
        [string]$DesktopName = ('dotcharter-hidden-' + [guid]::NewGuid().ToString('N').Substring(0, 12)),
        # Normal priority instead of idle (idle is the default: the owner may be gaming).
        [switch]$NormalPriority,
        # CPUs the child may use, intersected with the machine's; 0 = leave it alone.
        [long]$AffinityMask = 0xFF00
    )
    $exe = (Resolve-Path -LiteralPath $FilePath).Path
    if (-not $WorkingDirectory) { $WorkingDirectory = Split-Path $exe }
    $out = if ($RedirectStandardOutput) { [System.IO.Path]::GetFullPath($RedirectStandardOutput) } else { $null }
    $err = if ($RedirectStandardError) { [System.IO.Path]::GetFullPath($RedirectStandardError) } else { $null }
    [DotCharter.Tools.HiddenDesktop]::Start($exe, $ArgumentList, $WorkingDirectory, $DesktopName, $out, $err,
        -not $NormalPriority, $AffinityMask)
}

# Kills the started process and everything it spawned (unless it has exited),
# then releases the desktop. Safe to call twice.
function Stop-HiddenDesktopProcess {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()][DotCharter.Tools.HiddenDesktopProcess]$Launch, [int]$TimeoutMs = 15000)
    if (-not $Launch) { return }
    try {
        if (-not $Launch.Process.HasExited) {
            & taskkill.exe /PID $Launch.Id /T /F *> $null
            $global:LASTEXITCODE = 0
            if (-not $Launch.Process.WaitForExit($TimeoutMs)) {
                Write-Warning "pid $($Launch.Id) on desktop $($Launch.DesktopName) did not exit after taskkill."
            }
        }
    }
    finally { $Launch.Dispose() }
}

# The processes named $Name whose parent is $ParentId (the app a launcher started).
function Get-ChildProcess {
    param([Parameter(Mandatory)][int]$ParentId, [string]$Name = 'DotCharter.exe')
    foreach ($child in Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentId AND Name = '$Name'" -ErrorAction SilentlyContinue) {
        $process = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            $null = $process.Handle   # keep the exit code readable
            $process
        }
    }
}

Export-ModuleMember -Function Start-HiddenDesktopProcess, Stop-HiddenDesktopProcess, Get-ChildProcess
