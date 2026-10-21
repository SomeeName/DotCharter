<#
.SYNOPSIS
    Builds DotCharter's minimal LGPL ffmpeg.exe from pinned, hash-checked sources.

.DESCRIPTION
    The app runs ffmpeg for a known set of jobs (tools/release/ffmpeg-min/components.json
    lists them and the components each needs). This script builds an ffmpeg with
    exactly those components, statically linked, LGPL v2.1+ (no --enable-gpl):

      ffmpeg + libogg + libvorbis + opus + lame + zlib, each a pinned tarball
      checked against its SHA-256 below, compiled with MSYS2's MINGW64 gcc.

    Each pin lists its mirrors in order, all held to the SAME SHA-256
    (PinnedDownload.psm1): a mirror serving other bytes is skipped, the log
    names the mirror that served each file, and only verified bytes are cached.

    MSYS2 itself is a pinned base archive (URL + SHA-256), unpacked into -WorkRoot
    (outside the repo) the first time. Its toolchain (gcc/make/nasm/pkgconf and
    their whole dependency closure) is pinned the same way: every package file is
    named with its SHA-256 in ffmpeg-min/msys2-packages.json, downloaded through
    Save-PinnedFile and installed with `pacman -U` from the verified file. pacman
    never syncs (-S), so nothing the repository serves today is installed, and
    the build stops unless the installed set is exactly the pins. Nothing is
    installed system-wide.

    Output in -OutDir: ffmpeg.exe, ffmpeg-LICENSE.txt (LGPL v2.1),
    ffmpeg-BUILDINFO.txt. The script fails unless the built exe lists every
    component in the inventory (FfmpegInventory.psm1, the same check
    New-Release.ps1 runs in preflight).

    A first build takes ~10 minutes at -Jobs 4; it runs at low priority.

.PARAMETER OutDir
    Where ffmpeg.exe and its licence/build-info files are written.

.PARAMETER WorkRoot
    Scratch folder for MSYS2, sources and the build. Default:
    %LOCALAPPDATA%\DotCharter-build\ffmpeg-min.

.PARAMETER Jobs
    make -j value. Default 4.

.PARAMETER AffinityMask
    Optional hex CPU mask for the build processes (e.g. FF00).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA 'DotCharter-build\ffmpeg-min'),
    [int]$Jobs = 4,
    [string]$AffinityMask
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'FfmpegInventory.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PinnedDownload.psm1') -Force
$inventory = Get-FfmpegInventory

# --- Pins (update deliberately; a new hash is a new review) -------------------
# Urls are mirrors tried in order under the one Sha256; a mirror is added only
# after it was seen serving exactly the pinned bytes. ffmpeg has no second
# source that serves its release tarball byte-identical.
$Msys2BaseUrls   = @(
    'https://github.com/msys2/msys2-installer/releases/download/2026-09-27/msys2-base-x86_64-20260927.sfx.exe'
    'https://repo.msys2.org/distrib/x86_64/msys2-base-x86_64-20260927.sfx.exe'
)
$Msys2BaseSha256 = 'ad336cccfda47758b5e15cda993fbba421115cb0b126697daef1ee4dfe37209f'
# The toolchain packages, each pinned by file and SHA-256. Re-pin all of them
# together (new files from a fresh MSYS2's /var/cache/pacman/pkg): a partial
# bump mixes package generations.
$Msys2Pins = Get-Content -LiteralPath (Join-Path (Join-Path $PSScriptRoot 'ffmpeg-min') 'msys2-packages.json') -Raw | ConvertFrom-Json
$Versions = [ordered]@{
    FFMPEG_VERSION = $inventory.ffmpegVersion
    OGG_VERSION    = '1.3.6'
    VORBIS_VERSION = '1.3.7'
    OPUS_VERSION   = '1.5.2'
    LAME_VERSION   = '3.100'
    ZLIB_VERSION   = '1.3.2'
}
$Sources = @(
    @{ File = "ffmpeg-$($Versions.FFMPEG_VERSION).tar.xz"; Sha256 = '7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3'; Urls = @(
        "https://ffmpeg.org/releases/ffmpeg-$($Versions.FFMPEG_VERSION).tar.xz") }
    @{ File = 'libogg-1.3.6.tar.xz';    Sha256 = '5c8253428e181840cd20d41f3ca16557a9cc04bad4a3d04cce84808677fa1061'; Urls = @(
        'https://downloads.xiph.org/releases/ogg/libogg-1.3.6.tar.xz'
        'https://github.com/xiph/ogg/releases/download/v1.3.6/libogg-1.3.6.tar.xz') }
    @{ File = 'libvorbis-1.3.7.tar.xz'; Sha256 = 'b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b'; Urls = @(
        'https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz'
        'https://github.com/xiph/vorbis/releases/download/v1.3.7/libvorbis-1.3.7.tar.xz') }
    @{ File = 'opus-1.5.2.tar.gz';      Sha256 = '65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1'; Urls = @(
        'https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz'
        'https://github.com/xiph/opus/releases/download/v1.5.2/opus-1.5.2.tar.gz') }
    # SourceForge served an HTML page for this file on 2026-10-08 (#449); Debian's orig tarball is the same bytes.
    @{ File = 'lame-3.100.tar.gz';      Sha256 = 'ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e'; Urls = @(
        'https://deb.debian.org/debian/pool/main/l/lame/lame_3.100.orig.tar.gz'
        'https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz') }
    @{ File = 'zlib-1.3.2.tar.gz';      Sha256 = 'bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16'; Urls = @(
        'https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz'
        'https://zlib.net/fossils/zlib-1.3.2.tar.gz') }
)

