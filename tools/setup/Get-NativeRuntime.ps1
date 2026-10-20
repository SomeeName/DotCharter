<#
.SYNOPSIS
    Installs the prebuilt juce_sharp.dll + juce_vst_host.exe pair for the
    JuceSharp commit this checkout records, so no C++ toolchain is needed (#832).

.DESCRIPTION
    Does nothing when JuceSharp/src/JuceSharp/runtimes/win-x64/native/juce_sharp.dll
    already exists (pass -Force to replace it). Otherwise it reads the gitlink
    (git ls-tree HEAD JuceSharp), downloads juce-sharp-native-win-x64-<sha12>.zip
    from the release named in tools/setup/native-runtime.json, checks each file's
    SHA-256 against that manifest and the DLL's dca_abi_version() against
    DCA_ABI_VERSION in the header, and only then installs the pair.

    The build calls this (tools/setup/NativeRuntime.targets) when the DLL is
    missing. Building native yourself? Set DotCharterSkipNativeFetch=true.
    DOTCHARTER_NATIVE_ASSET_DIR=<folder> installs a zip from that folder instead
    of downloading it.

.EXAMPLE
    pwsh -NoProfile -File tools/setup/Get-NativeRuntime.ps1
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
Import-Module (Join-Path $PSScriptRoot 'NativeRuntime.psm1') -Force
if (-not $Destination) { $Destination = Join-Path $repoRoot 'JuceSharp/src/JuceSharp/runtimes/win-x64/native' }

if ((Test-Path -LiteralPath (Join-Path $Destination 'juce_sharp.dll')) -and -not $Force) {
    Write-Host "juce_sharp.dll is already in $Destination; nothing to do (-Force replaces it)."
    exit 0
}

try {
    $commit = Get-JuceSharpGitlink $repoRoot
    $headerAbi = Get-HeaderAbiVersion (Join-Path $repoRoot 'JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h')
    Write-Host "Fetching the prebuilt native runtime for JuceSharp $commit (ABI $headerAbi)..."
    $result = Install-NativeRuntime -Commit $commit -ManifestPath (Join-Path $PSScriptRoot 'native-runtime.json') `
        -HeaderAbi $headerAbi -Destination $Destination
    Write-Host "Installed $($result.Asset) (SHA-256 and ABI $($result.Abi) verified) into $Destination."
    exit 0
}
catch {
    # Canonical MSBuild error format, so the build reports it as an error.
    $message = ($_.Exception.Message -replace '\s*\r?\n\s*', ' ')
    Write-Host "tools/setup/Get-NativeRuntime.ps1 : error DCNATIVE: $message"
    Write-Host 'Building the native engine yourself? Set DotCharterSkipNativeFetch=true (docs/dev/GETTING-STARTED.md).'
    exit 1
}
