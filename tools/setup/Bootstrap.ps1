<#
.SYNOPSIS
    Checks that this machine can build and run DotCharter, and prints what to do next.

.DESCRIPTION
    READ-ONLY. It never installs anything, never changes a system setting, never
    writes a file and never runs a git command that modifies the checkout. It looks at:

      * Windows and the .NET SDK the repository needs,
      * Git, and whether the JuceSharp submodule has been fetched,
      * a Vulkan loader (vulkan-1.dll),
      * whether juce_sharp.dll exists and reports the same DCA_ABI_VERSION as the
        header it was built from (it loads the DLL only to call dca_abi_version()).

    Then it prints the commands to run next. Exit code 0 when nothing blocks a
    build, 1 when something does.

    Full walkthrough: docs/dev/GETTING-STARTED.md

.EXAMPLE
    pwsh -NoProfile -File tools/setup/Bootstrap.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$blocking = 0

function Write-Check {
    param([string]$Status, [string]$Name, [string]$Detail)
    $tag = switch ($Status) { 'ok' { '[ ok ]' } 'warn' { '[warn]' } default { '[FAIL]' } }
    Write-Host ('{0} {1}: {2}' -f $tag, $Name, $Detail)
    if ($Status -eq 'fail') { $script:blocking++ }
}

Write-Host "DotCharter bootstrap check (read-only) - $repoRoot"
Write-Host ''

# --- Windows ---------------------------------------------------------------
$onWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'
if ($onWindows) { Write-Check ok 'Windows' ([Environment]::OSVersion.VersionString) }
else { Write-Check fail 'Windows' 'DotCharter is Windows-only (Windows 10/11 x64).' }

# --- .NET SDK --------------------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Check fail '.NET SDK' 'dotnet not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}
else {
    $sdkVersion = (& dotnet --version 2>$null | Select-Object -First 1)
    $major = 0
    if ($sdkVersion -match '^(\d+)\.') { $major = [int]$Matches[1] }
    if ($major -ge 10) { Write-Check ok '.NET SDK' $sdkVersion }
    else { Write-Check fail '.NET SDK' "found $sdkVersion, need 10 or newer (the repository targets .NET 10)." }
}

if (Test-Path (Join-Path $repoRoot 'global.json')) { Write-Check ok 'global.json' 'present (selects the Microsoft.Testing.Platform test runner).' }
else { Write-Check fail 'global.json' 'missing - is this the repository root?' }

# --- Git and the submodule -------------------------------------------------
if (Get-Command git -ErrorAction SilentlyContinue) { Write-Check ok 'Git' ((& git --version) -join ' ') }
else { Write-Check fail 'Git' 'git not found on PATH.' }

$header = Join-Path $repoRoot 'JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h'
$submoduleReady = Test-Path $header
if ($submoduleReady) { Write-Check ok 'JuceSharp submodule' 'fetched.' }
else { Write-Check fail 'JuceSharp submodule' 'empty. Run: git submodule update --init --recursive' }

# --- Vulkan ----------------------------------------------------------------
if ($onWindows) {
    $vulkan = Join-Path $env:SystemRoot 'System32\vulkan-1.dll'
    if (Test-Path $vulkan) { Write-Check ok 'Vulkan loader' $vulkan }
    else { Write-Check warn 'Vulkan loader' 'vulkan-1.dll not found. Update your GPU driver; the highway and timeline are drawn with Vulkan 1.2.' }
}

# --- Native engine DLL -----------------------------------------------------
$headerAbi = $null
if ($submoduleReady) {
    $m = Select-String -Path $header -Pattern '^\s*#define\s+DCA_ABI_VERSION\s+(\d+)' | Select-Object -First 1
    if ($m) { $headerAbi = [int]$m.Matches[0].Groups[1].Value }
}

$dllDir = Join-Path $repoRoot 'JuceSharp/src/JuceSharp/runtimes/win-x64/native'
$dll = Join-Path $dllDir 'juce_sharp.dll'
if (-not $submoduleReady) {
    Write-Check warn 'juce_sharp.dll' 'skipped until the submodule is fetched.'
}
elseif (-not (Test-Path $dll)) {
    Write-Check warn 'juce_sharp.dll' "not there yet ($dllDir). The first build downloads the prebuilt pair (or run: pwsh -NoProfile -File tools/setup/Get-NativeRuntime.ps1). See 'The native engine' in docs/dev/GETTING-STARTED.md."
}
else {
    $dllAbi = $null
    try {
        if (-not ('DotCharterBootstrap.Native' -as [type])) {
            Add-Type -Namespace DotCharterBootstrap -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr LoadLibraryW(string path);
[System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
public static extern System.IntPtr GetProcAddress(System.IntPtr module, string name);
'@
        }
        if (-not ('DotCharterBootstrapAbiFn' -as [type])) {
            Add-Type -TypeDefinition @'
[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
public delegate uint DotCharterBootstrapAbiFn();
'@
        }
        $module = [DotCharterBootstrap.Native]::LoadLibraryW($dll)
        if ($module -ne [IntPtr]::Zero) {
            $proc = [DotCharterBootstrap.Native]::GetProcAddress($module, 'dca_abi_version')
            if ($proc -ne [IntPtr]::Zero) {
                $fn = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($proc, [DotCharterBootstrapAbiFn])
                $dllAbi = [int]$fn.Invoke()
            }
        }
    }
    catch { $dllAbi = $null }

    if ($null -eq $dllAbi) {
        Write-Check warn 'juce_sharp.dll' 'exists, but its ABI version could not be read (missing runtime DLLs?). The app reports a mismatch on engine open.'
    }
    elseif ($dllAbi -eq $headerAbi) {
        Write-Check ok 'juce_sharp.dll' "ABI $dllAbi matches DCA_ABI_VERSION $headerAbi."
    }
    else {
        Write-Check fail 'juce_sharp.dll' "ABI $dllAbi but the header says DCA_ABI_VERSION $headerAbi - rebuild the DLL."
    }

    if (Test-Path (Join-Path $dllDir 'juce_vst_host.exe')) { Write-Check ok 'juce_vst_host.exe' 'present (the VST sandbox child; must be the matching build).' }
    else { Write-Check warn 'juce_vst_host.exe' 'not next to the DLL. Rebuilding the native engine deploys both.' }
}

# --- Optional --------------------------------------------------------------
if (Test-Path (Join-Path $repoRoot 'TestData')) { Write-Check ok 'TestData/' 'present (optional song corpus).' }
else { Write-Check ok 'TestData/' 'absent - fine. Tests that need the corpus skip themselves.' }

# --- Next steps ------------------------------------------------------------
Write-Host ''
if ($blocking -gt 0) { Write-Host "$blocking blocking problem(s) above. Fix them, then run this script again." }
else { Write-Host 'Nothing blocks a build. Next:' }
Write-Host '  dotnet build DotCharter.sln --nologo -v:m'
Write-Host '  dotnet run --project DotCharter.UI\DotCharter.UI.csproj'
Write-Host '  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate        # default tests'
Write-Host '  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless    # UI tests, separate run'
Write-Host 'Walkthrough and troubleshooting: docs/dev/GETTING-STARTED.md'
if ($blocking -gt 0) { exit 1 }
exit 0