# A complete cached toolchain needs no package refresh to rebuild pinned sources.
function Test-MsysToolchain([string]$Root) {
    foreach ($tool in 'usr/bin/bash.exe', 'usr/bin/make.exe', 'usr/bin/diff.exe',
                        'mingw64/bin/gcc.exe', 'mingw64/bin/nasm.exe', 'mingw64/bin/pkg-config.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $tool) -PathType Leaf)) { return $false }
    }
    return $true
}

# The differences between the installed packages (`pacman -Q` lines, "name version")
# and the pins: a pin missing or at another version, and any MinGW package that
# is not pinned at all. Empty means the toolchain is exactly the pins.
function Compare-MsysPinnedPackages([string[]]$Installed, $Pins) {
    $have = @{}
    foreach ($line in $Installed) {
        $parts = "$line".Trim() -split '\s+'
        if ($parts.Count -ge 2) { $have[$parts[0]] = $parts[1] }
    }
    $pinned = @{}
    foreach ($pin in $Pins) { $pinned[$pin.name] = $pin.version }
    $problems = @()
    foreach ($name in $pinned.Keys | Sort-Object) {
        if (-not $have.ContainsKey($name)) { $problems += "$name is not installed (pinned $($pinned[$name]))" }
        elseif ($have[$name] -cne $pinned[$name]) { $problems += "$name is $($have[$name]), pinned $($pinned[$name])" }
    }
    foreach ($name in $have.Keys | Sort-Object) {
        if ($name -like 'mingw-w64-*' -and -not $pinned.ContainsKey($name)) { $problems += "$name $($have[$name]) is installed but not pinned" }
    }
    return $problems
}

# Launch without a console, retain diagnostics and check the actual child's exit code.
function Invoke-Low([string]$Exe, [string[]]$Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        try {
            $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle
            if ($AffinityMask) { $process.ProcessorAffinity = [IntPtr][Convert]::ToInt64($AffinityMask, 16) }
        }
        catch { if (-not $process.HasExited) { throw } }
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($output) { Write-Host ($output.TrimEnd()) }
        if ($errors) { Write-Host ($errors.TrimEnd()) }
        $global:LASTEXITCODE = $process.ExitCode
        if ($process.ExitCode -ne 0) { throw "'$Exe' exited with $($process.ExitCode)." }
    }
    catch {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        throw
    }
    finally { $process.Dispose() }
}

New-Item -ItemType Directory -Force -Path $WorkRoot, $OutDir | Out-Null
$dl = Join-Path $WorkRoot 'sources'
New-Item -ItemType Directory -Force -Path $dl | Out-Null

