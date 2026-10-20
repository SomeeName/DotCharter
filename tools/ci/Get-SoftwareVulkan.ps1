<#
.SYNOPSIS
    Downloads the pinned software Vulkan ICD (Mesa lavapipe) for the headless
    GPU golden-frame tests, verifies its SHA-256 and unpacks it OUTSIDE the repo.

.DESCRIPTION
    The software lane of DotCharter.Tests/Rendering/Headless (#315):
    DOTCHARTER_GPU_TESTS=sw points the Vulkan loader of the TEST PROCESS ONLY at
    this ICD (VK_DRIVER_FILES / VK_ICD_FILENAMES), so the golden frames render
    on the CPU on any box, a GPU-less CI runner included.

    Nothing is installed: no registry key, no system driver, no PATH change. The
    archive is a test tool and is never shipped (THIRD-PARTY-NOTICES.md 2.10,
    licenses/dependencies.json).

    Source: Mesa lavapipe, built for Windows x64 by jakoch/rasterizers (the
    builds jakoch/install-vulkan-sdk-action installs). The hash is pinned here;
    a download whose SHA-256 differs is deleted and the script fails before
    anything is unpacked.

    Prints the ICD manifest path (the JSON the loader reads) on success.

.PARAMETER Destination
    Where to unpack. Default: %LOCALAPPDATA%\DotCharter\swvk\<Version>, which
    is also where the tests look when DOTCHARTER_SWVK_ICD is not set.

.PARAMETER ArchivePath
    Use an already-downloaded archive instead of fetching -Url (a CI cache, or
    an offline box). It is hash-checked exactly like a download.

.PARAMETER ExpectedSha256
    The pinned hash. Override only to prove the check fails on a wrong one.

.EXAMPLE
    pwsh -NoProfile -File tools/ci/Get-SoftwareVulkan.ps1
    $env:DOTCHARTER_GPU_TESTS = 'sw'   # then run the Rendering/Headless classes

.OUTPUTS
    The full path of the ICD manifest (lvp_icd.x86_64.json, written by this
    script next to bin\vulkan_lvp.dll). Exit 0 on
    success, 1 on a hash mismatch or a malformed archive.
#>
[CmdletBinding()]
param(
    [string]$Version = 'lavapipe-x64-26.2.4-20261006',
    [string]$Url = 'https://github.com/jakoch/rasterizers/releases/download/20261006/lavapipe-x64-26.2.4.zip',
    [string]$ExpectedSha256 = 'dd6d69c0df698bacf6633711912e8eb1cf6e70b2796d46240c7ee3708e27a438',
    [string]$Destination,
    [string]$ArchivePath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Destination) {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [IO.Path]::GetTempPath() }
    $Destination = Join-Path $base "DotCharter\swvk\$Version"
}

$marker = Join-Path $Destination '.sha256'

# The archive ships the driver DLL but an EMPTY share/vulkan/icd.d, so the
# manifest the loader reads is written here, next to it, with an absolute
# library_path. It is derived from the hash-checked DLL, never downloaded.
$ManifestName = 'lvp_icd.x86_64.json'
$DriverRelative = 'bin\vulkan_lvp.dll'

# .NET calls rather than Get-FileHash / Expand-Archive / Invoke-WebRequest:
# those live in modules that fail to autoload when Windows PowerShell 5.1 is
# started from a pwsh 7 process (it inherits pwsh's PSModulePath).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Find-IcdManifest([string]$root) {
    $json = Join-Path $root $ManifestName
    if ((Test-Path -LiteralPath $json) -and (Test-Path -LiteralPath (Join-Path $root $DriverRelative))) { return $json }
    return $null
}

function Write-IcdManifest([string]$root) {
    $manifest = [ordered]@{
        file_format_version = '1.0.1'
        ICD = [ordered]@{
            library_path = (Join-Path $root $DriverRelative)
            api_version = '1.4.0'
            is_portability_driver = $false
        }
    }
    $json = Join-Path $root $ManifestName
    [IO.File]::WriteAllText($json, ($manifest | ConvertTo-Json -Depth 4) + "`n")
}

# Already unpacked from an archive with this exact hash: nothing to do.
if (-not $Force -and (Test-Path -LiteralPath $marker) -and
    ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $ExpectedSha256.ToLowerInvariant())) {
    $existing = Find-IcdManifest $Destination
    if ($existing) {
        Write-Output $existing
        exit 0
    }
}

# Download into its OWN fresh directory, never into the repo.
$work = Join-Path ([IO.Path]::GetTempPath()) ("dotcharter-swvk-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($ArchivePath) {
        $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    }
    else {
        $archive = Join-Path $work 'swvk.zip'
        Write-Host "Downloading $Url"
        $client = New-Object System.Net.WebClient
        try { $client.DownloadFile($Url, $archive) } finally { $client.Dispose() }
    }

    $actual = Get-Sha256 $archive
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        Write-Error -ErrorAction Continue "SHA-256 mismatch for $archive`n  expected $($ExpectedSha256.ToLowerInvariant())`n  actual   $actual`nNothing was unpacked."
        exit 1
    }

    $unpack = Join-Path $work 'unpacked'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpack)
    if (-not (Test-Path -LiteralPath (Join-Path $unpack $DriverRelative))) {
        Write-Error -ErrorAction Continue "The archive holds no $DriverRelative; is -Url the lavapipe build?"
        exit 1
    }

    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Move-Item -LiteralPath $unpack -Destination $Destination
    Write-IcdManifest $Destination
    Set-Content -LiteralPath $marker -Value $actual -NoNewline -Encoding ascii

    Write-Output (Find-IcdManifest $Destination)
    exit 0
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