# --- MSYS2 (portable, inside WorkRoot) ---------------------------------------
$bash = Join-Path $WorkRoot 'msys64\usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash)) {
    Write-Host 'MSYS2 base'
    $sfx = Join-Path $dl 'msys2-base.sfx.exe'
    Save-PinnedFile -Urls $Msys2BaseUrls -Dest $sfx -Sha256 $Msys2BaseSha256 | Out-Null
    Invoke-Low $sfx @('-y', "-o$WorkRoot")
}
$env:MSYSTEM = 'MINGW64'
$env:CHERE_INVOKING = '1'
Write-Host 'MSYS2 toolchain'
$msysRoot = Join-Path $WorkRoot 'msys64'
function Get-MsysPinMismatches {
    $list = Join-Path $WorkRoot 'msys2-installed.txt'
    if (Test-Path -LiteralPath $list) { Remove-Item -LiteralPath $list -Force }
    Invoke-Low $bash @('-lc', "pacman -Q > `"`$(cygpath -u '$list')`"")
    return @(Compare-MsysPinnedPackages (Get-Content -LiteralPath $list) $Msys2Pins.packages)
}
if ((Test-MsysToolchain $msysRoot) -and (Get-MsysPinMismatches).Count -eq 0) {
    Write-Host 'Using the cached MSYS2 toolchain; it is exactly the pinned packages.'
}
else {
    $packageDir = Join-Path $dl 'msys2-packages'
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    foreach ($package in $Msys2Pins.packages) {
        Save-PinnedFile -Urls @($Msys2Pins.mirrors | ForEach-Object { "$_/$($package.repo)/$($package.file)" }) -Dest (Join-Path $packageDir $package.file) -Sha256 $package.sha256 | Out-Null
    }
    # Only the verified files: no -S, so pacman reads no repository database.
    $files = ($Msys2Pins.packages | ForEach-Object { "'$($_.file)'" }) -join ' '
    Invoke-Low $bash @('-lc', "cd `"`$(cygpath -u '$packageDir')`" && pacman -U --noconfirm --needed $files")
    if (-not (Test-MsysToolchain $msysRoot)) { throw 'MSYS2 did not install the required compiler tools.' }
    $mismatches = Get-MsysPinMismatches
    if ($mismatches.Count -gt 0) { throw "The MSYS2 toolchain is not the pinned one: $($mismatches -join '; ')" }
}

# --- Sources ------------------------------------------------------------------
Write-Host 'Sources'
foreach ($s in $Sources) { Save-PinnedFile -Urls $s.Urls -Dest (Join-Path $dl $s.File) -Sha256 $s.Sha256 | Out-Null }

# --- Build --------------------------------------------------------------------
$work = Join-Path $WorkRoot 'build'
$env:DL_DIR = $dl
$env:WORK_DIR = $work
$env:JOBS = "$Jobs"
$env:FFMPEG_EXTRA_VERSION = $inventory.extraVersion
$env:FFMPEG_COMPONENT_ARGS = (Get-FfmpegConfigureComponentArgs $inventory) -join ' '
foreach ($k in $Versions.Keys) { Set-Item -Path "env:$k" -Value $Versions[$k] }
$buildSh = Join-Path $PSScriptRoot 'ffmpeg-min\build.sh'
Write-Host "Building ffmpeg $($Versions.FFMPEG_VERSION) (-j$Jobs, low priority)"
Invoke-Low $bash @('-lc', "bash `"`$(cygpath -u '$buildSh')`"")

$built = Join-Path $work 'out\bin\ffmpeg.exe'
$missing = Test-FfmpegInventory $built $inventory
if ($missing.Count -gt 0) { throw "the built ffmpeg lacks: $($missing -join ', ')" }

# The exe ships to users, and its configure line is readable (-buildconf): it
# must not carry this machine's build folder.
$bytes = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($built))
$full = (Resolve-Path -LiteralPath $WorkRoot).Path.TrimEnd('\')
$posix = '/' + $full.Substring(0, 1).ToLowerInvariant() + ($full.Substring(2) -replace '\\', '/')
foreach ($leak in @($full, $posix)) {
    if ($bytes.IndexOf($leak, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "the built ffmpeg.exe contains the build path '$leak'." }
}

Copy-Item $built (Join-Path $OutDir 'ffmpeg.exe') -Force
Copy-Item (Join-Path $work 'out\ffmpeg-LICENSE.txt') (Join-Path $OutDir 'ffmpeg-LICENSE.txt') -Force
$info = Join-Path $OutDir 'ffmpeg-BUILDINFO.txt'
Copy-Item (Join-Path $work 'out\ffmpeg-BUILDINFO.txt') $info -Force
Add-Content -LiteralPath $info -Value "`nMSYS2 base: $($Msys2BaseUrls[0]) ($Msys2BaseSha256)"
Add-Content -LiteralPath $info -Value ("MSYS2 packages (pinned, tools/release/ffmpeg-min/msys2-packages.json): " +
    (($Msys2Pins.packages | ForEach-Object { "$($_.name) $($_.version)" }) -join ', '))
Write-Host ("ffmpeg.exe {0:N1} MB -> {1}" -f ((Get-Item (Join-Path $OutDir 'ffmpeg.exe')).Length / 1MB), $OutDir)
