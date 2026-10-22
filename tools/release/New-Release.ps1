#Requires -Version 7.0
<#
.SYNOPSIS
    One-command DotCharter release pipeline: builds all 5 GitHub release assets
    locally and prints the exact `gh release create` command (never runs it).

.DESCRIPTION
    CI cannot upload release artifacts (artifact storage quota), so releases are
    built and shipped locally. A release has exactly 5 assets — count them off
    the printed `gh release create` line, which uploads every member of $assets:

      1. DotCharter-v<ver>-win-x64.zip        UI, self-contained, folder-wrapped, SPLIT
      2. DotCharter-RigLab-v<rigver>.zip      the standalone rig, folder-wrapped
      3. payload-manifest.json                SHA-256 per payload file
      4. SHA256SUMS.txt                       digests of assets 1-3, bound to the tag
      5. SHA256SUMS.txt.sig                   Ed25519 signature over asset 4

    No voice pack with a non-free licence is built or shipped (#767): the
    release's voices/ holds only the CC0 / CC BY recordings.

    Asset 2 is a DIFFERENT APP on the same page (DotCharter.RigLab, its own
    <Version>). Its name carries neither "win-x64" nor "x64" ON PURPOSE: the
    in-app updater of every DotCharter up to v0.56 (the last release before the rig shipped) picks the first zip
    containing "win-x64", and installing the rig over DotCharter would replace
    the charting app. GitHubUpdateService also excludes "RigLab" by name now.
    -Tone3000ClientId bakes the publishable TONE3000 key into it (never commit
    the key); -SkipRigLab leaves it out.

    Asset 5 is what the updater trusts: from v1.1.0 on an install
    REFUSES any update whose SHA256SUMS.txt is not signed with the release key
    pinned in DotCharter.Services/Update/ReleaseSignature.cs. The private key
    lives OUTSIDE the repository (-SigningKeyPath); without it this script stops
    in preflight, before anything is built. See tools/release/README.md
    § Release signing.

    Asset 3 is NOT a convenience either: the in-app updater fetches it and
    refuses a downloaded asset whose digest disagrees (see the Checksum manifest
    section below). It was missing from this list while the script had been
    uploading it for releases.

    Asset 2 is NOT optional and NOT documentation. It is what makes the next
    update a small download: the in-app updater range-fetches only the payload
    members whose hash differs, out of asset 1 (Services/Update/
    PartialUpdateDownloader). A release that ships without it still installs -
    every install just downloads the full ~414 MB.

    INSTALL LAYOUT (#413). Asset 1 unpacks to a folder whose TOP holds only
    DotCharter.exe (the NativeAOT launcher, DotCharter.Launcher), voices/,
    tools/, README.txt and LegacyUpdaterShim's four copies; the app and its ~390
    files are in app/. The launcher starts app\DotCharter.exe. The four copies
    (DotCharter.runtimeconfig.json, DotCharter.deps.json, hostfxr.dll,
    coreclr.dll) are there ONLY so the updater inside every v0.62-and-older
    install accepts the payload; the app deletes them on its first start
    (SplitInstallCleanup). Removing them strands every such install.

    ZIP INVARIANT (do not "simplify" to Compress-Archive):
    The in-app updater's AppPayload.ResolveArchiveRoot matches entry names with
    FORWARD slashes only ("folder/DotCharter.exe"). PowerShell's Compress-Archive
    can emit backslash-separated entries, which the updater then rejects as "no
    DotCharter.exe". This script therefore zips via .NET's
    [System.IO.Compression.ZipFile]::CreateFromDirectory (always emits '/'), and
    every built UI zip is validated against the REAL AppPayload rules via
    tools/release/ValidateReleaseZip.cs before you are told to upload it.

.PARAMETER Version
    Release version. Defaults to <Version> parsed from DotCharter.UI.csproj.

.PARAMETER OutputDir
    Where assets land. Defaults to artifacts/release/v<Version> under the repo root.

.PARAMETER SkipUi
    Skip the (slow, ~300 MB) UI publish + zip. For iterating on the other assets.

.PARAMETER ContestedFeatures
    Default $true: the release is built as before. Pass -ContestedFeatures:$false
    for the public-build variant (#824): the UI is published with
    -p:DotCharterContestedFeatures=false, so the YouTube audio download
    is not registered, its menu row and palette entry are absent and
    yt-dlp is neither listed nor bundled. docs/agents/services/contested-features.md
    lists exactly what the switch covers.

    Independently of it, the UI is published with -p:DotCharterPlugins=false unless
    -WithPlugins is given: an optional plugin checked out in the git-ignored plugins/
    folder never reaches a public asset, and a payload or zip holding a plugin's
    assembly is refused (PluginPayloadCheck.psm1).

.PARAMETER WithPlugins
    The build WITH the plugins checked out under plugins/, off by default: the UI is
    published with -p:DotCharterPlugins=true and the payload folder, the zip and a
    -SmokeOnly input are REFUSED when they LACK one of the plugins' assemblies - the
    inverse of the public refusal, the same module (PluginPayloadCheck.psm1). Needs
    -Repo, which may not be the public repo. A plugin repository drives this from its
    own release script.

.PARAMETER Repo
    <owner>/<name> the printed `gh release create` targets. Default: the public
    repo; required with -WithPlugins (Resolve-ReleaseFeed).

.PARAMETER TagCheckout
    The checkout whose HEAD the printed `gh release create` tags; default the
    DotCharter checkout. A release in a plugin's own repo passes that plugin's
    checkout (its repo has no DotCharter commit); the notes then record both HEADs.

.PARAMETER SkipTools
    Skip bundling ffmpeg/yt-dlp into the UI payload's tools/ folder (for a fast
    test pack). Default is to bundle them.

.PARAMETER SkipTonelab
    Leave tonelab (the in-game tone preview's DLL and its 32-bit injection
    helper) out of the install's tools/tonelab/. Default: copy the prebuilt
    files from -TonelabDir and FAIL in preflight when one is missing or its
    SHA-256 differs from tools/release/tonelab/tonelab.json. A release without
    tools/tonelab/ still works; the app just registers no launcher, so "Start
    Rocksmith" is not offered.

.PARAMETER TonelabDir
    The folder holding the prebuilt tonelab.dll, tonelab-launch.exe and
    tonelab.ini (tonelab's own build output). Default: $env:DOTCHARTER_TONELAB_DIR.
    tonelab's source is NOT in this repository; only the hashes are.

.PARAMETER AllowMissingTools
    Produce the release even when a media tool cannot be acquired (its pinned
    download 404s, is truncated, or the cached copy is not the pinned version).
    Default: FAIL in preflight, before anything is published. Without ffmpeg a
    user's stem Opus encoding and YouTube import break, and the v0.59.0 pack
    nearly shipped that way when BtbN retired the n7.1 asset (#137); ffmpeg is
    now built from pinned sources (Build-MinimalFfmpeg.ps1, #409).

.PARAMETER SkipLaunchSmoke
    Skip the launch smoke check of the packed DotCharter.exe. ESCAPE HATCH ONLY:
    it is the one step that runs what ships. Default: the UI zip is extracted to
    a scratch folder with a portable appconfig.json (so it never reads or writes
    your real config, and opens WASAPI, never ASIO), DotCharter.exe is started
    on a hidden Win32 desktop (HiddenDesktop.psm1, #419: it can never take the
    foreground), and the pack FAILS unless app\DotCharter.exe writes its
    StartupPhases: line within -SmokeTimeoutSeconds and is still alive
    -SmokeSettleSeconds later, or if DotCharter ever owns the foreground window.

.PARAMETER SmokeTimeoutSeconds
    How long the launch smoke waits for the StartupPhases: line. Default 120.

.PARAMETER SmokeSettleSeconds
    How long the app must stay alive after its StartupPhases: line. Default 5.

.PARAMETER SmokeOnly
    Build nothing. Run the binary check and the launch smoke against an existing
    payload folder or release zip (copied/extracted to a scratch folder first, so
    the input is never modified), then exit. Use it to re-verify a pack.

.PARAMETER SkipHeadlessUi
    Skip the blocking headless-UI test gate (~3 min sharded). ESCAPE HATCH ONLY — that
    suite is the only thing in the repo that catches the five classes of XAML
    fault which build clean and fail at runtime, and it has no other home
    (it cannot share a testhost with the default gate). Skipping it means the
    pack is unverified, and the script says so.

.PARAMETER ForceHeadlessUi
    Run the headless UI suite's main pass even when a green run of it is on record
    for this exact tree (ReleaseCache.psm1 Find-GateRecord: the merge train, an
    earlier release run, or the dev tree a scratch export was cut from). Without it
    the recorded run is reused and named; the test build and the localized layout
    sweeps run either way.

.PARAMETER ForceReleaseLane
    Run the release lane even when a green run of it is on record for this exact tree
    (the merge train records one). The lane itself cannot be skipped: it is the slow
    corpus / integration classes the default gate leaves out
    (tools/ci/release-lane.json), and a release is not cut without them.

.PARAMETER HeadlessShards
    How many concurrent testhosts each headless-UI pass is split across
    (tools/ci/HeadlessShards.psm1 — whole classes, balanced by measured class
    time). Default 4. Retries and isolated re-runs stay one fresh process.

.PARAMETER SigningKeyPath
    The release-signing Ed25519 PRIVATE key (base64, 32 bytes) that signs
    SHA256SUMS.txt. Default: $env:DOTCHARTER_RELEASE_SIGNING_KEY, else
    ..\DotCharter-local\release-signing\dotcharter-release-ed25519.key beside the
    repository. A missing key, or one whose public half is not the pinned one,
    FAILS preflight: an unsigned release is one no install will take.
.PARAMETER SkipRigLab
    Do not build the standalone rig (asset 2).
.PARAMETER Tone3000ClientId
    The PUBLISHABLE TONE3000 key (t3k_pub_...) baked into the rig so it can
    sign in out of the box. Passed to Publish-RigLab.ps1; never committed.
.PARAMETER PayloadOnly
    Build the UI payload stage ONLY: the clean Release publish into
    staging/<name>/app with exactly the release's arguments (-p:DotCharterPlugins=false
    included, or =true with -WithPlugins) and the plugin check, then exit. No preflight, so no voice packs and
    no publish-layout check (it demands them); no headless suite; nothing zipped,
    hashed or printed for upload. The dry run of the public payload: run it with a
    plugin checked out and the payload must still come out without the plugin's
    assembly. With -WithPlugins it is the dry run of the plugin payload, which must
    come out WITH every plugin.

.PARAMETER SelfTest
    Do not build anything. Exercises the zip-layout logic + validator against a
    synthetic payload in a temp dir (good zip must PASS, backslash-entry zip must
    FAIL), the binary check (a zero-filled DotCharter.dll and a text stub .exe
    must FAIL by name, real PE images must PASS), the launch smoke's log reader,
    the hidden-desktop launcher (HiddenDesktop.psm1: a child that opens and
    activates a window runs on the hidden desktop and never owns the
    foreground), and the headless coverage + shard logic, then exits. Run this after touching
    this script or AppPayload.

.EXAMPLE
    pwsh tools/release/New-Release.ps1
.EXAMPLE
    pwsh tools/release/New-Release.ps1 -SelfTest
.EXAMPLE
    pwsh tools/release/New-Release.ps1 -SkipTools   # fast pack without the media tools
.EXAMPLE
    pwsh tools/release/New-Release.ps1 -SmokeOnly artifacts/release/v0.59.0/DotCharter-v0.59.0-win-x64.zip
.EXAMPLE
    pwsh tools/release/New-Release.ps1 -PayloadOnly -OutputDir $env:TEMP/dc-payload   # the public payload, nothing else
.EXAMPLE
    pwsh tools/release/New-Release.ps1 -WithPlugins -Repo owner/name -TagCheckout plugins/Example   # a plugin build, for that plugin's repo
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [switch]$SkipUi,
    [switch]$SkipTools,
    [switch]$SkipTonelab,
    [string]$TonelabDir = $env:DOTCHARTER_TONELAB_DIR,
    [bool]$ContestedFeatures = $true,
    [switch]$AllowMissingTools,
    [switch]$SkipHeadlessUi,
    # Re-run the headless suite even when a green run is on record for this exact tree.
    [switch]$ForceHeadlessUi,
    # Run the release lane even when a green run of it is on record for this exact tree.
    [switch]$ForceReleaseLane,
    [switch]$SkipRigLab,
    [string]$Tone3000ClientId,
    [ValidateRange(1, 16)]
    [int]$HeadlessShards = 4,
    [switch]$SkipLaunchSmoke,
    [ValidateRange(10, 900)]
    [int]$SmokeTimeoutSeconds = 120,
    [ValidateRange(0, 120)]
    [int]$SmokeSettleSeconds = 5,
    [string]$SmokeOnly,
    [switch]$SelfTest,
    [switch]$PayloadOnly,
    # The build with the plugins checked out under plugins/; off by default.
    [switch]$WithPlugins,
    # <owner>/<name> for the printed `gh release create`; default by mode (Resolve-ReleaseFeed).
    [string]$Repo,
    # The checkout whose HEAD the printed `gh release create` tags; default this one.
    [string]$TagCheckout,
    # Skip the public -> dev sync check (Find-UnimportedPublicCommits.ps1), e.g. offline.
    [switch]$SkipPublicSyncCheck,
    # The release-signing private key. NEVER inside the repository.
    [string]$SigningKeyPath = $env:DOTCHARTER_RELEASE_SIGNING_KEY
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
# MSBuild arguments for the UI publish. Empty by default, so the default release is unchanged.
$ContestedPublishArgs = @()
if (-not $ContestedFeatures) { $ContestedPublishArgs += '-p:DotCharterContestedFeatures=false' }
# Find-PluginsInPayload / Assert-ReleasePayloadPlugins / Resolve-ReleaseFeed: run by the gate
# on fixture payloads (ReleasePluginRefusalTests).
Import-Module (Join-Path $PSScriptRoot 'PluginPayloadCheck.psm1') -Force
$PluginAssemblies = @(Get-PluginAssemblyNames $RepoRoot)
# The console tools a plugin ships (<DotCharterReleaseTool>): the executables go into the app folder,
# the launchers into tools/. A public payload carries neither; a -WithPlugins one carries both.
$PluginToolExes = @(Get-PluginToolExeNames $RepoRoot)
$PluginToolFiles = @(Get-PluginToolFileNames $RepoRoot)
# Public assets never carry a plugin: the git-ignored plugins/ folder is ignored even when
# something is checked out there, and Assert-ReleasePayloadPlugins refuses a payload that
# still holds a plugin's assembly (a folder before zipping, the zip after). -WithPlugins
# flips both: the plugins are built in, and a payload WITHOUT one of them is refused.
$UiPluginPublishArgs = @(Get-UiPluginPublishArgs -WithPlugins:$WithPlugins)
# The committed packages.lock.json files pin the PUBLIC graph (version + content
# hash of every package), so a public publish restores in locked mode and fails
# on anything else. A plugin build restores another graph, whose lock lives
# uncommitted under obj/ (Directory.Build.props), so it cannot be held to them.
$LockedRestoreArgs = @(if (-not $WithPlugins) { '-p:RestoreLockedMode=true' })
$PluginList = if ($PluginAssemblies.Count -gt 0) { $PluginAssemblies -join ', ' } else { 'none checked out' }
$PluginModeText = if ($WithPlugins) { "WITH the plugins ($PluginList; DotCharterPlugins=true)" } else { "no plugin in the payload (checked out: $PluginList; DotCharterPlugins=false)" }
Import-Module (Join-Path $RepoRoot 'tools/ci/HeadlessShards.psm1') -Force
$Validator = Join-Path $PSScriptRoot 'ValidateReleaseZip.cs'
$ManifestTool = Join-Path $PSScriptRoot 'BuildPayloadManifest.cs'
$PruneTool = Join-Path $PSScriptRoot 'PrunePayload.cs'
$BinaryCheckTool = Join-Path $PSScriptRoot 'CheckPayloadBinaries.cs'
$SignTool = Join-Path $PSScriptRoot 'SignReleaseManifest.cs'

# --- Bundled media tools (ffmpeg / yt-dlp) -----------------------------------
# The UI payload ships these in its tools/ subfolder so stem Opus encoding and
# YouTube import work out of the box (the app resolves configured -> exe dir ->
# exe dir/tools -> PATH). Binaries are made once into the git-ignored repo
# cache tools/redist/win-x64/ and reused by every subsequent pack — but only
# while each one still REPORTS the pinned version when RUN (Ensure-RedistTools),
# and, for ffmpeg, still lists every component the app uses.
#
# Pinned versions (update deliberately):
#   yt-dlp : tools/release/yt-dlp.json pins the dated tag, its URL and the
#            exe's SHA-256 (the release's own SHA2-256SUMS line). The exe is
#            fetched through Save-PinnedFile, so a cached or downloaded file that
#            does not hash to the pin is deleted and NEVER run; only then is it
#            run for its --version. Single ~18 MB exe. Unlicense (public domain).
#            https://github.com/yt-dlp/yt-dlp/releases
#   ffmpeg : OUR minimal LGPL static build (#409), made by Build-MinimalFfmpeg.ps1
#            from pinned, hash-checked sources with exactly the components in
#            tools/release/ffmpeg-min/components.json. The version and the
#            -extra-version tag come from that file, so moving either rebuilds.
#            No ffprobe: AudioMetadataExtractor reads tags through ffmpeg alone.
Import-Module (Join-Path $PSScriptRoot 'FfmpegInventory.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PinnedDownload.psm1') -Force
# The redist cache and the gate records live OUTSIDE the tree (ReleaseCache.psm1), so a
# fresh scratch export reuses the minimal ffmpeg and a green headless run of its tree.
Import-Module (Join-Path $PSScriptRoot 'ReleaseCache.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'VcRedist.psm1') -Force
# The key's default lives in the owner's DotCharter-local, beside the DEV checkout: for a
# scratch export (export-origin.json) the checkout it came from, for a worktree its main one.
if (-not $SigningKeyPath) {
    $SigningKeyPath = Join-Path (Get-ReleaseLocalDir -RepoRoot $RepoRoot) 'release-signing/dotcharter-release-ed25519.key'
}
# Start-HiddenDesktopProcess: the launch smoke runs the app on a never-shown
# desktop so it cannot take the foreground (#419).
Import-Module (Join-Path $PSScriptRoot 'HiddenDesktop.psm1') -Force
$YtDlpPin       = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'yt-dlp.json') -Raw | ConvertFrom-Json
$YtDlpVersion   = $YtDlpPin.version
$FfmpegInventory = Get-FfmpegInventory
$FfmpegBuildScript = Join-Path $PSScriptRoot 'Build-MinimalFfmpeg.ps1'
# `ffmpeg -version`'s first line reads "ffmpeg version 8.1.3-dotcharter-min1
# Copyright ..."; a cached exe with any other version or tag is rebuilt.
$FfmpegVersionPattern = '^ffmpeg version ' + [regex]::Escape("$($FfmpegInventory.ffmpegVersion)-$($FfmpegInventory.extraVersion)") + '\s'
$RedistDir      = Join-Path $RepoRoot 'tools/redist/win-x64'
# The x64 VC++ runtime DLLs onnxruntime.dll imports, shipped app-local beside it
# (tools/release/vcredist.json pins the version and each file's SHA-256).
$VcRedistPin    = Read-VcRedistPin -Path (Join-Path $PSScriptRoot 'vcredist.json')
# What one minimal-ffmpeg build leaves in -OutDir; the cache entry holds the same set.
$FfmpegRedistFiles = @('ffmpeg.exe', 'ffmpeg-LICENSE.txt', 'ffmpeg-BUILDINFO.txt')

# --- tonelab (prebuilt, pinned by hash) ----------------------------------------
# The in-game tone preview's game side: tonelab.dll, the 32-bit helper that
# injects it, and its default tonelab.ini. Taken prebuilt from -TonelabDir,
# each file checked against tools/release/tonelab/tonelab.json, then copied
# into the install's tools/tonelab/ BEFORE the payload is hashed. The
# app registers its launcher only when both binaries are there
# (TonelabInstall.Find). docs/agents/services/tonelab-bridge.md.
$TonelabPinDir  = Join-Path $PSScriptRoot 'tonelab'
$TonelabFiles   = @('tonelab.dll', 'tonelab-launch.exe', 'tonelab.ini')
$script:ToolsSummary = 'not attempted'
$script:SmokeSummary = 'not run'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message)   { Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn2([string]$Message){ Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

Import-Module (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') -Force

function Get-AppVersion {
    $csproj = Get-Content (Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj') -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') {
        throw 'Could not parse <Version> from DotCharter.UI/DotCharter.UI.csproj.'
    }
    return $Matches[1].Trim()
}

# The UI publish, the plugin check and the layout check: the release's first stage,
# and all of -PayloadOnly. $UiPluginPublishArgs is never optional.
function Publish-UiPayload([string]$AppDir, [switch]$NoLayoutCheck) {
    & dotnet publish (Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj') `
        -c Release -r win-x64 --self-contained true -o $AppDir "-p:Version=$Version" @ContestedPublishArgs @UiPluginPublishArgs @LockedRestoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (UI) failed.' }
    Assert-ReleasePayloadPlugins $AppDir $PluginAssemblies -WithPlugins:$WithPlugins
    if ($WithPlugins) { Publish-PluginTools -RepoRoot $RepoRoot -AppDir $AppDir -Scratch $OutputDir -Version $Version }
    if ($PluginToolExes.Count -gt 0) { Assert-ReleasePayloadPlugins $AppDir $PluginToolExes -WithPlugins:$WithPlugins }
    Write-Ok $PluginModeText

    if ($NoLayoutCheck) { return }
    Test-PublishLayout -PublishDir $AppDir
    Write-Ok 'publish layout verified (exe + juce_sharp + hostfxr/coreclr + self-contained runtimeconfig)'
}

# Folder-wrapped zip: CreateFromDirectory(dir, zip, Optimal, includeBaseDirectory: $true)
# produces entries like "DotCharter-vX-win-x64/DotCharter.exe" with '/' separators,
# which is exactly the shape AppPayload.ResolveArchiveRoot unwraps.
function New-FolderWrappedZip([string]$Directory, [string]$ZipPath) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $Directory, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}

# Runs the UI zip through the REAL updater rules (AppPayload.ValidateArchive /
# ResolveArchiveRoot from DotCharter.Services) via a .NET 10 file-based program.
function Test-ReleaseZip([string]$ZipPath, [string]$ExpectedRoot) {
    & dotnet run $Validator $ZipPath $ExpectedRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release zip failed AppPayload validation: $ZipPath (see output above)."
    }
}

# The top level of a split install (#413), exactly: the launcher, voices/,
# tools/, the readme, the app folder and LegacyUpdaterShim's four copies. Anything
# else up there is the clutter #413 removed, back again.
$InstallTopFiles = @('DotCharter.exe', 'README.txt',
                     'DotCharter.runtimeconfig.json', 'DotCharter.deps.json', 'hostfxr.dll', 'coreclr.dll')
$InstallTopFolders = @('app', 'voices', 'tools')

# The launcher (DotCharter.Launcher), published NativeAOT and copied to the top as
# DotCharter.exe. NativeAOT links with the MSVC linker, which the ILCompiler finds
# through vswhere.exe - not on PATH on a Build-Tools-only machine.
function Publish-Launcher([string]$Destination, [string]$AppVersion) {
    $vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $vswhereDir 'vswhere.exe'))) {
        $env:PATH = "$env:PATH;$vswhereDir"
    }
    $out = Join-Path $OutputDir 'staging-launcher'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & dotnet publish (Join-Path $RepoRoot 'DotCharter.Launcher/DotCharter.Launcher.csproj') `
        -c Release -r win-x64 -o $out "-p:Version=$AppVersion" -p:DotCharterPlugins=false -p:RestoreLockedMode=true | Out-Host   # never into the return value
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (launcher, NativeAOT) failed.' }
    $exe = Join-Path $out 'DotCharter.Launcher.exe'
    if (-not (Test-Path $exe)) { throw "The launcher publish wrote no $exe." }
    if (Test-Path (Join-Path $out 'DotCharter.Launcher.dll')) {
        throw 'The launcher was published as a managed app (DotCharter.Launcher.dll beside it), not NativeAOT.'
    }
    Copy-Item $exe (Join-Path $Destination 'DotCharter.exe') -Force
    return (Get-Item $exe).Length
}

# Lays the published app out as a split install: the app stays in app/, the
# voice packs move to the top (the app points the native store there through
# DOTCHARTER_VOICE_DIR), the shim copies and the readme are written.
function New-SplitInstallLayout([string]$InstallDir, [string]$AppDir) {
    $voices = Join-Path $AppDir 'voices'
    if (Test-Path $voices) { Move-Item $voices (Join-Path $InstallDir 'voices') }
    foreach ($name in 'DotCharter.runtimeconfig.json', 'DotCharter.deps.json', 'hostfxr.dll', 'coreclr.dll') {
        Copy-Item (Join-Path $AppDir $name) (Join-Path $InstallDir $name) -Force
    }
    # The source pointer names the exact tag (GPL-3.0 corresponding source, #753).
    Set-Content -Path (Join-Path $InstallDir 'README.txt') -Encoding utf8 -Value @"
DotCharter $($Version)
==========

Start DotCharter.exe in this folder.

DotCharter is free software under the GNU GPL v3 or later: app\LICENSE is the
licence, app\THIRD-PARTY-NOTICES.md the notices of everything it ships. The
source of this build is the tag v$($Version):
$($ReleaseIdentity.SourceUrl)

  app\     the application and its libraries (do not move or rename)
  voices\  the built-in guitar and bass sample packs
  tools\   ffmpeg and yt-dlp, with their licences$(if ($WithPlugins -and $PluginToolFiles.Count -gt 0) { "`n            and the launchers of the plugin tools (drop files on one)" })

Not affiliated with or endorsed by Ubisoft. Rocksmith is a trademark of Ubisoft Entertainment.

Settings, profiles and logs are kept in your user profile, or in this folder
when an appconfig.json sits beside DotCharter.exe (a portable install).

DotCharter.runtimeconfig.json, DotCharter.deps.json, hostfxr.dll and
coreclr.dll up here are only for updating installs older than 0.63; the app
removes them the first time it starts.
"@
}

function Test-InstallLayout([string]$InstallDir) {
    foreach ($entry in Get-ChildItem -LiteralPath $InstallDir -Force) {
        $allowed = if ($entry.PSIsContainer) { $InstallTopFolders } else { $InstallTopFiles }
        if ($allowed -notcontains $entry.Name) {
            throw "Unexpected entry at the top of the install: $($entry.Name). The top holds only $($InstallTopFiles -join ', ') and $($InstallTopFolders -join '/, ')/ (#413)."
        }
    }
    foreach ($required in @('DotCharter.exe', 'app/DotCharter.exe', 'voices', 'README.txt')) {
        if (-not (Test-Path (Join-Path $InstallDir $required))) { throw "Missing from the install layout: $required" }
    }
}

function Test-PublishLayout([string]$PublishDir) {
    foreach ($file in 'DotCharter.exe', 'juce_sharp.dll', 'hostfxr.dll', 'coreclr.dll',
                      'DotCharter.runtimeconfig.json', 'DotCharter.deps.json') {
        if (-not (Test-Path (Join-Path $PublishDir $file))) {
            throw "Missing from publish output: $file (in $PublishDir)"
        }
    }
    # The built-in guitar / bass voice's sample packs (DotCharter.UI.csproj copies
    # artifacts/voice-pack into voices/). Without them the voice falls back to its
    # string model (or a Strat model to the Gretsch set): a release must never
    # ship that by accident.
    foreach ($file in ($VoicePackFiles | ForEach-Object { "voices/$_" })) {
        if (-not (Test-Path (Join-Path $PublishDir $file))) {
            throw "Missing from publish output: $file (the voice sample packs; see Initialize-VoicePacks)"
        }
    }
    # ...and none of them may hold a recording under its own licence (#757, #767):
    # no such recording ships in any release.
    Assert-NoSeparatelyLicensedVoice (Join-Path $PublishDir 'voices')
    $config = Get-Content (Join-Path $PublishDir 'DotCharter.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) {
        throw 'Publish is not self-contained: runtimeconfig.json has no includedFrameworks. ' +
              'Shipping this would brick in-app updates (see AppPayload / InstallLayout).'
    }
    Test-PublishTrimSettings -PublishDir $PublishDir
}

# The feature switches the .NET linker writes into runtimeconfig.json when
# PublishTrimmed is a PROPERTY (Microsoft.NET.ILLink.targets). DotCharter.UI.csproj
# trims only the two Silk.NET COM bindings and sets PublishTrimmed inside a target
# so none of these is written: each one changes the whole app (System.Text.Json
# reflection off breaks every reflection-serialized type, #410).
$TrimmedAppSwitches = @(
    'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault',
    'System.ComponentModel.DefaultValueAttribute.IsSupported',
    'System.ComponentModel.Design.IDesignerHost.IsSupported',
    'System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported',
    'System.Resources.ResourceManager.AllowCustomResourceTypes',
    'System.StartupHookProvider.IsSupported',
    'System.Runtime.InteropServices.EnableConsumingManagedCodeFromNativeHosting',
    'Microsoft.Extensions.DependencyInjection.VerifyOpenGenericServiceTrimmability',
    'Serilog.Capturing.IsStructureValueSupported')

# Payload size (#410): the publish trims Silk.NET.Direct3D11/DXGI (30 MB of
# precompiled COM binding down to ~70 KB) and NOTHING else. Refuses a payload
# where the trim did not run (the binding is back at full size) or ran as a
# whole-app trim (its feature switches are in runtimeconfig.json).
function Test-PublishTrimSettings([string]$PublishDir) {
    $config = Get-Content (Join-Path $PublishDir 'DotCharter.runtimeconfig.json') -Raw | ConvertFrom-Json
    $props = @()
    if ($config.runtimeOptions.configProperties) { $props = @($config.runtimeOptions.configProperties.PSObject.Properties.Name) }
    $found = @($props | Where-Object { $TrimmedAppSwitches -contains $_ })
    if ($found.Count -gt 0) {
        throw "runtimeconfig.json carries trimmed-app feature switches ($($found -join ', ')): PublishTrimmed was set as a " +
              'property, which changes the whole app. DotCharter.UI.csproj sets it only inside DotCharterEnableBindingTrim.'
    }
    $binding = Join-Path $PublishDir 'Silk.NET.Direct3D11.dll'
    if ((Test-Path -LiteralPath $binding) -and (Get-Item -LiteralPath $binding).Length -gt 1MB) {
        throw ("Silk.NET.Direct3D11.dll is {0:N1} MB: the binding trim did not run (DotCharterTrimOnlyBindings); " +
               'the payload carries ~30 MB it does not need.') -f ((Get-Item -LiteralPath $binding).Length / 1MB)
    }
}

# The voice sample packs are NOT in git (~600 MB: the CC0 Karoryfer sets, one
# pack per Stratocaster pickup position from EGFxSet, CC BY 4.0 — #50 — and the
# guitar articulations every model shares from Guitar-TECHS, CC BY 4.0 — #52).
# The bass slaps of a Rickenbacker 4001 (#109, CC BY-NC-SA 3.0 modified) ship
# nowhere (#767): the packer leaves every "private" source out of these packs
# unless asked, and the release never asks. The packer
# downloads exactly the files its committed manifest pins (URL + SHA-256), so a
# missing or stale pack is rebuilt identically on any machine with Python + numpy.
$VoicePackFiles = @('guitar.dcvp', 'bass.dcvp', 'strat-neck.dcvp', 'strat-neck-middle.dcvp', 'strat-middle.dcvp',
                    'strat-middle-bridge.dcvp', 'strat-bridge.dcvp', 'guitar-arts.dcvp', 'VOICE-CREDITS.txt')
# What the packs must have been built FROM: the SHA-256 of the committed manifest
# and of the builder, exactly as build_voice_pack.py writes voice-pack.stamp.
function Get-VoicePackSourceStamp {
    $voice = Join-Path $RepoRoot 'JuceSharp/src/JuceSharp.Native/tools/voice'
    $manifest = (Get-FileHash (Join-Path $voice 'voice_pack_manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $builder = (Get-FileHash (Join-Path $voice 'build_voice_pack.py') -Algorithm SHA256).Hash.ToLowerInvariant()
    return "manifest=$manifest`nbuilder=$builder`n"
}
function Initialize-VoicePacks {
    $packDir = Join-Path $RepoRoot 'artifacts/voice-pack'
    $have = -not ($VoicePackFiles | Where-Object { -not (Test-Path (Join-Path $packDir $_)) })
    # Present is not enough: packs from an older manifest or builder (or with no
    # voice-pack.stamp at all) are STALE — #109 added the recorded slaps to an
    # existing bass.dcvp — and are rebuilt, never shipped.
    $stampPath = Join-Path $packDir 'voice-pack.stamp'
    $fresh = (Test-Path $stampPath) -and ((Get-Content $stampPath -Raw) -ceq (Get-VoicePackSourceStamp))
    if ($have -and $fresh) { Write-Ok "voice sample packs present and current ($packDir)"; return }
    $packer = Join-Path $RepoRoot 'JuceSharp/src/JuceSharp.Native/tools/voice/build_voice_pack.py'
    if ($have) { Write-Step 'The voice sample packs are STALE (voice-pack.stamp does not match the manifest / builder): rebuilding' }
    Write-Step 'Building the voice sample packs (build_voice_pack.py, pinned + hashed sources)'
    & python $packer build --out $packDir
    if ($LASTEXITCODE -ne 0) { throw "Building the voice sample packs failed (python $packer build --out $packDir)." }
    Write-Ok "voice sample packs built ($packDir)"
}

# True when the .dcvp at $Path has a zone of articulation $Art: the 64-byte
# header (u32 zoneCount at 12, u64 zonesOffset at 24), then 40-byte zones whose
# first byte is the articulation (build_voice_pack.py, PACK FORMAT).
function Test-VoicePackHasArticulation([string]$Path, [int]$Art) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'DCVP') { throw "Not a voice pack: $Path" }
    $count = [System.BitConverter]::ToUInt32($bytes, 12)
    $offset = [System.BitConverter]::ToUInt64($bytes, 24)
    for ($i = 0; $i -lt $count; $i++) {
        if ($bytes[$offset + 40 * $i] -eq $Art) { return $true }
    }
    return $false
}

# The release's voices/ holds no recording under its own licence: no recorded
# slap (articulation 9, the Rickenbacker set) in its bass.dcvp and no
# <pack>.LICENSE.txt.
function Assert-NoSeparatelyLicensedVoice([string]$VoicesDir) {
    $bass = Join-Path $VoicesDir 'bass.dcvp'
    if ((Test-Path $bass) -and (Test-VoicePackHasArticulation $bass 9)) {
        throw "$bass holds the recorded slaps (CC BY-NC-SA): it is the non-commercial pack, which ships nowhere (#757, #767). Rebuild artifacts/voice-pack with a plain build_voice_pack.py build."
    }
    $licences = @(Get-ChildItem -Path $VoicesDir -Filter '*.LICENSE.txt' -File -ErrorAction SilentlyContinue)
    if ($licences.Count -gt 0) {
        throw "$VoicesDir holds $($licences.Name -join ', '): a separately licensed pack must never ship in the release (#757, #767)."
    }
}

function Remove-DeadPayloadWeight([string]$PublishDir) {
    # Rules live in DotCharter.Services (ReleasePayloadPrune) so the gate can pin
    # them; this only applies them. Runs BEFORE the manifest is hashed -- the
    # manifest is what the in-app updater range-fetches against, so hashing a file
    # we then delete would make every partial update hunt for bytes the zip lacks.
    & dotnet run $PruneTool $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Payload prune failed for $PublishDir (see output above)."
    }
}

# ---------------------------------------------------------------------------
# Media-tool bundling (ffmpeg / yt-dlp)
# ---------------------------------------------------------------------------

# Throws unless $Path hashes to the yt-dlp pin (tools/release/yt-dlp.json).
function Assert-YtDlpPin([string]$Path) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $YtDlpPin.sha256) { throw "$Path has SHA-256 $actual, but tools/release/yt-dlp.json pins $($YtDlpPin.sha256)." }
}

# The first line a tool prints for its version flag, or $null when it does not
# run (missing, not a PE, crashes, non-zero exit). Running it is the point: a
# cached exe that merely EXISTS proved nothing in #137.
function Get-ToolVersionLine([string]$Exe, [string[]]$Arguments) {
    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    try {
        $out = @(& $Exe @Arguments 2>$null)
        if ($LASTEXITCODE -ne 0 -or $out.Count -eq 0) { return $null }
        return ([string]$out[0]).Trim()
    }
    catch { return $null }
    finally { $global:LASTEXITCODE = 0 }
}

# "ffmpeg version 8.1.3-dotcharter-min1 Copyright ..." -> "8.1.3-dotcharter-min1"
function Get-FfmpegBuildVersion([string]$VersionLine) {
    if ($VersionLine -match '^ffmpeg version (\S+)') { return $Matches[1] }
    return $null
}

# { Line; Missing; Ok } for the ffmpeg.exe at $Path: its `-version` line, the
# inventory components it lacks, and whether it is the pinned, complete build.
function Get-FfmpegState([string]$Path) {
    $line = Get-ToolVersionLine $Path @('-hide_banner', '-version')
    $missing = @()
    if ($line -and $line -match $FfmpegVersionPattern) { $missing = @(Test-FfmpegInventory $Path $FfmpegInventory) }
    return [pscustomobject]@{
        Line    = $line
        Missing = $missing
        Ok      = [bool]($line -and $line -match $FfmpegVersionPattern -and $missing.Count -eq 0)
    }
}

# Ensure the redistributables exist in tools/redist/win-x64/ AT THEIR PINNED
# VERSIONS, downloading any that are missing or stale. Returns a result object;
# records (does not throw) per-tool failures so the caller decides whether they
# are fatal (they are, unless -AllowMissingTools).
function Ensure-RedistTools {
    New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null
    $ytDlp   = Join-Path $RedistDir 'yt-dlp.exe'
    $ffmpeg  = Join-Path $RedistDir 'ffmpeg.exe'
    $result = [pscustomobject]@{
        YtDlp = $null; Ffmpeg = $null
        YtDlpVersion = $YtDlpVersion; FfmpegVersion = $null
        Errors = @()
    }

    # --- yt-dlp (single exe) ---
    if (-not $ContestedFeatures) {
        Write-Ok 'yt-dlp left out (-ContestedFeatures:$false)'
    }
    else {
    try {
        # The hash first: a cached or downloaded exe that is not the pinned one
        # is deleted by Save-PinnedFile and never reaches the line that runs it.
        # Downloaded once into the release cache (keyed by the pinned hash), then
        # copied into the tree and held to the same pin there.
        $cachedYtDlp = Join-Path (Get-RedistCacheDir -Tool 'yt-dlp' -Key $YtDlpPin.sha256) 'yt-dlp.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $cachedYtDlp -Parent) | Out-Null
        $source = Save-PinnedFile -Urls @($YtDlpPin.url) -Dest $cachedYtDlp -Sha256 $YtDlpPin.sha256
        Copy-Item -LiteralPath $cachedYtDlp -Destination $ytDlp -Force
        Save-PinnedFile -Urls @($YtDlpPin.url) -Dest $ytDlp -Sha256 $YtDlpPin.sha256 | Out-Null
        $reported = Get-ToolVersionLine $ytDlp @('--version')
        if ($reported -ne $YtDlpVersion) { throw "yt-dlp.exe matches its pinned SHA-256 but reports '$reported', not $YtDlpVersion; fix tools/release/yt-dlp.json." }
        $how = if ($source -eq 'cache') { 'cached' } else { 'downloaded' }
        Write-Ok ("yt-dlp $reported $how, SHA-256 verified ({0:N1} MB)" -f ((Get-Item $ytDlp).Length / 1MB))
        $result.YtDlp = $ytDlp
    }
    catch {
        $result.Errors += "yt-dlp: $_"
        Write-Warn2 "yt-dlp not acquired: $_"
    }
    }

    # --- ffmpeg (our minimal LGPL build, #409) ---
    # Valid only while it reports the pinned version AND still lists every
    # component in components.json: an inventory that grew since the cache was
    # built rebuilds it, rather than shipping an ffmpeg that cannot do a job.
    # Looked for in the tree's tools/redist first, then in the release cache
    # (Get-FfmpegPinKey: the build script + ffmpeg-min/*), and built only when
    # neither holds a valid one. Every source is held to the same two checks.
    try {
        $ffmpegKey = Get-FfmpegPinKey -ReleaseDir $PSScriptRoot
        $state = Get-FfmpegState $ffmpeg
        $how = 'cached'
        if (-not $state.Ok -and (Copy-RedistFromCache -Tool 'ffmpeg' -Key $ffmpegKey -Destination $RedistDir -Files $FfmpegRedistFiles)) {
            $state = Get-FfmpegState $ffmpeg
            if ($state.Ok) { $how = "from the release cache ($(Get-RedistCacheDir -Tool 'ffmpeg' -Key $ffmpegKey))" }
            else {
                Write-Warn2 "the release cache's ffmpeg ($(Get-RedistCacheDir -Tool 'ffmpeg' -Key $ffmpegKey)) fails its checks - dropping it."
                Remove-RedistCacheEntry -Tool 'ffmpeg' -Key $ffmpegKey
            }
        }
        if ($state.Ok) {
            Write-Ok ("ffmpeg {0} {1} ({2:N1} MB), all {3} inventory components present" -f (Get-FfmpegBuildVersion $state.Line), $how,
                ((Get-Item $ffmpeg).Length / 1MB), (Get-FfmpegInventoryCount $FfmpegInventory))
        }
        else {
            if ($state.Line -and $state.Missing.Count -gt 0) { Write-Warn2 "cached ffmpeg lacks $($state.Missing -join ', ') - rebuilding." }
            elseif ($state.Line) { Write-Warn2 "cached ffmpeg is '$(Get-FfmpegBuildVersion $state.Line)', not the pinned build - rebuilding." }
            Write-Step "Building the minimal ffmpeg $($FfmpegInventory.ffmpegVersion) (Build-MinimalFfmpeg.ps1, ~10 min the first time)"
            & $FfmpegBuildScript -OutDir $RedistDir
            $state = Get-FfmpegState $ffmpeg
            if (-not $state.Line -or $state.Line -notmatch $FfmpegVersionPattern) {
                throw "the built ffmpeg.exe reports '$($state.Line)', not the pinned $($FfmpegInventory.ffmpegVersion)-$($FfmpegInventory.extraVersion)."
            }
            if ($state.Missing.Count -gt 0) { throw "the built ffmpeg.exe lacks $($state.Missing -join ', ')." }
            Write-Ok ("ffmpeg.exe {0} ({1:N1} MB)" -f (Get-FfmpegBuildVersion $state.Line), ((Get-Item $ffmpeg).Length / 1MB))
        }
        # A valid ffmpeg the cache does not hold yet fills it, so the next fresh tree copies it.
        if (-not (Test-Path -LiteralPath (Join-Path (Get-RedistCacheDir -Tool 'ffmpeg' -Key $ffmpegKey) 'ffmpeg.exe'))) {
            $saved = Save-RedistToCache -Tool 'ffmpeg' -Key $ffmpegKey -Source $RedistDir -Files $FfmpegRedistFiles
            Write-Ok "ffmpeg stored in the release cache ($saved)"
        }
        $result.Ffmpeg  = $ffmpeg
        $result.FfmpegVersion = Get-FfmpegBuildVersion $state.Line
    }
    catch {
        $result.Errors += "ffmpeg: $_"
        Write-Warn2 "ffmpeg not acquired: $_"
    }

    return $result
}

# Throws unless -TonelabDir holds every file of $TonelabFiles with exactly the
# SHA-256 that tools/release/tonelab/tonelab.json pins.
function Test-TonelabPin {
    if (-not $TonelabDir) { throw 'No tonelab folder: pass -TonelabDir or set DOTCHARTER_TONELAB_DIR (or -SkipTonelab).' }
    $pin = Get-Content -Raw (Join-Path $TonelabPinDir 'tonelab.json') | ConvertFrom-Json
    foreach ($f in $TonelabFiles) {
        $path = Join-Path $TonelabDir $f
        if (-not (Test-Path -LiteralPath $path)) { throw "tonelab: $f is not in $TonelabDir." }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $pin.files.$f) { throw "tonelab: $f in $TonelabDir is not the pinned build $($pin.commit) (SHA-256 $hash). Update tools/release/tonelab/tonelab.json on purpose, or point -TonelabDir at the pinned build." }
    }
    return $pin.commit
}

# Copies the pinned tonelab files and the MIT licence into <publish>/tools/tonelab/.
# Re-checks the hashes first: a payload never carries an unpinned tonelab.
function Add-Tonelab([string]$PublishDir) {
    Test-TonelabPin | Out-Null
    # Not $tonelabDir: PowerShell names ignore case, so that would overwrite -TonelabDir.
    $shippedDir = Join-Path $PublishDir 'tools/tonelab'
    New-Item -ItemType Directory -Force -Path $shippedDir | Out-Null
    foreach ($f in $TonelabFiles) { Copy-Item (Join-Path $TonelabDir $f) (Join-Path $shippedDir $f) -Force }
    Copy-Item (Join-Path $TonelabPinDir 'LICENSE.txt') (Join-Path $shippedDir 'LICENSE.txt') -Force
    return $shippedDir
}

# Copy the acquired tools into <publish>/tools/ and write THIRD-PARTY-NOTICES.txt.
# Returns which of the three landed.
function Add-BundledTools([string]$PublishDir, $Redist) {
    $toolsDir = Join-Path $PublishDir 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

    # No ffprobe: the minimal build does not make one. AudioMetadataExtractor
    # reads tags with `ffmpeg -f ffmetadata` and the cover with ffmpeg alone, and
    # yt-dlp probes through ffmpeg when ffprobe is absent.
    $bundled = [pscustomobject]@{ Ffmpeg = $false; YtDlp = $false }
    if ($Redist.Ffmpeg  -and (Test-Path $Redist.Ffmpeg))  { Copy-Item $Redist.Ffmpeg  (Join-Path $toolsDir 'ffmpeg.exe')  -Force; $bundled.Ffmpeg  = $true }
    if ($ContestedFeatures -and $Redist.YtDlp -and (Test-Path $Redist.YtDlp)) {
        Assert-YtDlpPin $Redist.YtDlp
        Copy-Item $Redist.YtDlp (Join-Path $toolsDir 'yt-dlp.exe') -Force
        $bundled.YtDlp = $true
    }

    # The LGPL text and the build record (exact sources + configure line) that
    # Build-MinimalFfmpeg.ps1 wrote beside the exe.
    foreach ($f in 'ffmpeg-LICENSE.txt', 'ffmpeg-BUILDINFO.txt') {
        $src = Join-Path $RedistDir $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $toolsDir $f) -Force }
    }

    $ytDlpNotice = if ($ContestedFeatures) { @"

yt-dlp $($Redist.YtDlpVersion)
  License : The Unlicense (released into the public domain).
  Source  : https://github.com/yt-dlp/yt-dlp
"@ } else { '' }
    $notices = @"
DotCharter bundles the third-party command-line tools in this folder. DotCharter
uses them when present -- ffmpeg for audio transcoding (stem Opus cache,
audio import) and yt-dlp for URL-based audio import -- but is not a derivative of
them. Each is redistributed under its own license, below.

$(Format-FfmpegNotice $Redist.FfmpegVersion)
$ytDlpNotice
"@
    Set-Content -Path (Join-Path $toolsDir 'THIRD-PARTY-NOTICES.txt') -Value $notices -Encoding utf8

    return $bundled
}

# The ffmpeg block of THIRD-PARTY-NOTICES.txt. Names the EXACT build the payload
# carries (what `ffmpeg -version` reported) and where its LGPL corresponding
# source is: the unmodified upstream tarball plus the configure line in
# ffmpeg-BUILDINFO.txt. A pure function so -SelfTest can pin its text.
function Format-FfmpegNotice([string]$BuildVersion) {
    $v = $FfmpegInventory.ffmpegVersion
    $shown = if ($BuildVersion) { "ffmpeg $BuildVersion" } else { "ffmpeg $v" }
    return @"
ffmpeg
  Version : $shown (DotCharter's minimal static win64 build: only the codecs,
            formats and filters DotCharter uses)
  License : LGPL v2.1 or later (built without --enable-gpl / --enable-nonfree).
            The full license is in ffmpeg-LICENSE.txt in this folder. See
            https://www.ffmpeg.org/legal.html. Statically linked libraries:
            libvorbis + libogg (BSD), Opus (BSD), LAME (LGPL v2), zlib (zlib).
  Source offer : The corresponding source is the unmodified FFmpeg $v release,
            https://ffmpeg.org/releases/ffmpeg-$v.tar.xz, with the library
            releases and the exact configure line listed in ffmpeg-BUILDINFO.txt
            in this folder. All remain publicly available at those URLs.
"@
}

# ---------------------------------------------------------------------------
# Pack integrity: clean Release outputs, binaries that are binaries, and a
# launch of what ships. All three exist because of v0.59.0 (#137): a restart
# interrupted a pack while it wrote bin/Release + obj/Release, NTFS kept the
# allocation but not the data, 260 outputs (DotCharter.dll among them) were left
# the right size and all zeros, the next run's incremental build reused them,
# the zip validated (the updater rules check LAYOUT), and the packed
# DotCharter.exe died at launch with BadImageFormatException. A manual smoke run
# was the only thing that caught it.
# ---------------------------------------------------------------------------

# The project closure follows required references and, in plugin builds, the plugins.
# A public build excludes the plugins/ tree even when something is checked out there.
# A wildcard reference (DotCharter.UI's plugin references) expands to what exists.
# Missing references in the selected build mode are reported before cleanup.
function Get-ProjectClosure([string[]]$RootProjects, [switch]$IncludePlugins) {
    $pluginRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'plugins')) + [System.IO.Path]::DirectorySeparatorChar
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($root in $RootProjects) { $queue.Enqueue((Resolve-Path -LiteralPath $root).Path) }
    while ($queue.Count -gt 0) {
        $project = $queue.Dequeue()
        if (-not $seen.Add($project)) { continue }
        [xml]$xml = Get-Content -LiteralPath $project -Raw
        foreach ($ref in $xml.SelectNodes('//*[local-name()="ProjectReference"]')) {
            $include = $ref.GetAttribute('Include')
            if (-not $include) { continue }
            $path = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $project -Parent) $include))
            if (-not $IncludePlugins -and $path.StartsWith($pluginRoot, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($include.Contains('*')) {
                foreach ($match in @(Get-ChildItem -Path $path -File -ErrorAction SilentlyContinue)) { $queue.Enqueue($match.FullName) }
                continue
            }
            if (-not (Test-Path -LiteralPath $path)) { throw "ProjectReference '$include' in $project does not resolve ($path)." }
            $queue.Enqueue($path)
        }
    }
    return @($seen | Sort-Object)
}

# Deletes bin/Release and obj/Release of every project the release publishes,
# so this run compiles every byte it ships. obj/project.assets.json (restore
# state) lives one level up and is kept. A locked file (a running Release dev
# build) fails the pack HERE, loudly, rather than leaving a stale output behind.
function Clear-ReleaseBuildOutputs([string[]]$RootProjects) {
    $projects = Get-ProjectClosure $RootProjects -IncludePlugins:$WithPlugins
    $removed = 0
    foreach ($project in $projects) {
        $dir = Split-Path $project -Parent
        foreach ($sub in 'bin/Release', 'obj/Release') {
            $target = Join-Path $dir $sub
            if (-not (Test-Path -LiteralPath $target)) { continue }
            try { Remove-Item -LiteralPath $target -Recurse -Force }
            catch { throw "Could not delete $target (is a Release build of it running?): $($_.Exception.Message)" }
            $removed++
        }
    }
    Write-Ok "clean Release build: $removed bin/obj Release folder(s) deleted across $($projects.Count) projects"
}

# Every .dll/.exe must be a PE image that parses (and a managed one must have
# readable metadata); no file may be non-empty and all zeros. The rules are
# DotCharter.Services' ReleasePayloadIntegrity, pinned by
# ReleasePayloadIntegrityTests in the default gate; this only applies them.
# The same tool also refuses a native DLL/exe that imports a DLL which is neither
# shipped beside it nor a Windows system DLL (NativeImportAudit, #176: v1.0.0's
# juce_sharp.dll imported VCRUNTIME140/MSVCP140, which the zip did not carry).
function Test-PayloadBinaries([string]$PayloadDir, [string]$Label) {
    & dotnet run $BinaryCheckTool $PayloadDir
    if ($LASTEXITCODE -ne 0) {
        throw "The $Label payload carries corrupt binaries or native imports nothing satisfies (named above) - not packing it. " +
              'For a corrupt file delete the named build outputs (or bin/obj) and run the release again; ' +
              'for an import, link that runtime statically or ship the DLL beside the image.'
    }
}

# The StartupPhases: line out of a log folder, or $null. Pure over its input
# folder so -SelfTest can prove it reads the shape App.axaml.cs writes. Rolled
# files are read too: the line may land in any of them.
function Find-StartupPhasesLine([string]$LogDir) {
    if (-not (Test-Path -LiteralPath $LogDir)) { return $null }
    foreach ($log in Get-ChildItem -LiteralPath $LogDir -File -Filter '*.log*' | Sort-Object LastWriteTime -Descending) {
        try {
            # FileShare.ReadWrite: the app holds its log open while it runs.
            $stream = [System.IO.FileStream]::new($log.FullName, 'Open', 'Read', 'ReadWrite')
            try {
                $reader = [System.IO.StreamReader]::new($stream)
                while ($null -ne ($line = $reader.ReadLine())) {
                    $at = $line.IndexOf('StartupPhases:', [System.StringComparison]::Ordinal)
                    if ($at -ge 0) { return $line.Substring($at).Trim() }
                }
            }
            finally { $stream.Dispose() }
        }
        catch [System.IO.IOException] { continue }
    }
    return $null
}

# Kills a process and everything it spawned. DotCharter puts its children in a
# kill-on-close job, but the smoke does not rely on the app being healthy.
function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if (-not $Process -or $Process.HasExited) { return }
    & taskkill.exe /PID $Process.Id /T /F *> $null
    $global:LASTEXITCODE = 0
    if (-not $Process.WaitForExit(15000)) { Write-Warn2 "DotCharter.exe (pid $($Process.Id)) did not exit after taskkill." }
}

# Launches a payload's DotCharter.exe and fails unless it writes its
# StartupPhases: line (App.axaml.cs logs it once the main window is interactive)
# within $TimeoutSeconds and is still running $SettleSeconds later.
#
# It starts the TOP DotCharter.exe - the launcher users run (#413) - on a hidden
# desktop (Start-HiddenDesktopProcess, #419), so it can never take the
# foreground of whoever is at the machine; the launcher hands the same desktop to
# app\DotCharter.exe. The liveness checks watch that app process (the launcher's
# child), the whole tree is killed at the end, and the smoke FAILS if the
# foreground window ever belongs to either process.
#
# $PayloadDir is a SCRATCH copy and gets modified. The launch sets
# DOTCHARTER_DATA_ROOT to a folder inside it (#439), so config, profiles, logs AND
# every %LOCALAPPDATA% cache (PSARC audio, stems, waveforms, models) stay inside
# the copy — the developer's %APPDATA%\DotCharter and %LOCALAPPDATA%\DotCharter are
# never read or written. The config goes under that root, and ALSO beside the exe
# (portable), for a payload built before the override existed. Either way the
# audio engine opens WASAPI shared output, never the ASIO device someone may be
# using. Never point this at a folder that gets zipped.
function Invoke-LaunchSmoke([string]$PayloadDir, [int]$TimeoutSeconds, [int]$SettleSeconds) {
    $exe = Join-Path $PayloadDir 'DotCharter.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Launch smoke: no DotCharter.exe in $PayloadDir." }
    $smokeConfig = @'
{
  "AudioDriver": "WASAPI",
  "UpdateCheckOnStartup": false,
  "EnableFileLogging": true
}
'@
    $dataRoot = Join-Path $PayloadDir 'smoke-data'
    $redirectedConfigDir = Join-Path $dataRoot 'Roaming\DotCharter'
    New-Item -ItemType Directory -Force -Path $redirectedConfigDir | Out-Null
    Set-Content -LiteralPath (Join-Path $redirectedConfigDir 'appconfig.json') -Encoding utf8 -Value $smokeConfig
    Set-Content -LiteralPath (Join-Path $PayloadDir 'appconfig.json') -Encoding utf8 -Value $smokeConfig
    $logDirs = @((Join-Path $redirectedConfigDir 'logs'), (Join-Path $PayloadDir 'logs'))
    $stdout = Join-Path $PayloadDir 'smoke-stdout.txt'
    $stderr = Join-Path $PayloadDir 'smoke-stderr.txt'

    # A split install (#413): the top exe is the launcher, the app is its child.
    $split = Test-Path -LiteralPath (Join-Path $PayloadDir 'app\DotCharter.exe')

    Write-Step "Launch smoke: starting the packed DotCharter.exe on a hidden desktop (up to $TimeoutSeconds s for StartupPhases:, then $SettleSeconds s alive)"
    $foregroundBefore = [DotCharter.Tools.HiddenDesktop]::ForegroundWindow()
    $started = Get-Date
    $previousDataRoot = $env:DOTCHARTER_DATA_ROOT
    $env:DOTCHARTER_DATA_ROOT = $dataRoot
    try {
        $launch = Start-HiddenDesktopProcess -FilePath $exe -WorkingDirectory $PayloadDir `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    }
    finally {
        $env:DOTCHARTER_DATA_ROOT = $previousDataRoot
    }
    Write-Ok "started pid $($launch.Id) on desktop $($launch.DesktopName)$(if ($split) { ' (launcher; the app is its child)' })"
    $app = if ($split) { $null } else { $launch.Process }
    $failure = $null
    $phases = $null
    $stolenBy = $null
    try {
        $deadline = $started.AddSeconds($TimeoutSeconds)
        while (-not $phases) {
            if (-not $app) { $app = Get-ChildProcess -ParentId $launch.Id | Select-Object -First 1 }
            $foregroundPid = [DotCharter.Tools.HiddenDesktop]::ForegroundProcessId()
            if ($foregroundPid -ne 0 -and ($foregroundPid -eq $launch.Id -or ($app -and $foregroundPid -eq $app.Id))) { $stolenBy = $foregroundPid }
            if ($app -and $app.HasExited) {
                $failure = "app\DotCharter.exe EXITED (code $($app.ExitCode)) after {0:N1} s without writing StartupPhases:." -f ((Get-Date) - $started).TotalSeconds
                break
            }
            if (-not $app -and $launch.Process.HasExited) {
                $failure = "the launcher EXITED (code $($launch.Process.ExitCode)) after {0:N1} s without starting app\DotCharter.exe." -f ((Get-Date) - $started).TotalSeconds
                break
            }
            $phases = $logDirs | ForEach-Object { Find-StartupPhasesLine $_ } | Select-Object -First 1
            if ($phases) { break }
            if ((Get-Date) -gt $deadline) {
                $failure = "DotCharter.exe wrote no StartupPhases: line within $TimeoutSeconds s (still running: it hangs before its window is interactive)."
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $failure -and -not $app) { $app = Get-ChildProcess -ParentId $launch.Id | Select-Object -First 1 }
        if (-not $failure -and -not $app) {
            $failure = 'StartupPhases: was written, but no app\DotCharter.exe child of the launcher was found to watch.'
        }
        if (-not $failure) {
            $readyAfter = ((Get-Date) - $started).TotalSeconds
            if ($SettleSeconds -gt 0 -and $app.WaitForExit($SettleSeconds * 1000)) {
                $failure = "DotCharter.exe reached StartupPhases: and then EXITED (code $($app.ExitCode)) within $SettleSeconds s."
            }
            elseif ($split -and $launch.Process.HasExited) {
                $failure = "the launcher EXITED (code $($launch.Process.ExitCode)) while the app was still running; it must wait for the app."
            }
        }
        $foregroundPid = [DotCharter.Tools.HiddenDesktop]::ForegroundProcessId()
        if ($foregroundPid -ne 0 -and ($foregroundPid -eq $launch.Id -or ($app -and $foregroundPid -eq $app.Id))) { $stolenBy = $foregroundPid }
    }
    finally {
        if ($app -and $app.Id -ne $launch.Id) { Stop-ProcessTree $app }
        Stop-HiddenDesktopProcess $launch
    }
    $foregroundAfter = [DotCharter.Tools.HiddenDesktop]::ForegroundWindow()
    if (-not $failure -and $stolenBy) {
        $failure = "DotCharter (pid $stolenBy) owned the FOREGROUND window although it ran on a hidden desktop (#419)."
    }

    if ($failure) {
        Write-Host "    $failure" -ForegroundColor Red
        foreach ($file in @($stderr, $stdout) + @($logDirs | ForEach-Object { Get-ChildItem -LiteralPath $_ -File -ErrorAction SilentlyContinue } | ForEach-Object FullName)) {
            if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0) { continue }
            Write-Host "    --- last lines of $(Split-Path $file -Leaf) ---" -ForegroundColor DarkGray
            Get-Content -LiteralPath $file -Tail 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
        }
        throw "Launch smoke FAILED: $failure"
    }

    Write-Ok ("launch smoke: interactive after {0:N1} s and alive {1} s later" -f $readyAfter, $SettleSeconds)
    Write-Ok ("foreground window before/after the smoke: 0x{0:X} / 0x{1:X}{2}" -f $foregroundBefore, $foregroundAfter,
        $(if ($foregroundBefore -eq $foregroundAfter) { ' (unchanged)' } else { ' (changed by something else: never owned by DotCharter)' }))
    Write-Ok $phases
    if ($phases -notmatch '\br2r=yes\b') {
        Write-Warn2 'the packed build did not report r2r=yes - it is JIT-compiling its startup (see ReadyToRunProbe).'
    }
}

# Copies (folder) or extracts (zip) a payload into a scratch folder and returns
# the folder holding DotCharter.exe. The smoke then writes there, never into the
# input. A release zip wraps everything in one top-level folder.
function New-SmokeCopy([string]$Source, [string]$ScratchRoot) {
    if (Test-Path $ScratchRoot) { Remove-Item $ScratchRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
    if ((Test-Path -LiteralPath $Source -PathType Leaf) -and $Source -like '*.zip') {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Source, $ScratchRoot)
    }
    elseif (Test-Path -LiteralPath $Source -PathType Container) {
        Copy-Item -LiteralPath $Source -Destination (Join-Path $ScratchRoot (Split-Path $Source -Leaf)) -Recurse
    }
    else { throw "Not a payload folder or a .zip: $Source" }
    # The shallowest DotCharter.exe: the launcher of a split install (#413), not
    # the app inside its app\ folder.
    $exe = Get-ChildItem -LiteralPath $ScratchRoot -Recurse -Filter 'DotCharter.exe' -File |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $exe) { throw "No DotCharter.exe inside $Source." }
    return $exe.DirectoryName
}

# The whole post-zip check: unpack what ships, check its binaries, launch it.
function Invoke-PackSmoke([string]$Source, [string]$ScratchRoot) {
    $payload = New-SmokeCopy -Source $Source -ScratchRoot $ScratchRoot
    try {
        Test-PayloadBinaries -PayloadDir $payload -Label 'packed UI'
        Invoke-LaunchSmoke -PayloadDir $payload -TimeoutSeconds $SmokeTimeoutSeconds -SettleSeconds $SmokeSettleSeconds
    }
    finally {
        Remove-Item $ScratchRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Self-test: prove the zip-layout logic + validator without building anything.
# ---------------------------------------------------------------------------
function Invoke-SelfTest {
    Assert-ReleaseVersionSelfTest
    Write-Step 'Self-test: zip layout + AppPayload validation'
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "dotcharter-release-selftest-$([guid]::NewGuid().ToString('N'))"
    $rootName = 'DotCharter-v0.0.0-selftest-win-x64'
    $payload = Join-Path $work $rootName
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    try {
        # Minimal file set AppPayload.Validate insists on for a self-contained payload.
        Set-Content -Path (Join-Path $payload 'DotCharter.exe') -Value 'stub'
        Set-Content -Path (Join-Path $payload 'DotCharter.deps.json') -Value '{}'
        Set-Content -Path (Join-Path $payload 'hostfxr.dll') -Value 'stub'
        Set-Content -Path (Join-Path $payload 'coreclr.dll') -Value 'stub'
        Set-Content -Path (Join-Path $payload 'DotCharter.runtimeconfig.json') -Value @'
{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" } ] } }
'@

        # 1) The zip this script produces must pass.
        $goodZip = Join-Path $work 'good.zip'
        New-FolderWrappedZip -Directory $payload -ZipPath $goodZip
        Test-ReleaseZip -ZipPath $goodZip -ExpectedRoot "$rootName/"
        Write-Ok 'good zip (ZipFile.CreateFromDirectory, folder-wrapped) PASSES validation'

        # 1a) Plugins: the clean payload and its zip pass, the same payload with a
        #     plugin assembly planted in app/ is refused both ways.
        $plantedPlugin = 'Example.Plugin.dll'
        Assert-NoPluginInPayload $payload @($plantedPlugin)
        Assert-NoPluginInPayload $goodZip @($plantedPlugin)
        $planted = Join-Path $work 'planted'
        New-Item -ItemType Directory -Force -Path $planted | Out-Null
        Copy-Item $payload (Join-Path $planted $rootName) -Recurse
        New-Item -ItemType Directory -Force -Path (Join-Path $planted "$rootName/app") | Out-Null
        Set-Content -Path (Join-Path $planted "$rootName/app/$plantedPlugin") -Value 'stub'
        $plantedZip = Join-Path $work 'planted.zip'
        New-FolderWrappedZip -Directory (Join-Path $planted $rootName) -ZipPath $plantedZip
        foreach ($target in $planted, $plantedZip) {
            $refused = $false
            try { Assert-ReleasePayloadPlugins $target @($plantedPlugin) } catch { $refused = $true }
            if (-not $refused) { throw "Self-test FAILED: a payload carrying $plantedPlugin was not refused ($target)." }
        }
        Write-Ok "a payload or zip carrying a plugin assembly is REFUSED; the clean one passes"
        # ...and -WithPlugins the other way round: the planted one passes, the clean one is refused.
        Assert-ReleasePayloadPlugins $planted @($plantedPlugin) -WithPlugins
        Assert-ReleasePayloadPlugins $plantedZip @($plantedPlugin) -WithPlugins
        foreach ($target in $payload, $goodZip) {
            $refused = $false
            try { Assert-ReleasePayloadPlugins $target @($plantedPlugin) -WithPlugins } catch { $refused = $true }
            if (-not $refused) { throw "Self-test FAILED: a -WithPlugins payload WITHOUT $plantedPlugin was not refused ($target)." }
        }
        Write-Ok "-WithPlugins: a payload or zip WITHOUT the plugin is REFUSED; the one carrying it passes"

        # 1b) The SPLIT layout (#413): app/ validated, the top passing the v0.62
        #     updater's flat rule through LegacyUpdaterShim's copies - and the same
        #     payload WITHOUT those copies must FAIL, or old installs are stranded.
        $splitName = 'DotCharter-v0.0.0-split-win-x64'
        $split = Join-Path $work $splitName
        New-Item -ItemType Directory -Force -Path (Join-Path $split 'app') | Out-Null
        foreach ($file in Get-ChildItem $payload -File) { Copy-Item $file.FullName (Join-Path $split 'app') }
        Set-Content -Path (Join-Path $split 'app/payload-manifest.json') -Value '{}'
        Set-Content -Path (Join-Path $split 'DotCharter.exe') -Value 'launcher'
        foreach ($name in 'DotCharter.runtimeconfig.json', 'DotCharter.deps.json', 'hostfxr.dll', 'coreclr.dll') {
            Copy-Item (Join-Path $split "app/$name") (Join-Path $split $name)
        }
        $splitZip = Join-Path $work 'split.zip'
        New-FolderWrappedZip -Directory $split -ZipPath $splitZip
        Test-ReleaseZip -ZipPath $splitZip -ExpectedRoot "$splitName/"
        Write-Ok 'split zip (launcher + app/ + the v0.62 shim) PASSES validation'

        Remove-Item (Join-Path $split 'hostfxr.dll')
        New-FolderWrappedZip -Directory $split -ZipPath $splitZip
        & dotnet run $Validator $splitZip
        if ($LASTEXITCODE -eq 0) {
            throw 'Self-test FAILED: a split zip without the v0.62 shim passed validation - old installs could not update to it.'
        }
        $global:LASTEXITCODE = 0
        Write-Ok 'split zip WITHOUT the v0.62 shim is REJECTED, as required'

        # 2) A backslash-entry zip (what Compress-Archive can emit) must FAIL —
        #    built deterministically so the test does not depend on the local
        #    Microsoft.PowerShell.Archive version.
        $badZip = Join-Path $work 'bad.zip'
        $stream = [System.IO.File]::Open($badZip, [System.IO.FileMode]::CreateNew)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($file in Get-ChildItem $payload -File) {
                    $entry = $archive.CreateEntry("$rootName\$($file.Name)")   # backslash on purpose
                    $entryStream = $entry.Open()
                    try {
                        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
                        $entryStream.Write($bytes, 0, $bytes.Length)
                    } finally { $entryStream.Dispose() }
                }
            } finally { $archive.Dispose() }
        } finally { $stream.Dispose() }

        & dotnet run $Validator $badZip
        if ($LASTEXITCODE -eq 0) {
            throw 'Self-test FAILED: a backslash-entry zip passed validation — AppPayload rules changed?'
        }
        Write-Ok 'backslash-entry zip (Compress-Archive shape) is REJECTED, as required'

        # 2b) The binary check (CheckPayloadBinaries.cs over ReleasePayloadIntegrity).
        #     Real PE images must PASS — a check that refuses a healthy payload
        #     blocks every release — and the #137 shape must FAIL by name: a
        #     zero-filled DotCharter.dll, beside a text stub where an .exe belongs.
        $binaries = Join-Path $work 'binaries'
        New-Item -ItemType Directory -Force -Path (Join-Path $binaries 'tools') | Out-Null
        Copy-Item ([object].Assembly.Location) (Join-Path $binaries 'System.Private.CoreLib.dll')   # managed, R2R
        Copy-Item (Get-Process -Id $PID).Path (Join-Path $binaries 'tools/ffmpeg.exe')             # native
        Set-Content -LiteralPath (Join-Path $binaries 'DotCharter.deps.json') -Value '{}'
        Test-PayloadBinaries -PayloadDir $binaries -Label 'self-test (healthy)'
        Write-Ok 'binary check PASSES real managed + native PE images'

        [System.IO.File]::WriteAllBytes((Join-Path $binaries 'DotCharter.dll'), [byte[]]::new(1MB))
        Set-Content -LiteralPath (Join-Path $binaries 'DotCharter.exe') -Value 'stub'
        $report = @(& dotnet run $BinaryCheckTool $binaries 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
        $global:LASTEXITCODE = 0
        $zeroNamed = @($report -match '^\s*AllZeros\s+DotCharter\.dll\s').Count -eq 1
        $stubNamed = @($report -match '^\s*NotPortableExecutable\s+DotCharter\.exe\s').Count -eq 1
        if ($code -ne 1 -or -not $zeroNamed -or -not $stubNamed) {
            throw "Self-test FAILED: the binary check did not refuse a zero-filled DotCharter.dll and a stub DotCharter.exe by name (exit $code):`n$($report -join "`n")"
        }
        Write-Ok 'binary check REFUSES a zero-filled DotCharter.dll and a text-stub DotCharter.exe, naming both'

        # 2c) The launch smoke's log reader, against the shape App.axaml.cs writes.
        $logs = Join-Path $work 'logs'
        New-Item -ItemType Directory -Force -Path $logs | Out-Null
        if ($null -ne (Find-StartupPhasesLine $logs)) { throw 'Self-test FAILED: an empty log folder produced a StartupPhases line.' }
        Set-Content -LiteralPath (Join-Path $logs 'dotcharter.log') -Value @(
            '2026-10-05 01:58:01.002 [INF] [Application] DotCharter 0.0.0 starting',
            '2026-10-05 01:58:03.123 [INF] [Application] StartupPhases: di=41 services=180 vmGraph=212 keybinds=3 windowCtor=388 bind=20 windowConstructed=844 opened=1306 interactive=1490 r2r=yes gcHold=ok')
        $line = Find-StartupPhasesLine $logs
        if ($line -notlike 'StartupPhases: di=41*r2r=yes*') { throw "Self-test FAILED: the StartupPhases line was not read back (got '$line')." }
        Write-Ok 'launch smoke reads the StartupPhases: line, and finds none in an empty log folder'

        # 2c1) The hidden desktop (#419): a child that opens and ACTIVATES a window
        #      runs on the desktop the helper created, never owns the foreground,
        #      and the desktop handle is released afterwards. The probe opens its
        #      window only on a desktop the helper named, so a regressed helper
        #      fails this check without ever showing a window to the person at
        #      the machine.
        $probe = Join-Path $work 'hidden-desktop-probe.ps1'
        $probeOut = Join-Path $work 'hidden-desktop-probe.out.txt'
        $probeErr = Join-Path $work 'hidden-desktop-probe.err.txt'
        Set-Content -LiteralPath $probe -Value @'
param([string]$Module)
Import-Module $Module
$desktop = [DotCharter.Tools.HiddenDesktop]::ThreadDesktopName()
$window = 0
if ($desktop -like 'dotcharter-hidden-*') {
    Add-Type -AssemblyName System.Windows.Forms
    $form = [System.Windows.Forms.Form]::new()
    $form.TopMost = $true
    $form.Show()
    $form.Activate()
    [System.Windows.Forms.Application]::DoEvents()
    $window = $form.Handle.ToInt64()
    Start-Sleep -Milliseconds 300
    $form.Close()
}
"$desktop|$window"
'@
        $foregroundBefore = [DotCharter.Tools.HiddenDesktop]::ForegroundWindow()
        $probeLaunch = Start-HiddenDesktopProcess -FilePath (Get-Process -Id $PID).Path `
            -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$probe`" -Module `"$(Join-Path $PSScriptRoot 'HiddenDesktop.psm1')`"" `
            -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr
        $probeOwnedForeground = $false
        try {
            $probeDeadline = (Get-Date).AddSeconds(90)
            while (-not $probeLaunch.Process.HasExited -and (Get-Date) -lt $probeDeadline) {
                if ([DotCharter.Tools.HiddenDesktop]::ForegroundProcessId() -eq $probeLaunch.Id) { $probeOwnedForeground = $true }
                Start-Sleep -Milliseconds 100
            }
            if (-not $probeLaunch.Process.HasExited) { throw 'Self-test FAILED: the hidden-desktop probe did not exit within 90 s.' }
        }
        finally { Stop-HiddenDesktopProcess $probeLaunch }
        $foregroundAfter = [DotCharter.Tools.HiddenDesktop]::ForegroundWindow()
        $probeReport = "$(Get-Content -LiteralPath $probeOut -Raw -ErrorAction SilentlyContinue)".Trim()
        $probeDesktop, $probeWindow = $probeReport -split '\|', 2
        if ($probeDesktop -ne $probeLaunch.DesktopName) {
            throw "Self-test FAILED: the probe ran on desktop '$probeDesktop', not on the hidden desktop '$($probeLaunch.DesktopName)' (stderr: $(Get-Content -LiteralPath $probeErr -Raw -ErrorAction SilentlyContinue))."
        }
        if ([long]$probeWindow -eq 0) { throw "Self-test FAILED: the probe opened no window on the hidden desktop (got '$probeReport')." }
        if ($probeOwnedForeground) { throw "Self-test FAILED: a window on the hidden desktop took the FOREGROUND (pid $($probeLaunch.Id))." }
        if ($probeLaunch.DesktopHeld) { throw 'Self-test FAILED: Stop-HiddenDesktopProcess did not release the desktop handle.' }
        Write-Ok ("hidden desktop: a child that opened and activated a window ran on {0}, never in the foreground (0x{1:X} before / 0x{2:X} after{3})" -f `
            $probeLaunch.DesktopName, $foregroundBefore, $foregroundAfter, $(if ($foregroundBefore -eq $foregroundAfter) { ', unchanged' } else { ', changed by something else' }))

        # 2c2) The trim check: refuses a whole-app trim's runtimeconfig and an untrimmed binding.
        $trimDir = Join-Path $work 'trim'
        New-Item -ItemType Directory -Force -Path $trimDir | Out-Null
        $cfgPath = Join-Path $trimDir 'DotCharter.runtimeconfig.json'
        Set-Content -LiteralPath $cfgPath -Value '{ "runtimeOptions": { "configProperties": { "System.GC.Concurrent": true } } }'
        [System.IO.File]::WriteAllBytes((Join-Path $trimDir 'Silk.NET.Direct3D11.dll'), [byte[]]::new(40KB))
        Test-PublishTrimSettings -PublishDir $trimDir
        Set-Content -LiteralPath $cfgPath -Value '{ "runtimeOptions": { "configProperties": { "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault": false } } }'
        $refused = $false
        try { Test-PublishTrimSettings -PublishDir $trimDir } catch { $refused = "$_" -like '*IsReflectionEnabledByDefault*' }
        if (-not $refused) { throw 'Self-test FAILED: the trim check accepted a runtimeconfig.json with the JSON reflection switch off.' }
        Set-Content -LiteralPath $cfgPath -Value '{ "runtimeOptions": { "configProperties": { "System.GC.Concurrent": true } } }'
        [System.IO.File]::WriteAllBytes((Join-Path $trimDir 'Silk.NET.Direct3D11.dll'), [byte[]]::new(2MB))
        $refused = $false
        try { Test-PublishTrimSettings -PublishDir $trimDir } catch { $refused = "$_" -like '*Silk.NET.Direct3D11.dll is*' }
        if (-not $refused) { throw 'Self-test FAILED: the trim check accepted an untrimmed Silk.NET.Direct3D11.dll.' }
        Write-Ok 'trim check passes an untrimmed-app runtimeconfig, refuses the JSON reflection switch and a full-size D3D11 binding'

        # 2d) What the clean Release build deletes: the publish closure, not the tests.
        $closure = @(Get-ProjectClosure @((Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj')) | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) })
        foreach ($expected in 'DotCharter.UI', 'DotCharter.Services', 'DotCharter.Core', 'DotCharter.Tone', 'JuceSharp') {
            if ($closure -notcontains $expected) { throw "Self-test FAILED: the publish closure of DotCharter.UI misses $expected (got: $($closure -join ', '))." }
        }
        if ($closure -contains 'DotCharter.Tests') { throw 'Self-test FAILED: the publish closure of DotCharter.UI reached DotCharter.Tests.' }
        Write-Ok "clean-build closure of DotCharter.UI: $($closure.Count) projects, DotCharter.Tests not among them"

        # 2e) The ffmpeg notice names the exact build, the LGPL and its source.
        $notice = Format-FfmpegNotice '8.1.3-dotcharter-min1'
        if ($notice -notmatch 'ffmpeg 8\.1\.3-dotcharter-min1' -or $notice -notmatch 'LGPL v2\.1 or later' -or
            $notice -notmatch [regex]::Escape("https://ffmpeg.org/releases/ffmpeg-$($FfmpegInventory.ffmpegVersion).tar.xz") -or
            $notice -match 'GPLv3') {
            throw "Self-test FAILED: the ffmpeg notice does not name the build, its LGPL licence and its source:`n$notice"
        }
        Write-Ok 'ffmpeg notice names the bundled build, its LGPL licence and the source tarball'

        # 2f) The ffmpeg inventory check (#409) reads ffmpeg's listings and fires
        #     on a missing component: proved both ways against canned output, so
        #     a parser that silently finds nothing (or everything) fails here.
        $inv = [pscustomobject]@{
            decoders = @('aac'); encoders = @('libopus'); muxers = @('ogg'); demuxers = @('mov', 'dds_pipe')
            filters = @('adelay'); protocols = @('pipe'); bsfs = @('aac_adtstoasc'); parsers = @('aac')
            externalLibraries = @('libopus')
        }
        $listings = @{
            decoders  = ConvertFrom-FfmpegListing 'decoders' @('Decoders:', ' A..... = Audio', ' ------', ' A....D aac                  AAC (Advanced Audio Coding)')
            encoders  = ConvertFrom-FfmpegListing 'encoders' @('Encoders:', ' ------', ' A....D libopus              libopus Opus')
            muxers    = ConvertFrom-FfmpegListing 'muxers'   @('Formats:', ' D. = Demuxing supported', ' ---', '  E ogg             Ogg')
            demuxers  = ConvertFrom-FfmpegListing 'demuxers' @('Formats:', ' ---', ' D  mov,mp4,m4a,3gp,3g2,mj2 QuickTime / MOV')
            filters   = ConvertFrom-FfmpegListing 'filters'  @('Filters:', '  T.. = Timeline support', ' ... adelay            A->A       Delay one or more audio channels.')
            protocols = ConvertFrom-FfmpegListing 'protocols' @('Supported file protocols:', 'Input:', '  file', '  pipe', 'Output:', '  pipe')
            bsfs      = ConvertFrom-FfmpegListing 'bsfs'     @('Bitstream filters:', 'aac_adtstoasc')
        }
        # -buildconf quotes each list, and -demuxers hides the image-pipe demuxers.
        $conf = "    --disable-everything`n    --enable-libopus`n    --enable-demuxer='dds_pipe,mov'`n    --enable-parser='aac,flac'"
        $none = @(Compare-FfmpegInventory $inv $listings $conf)
        if ($none.Count -ne 0) { throw "Self-test FAILED: a complete ffmpeg was reported missing $($none -join ', ')." }
        $inv.decoders = @('aac', 'flac'); $inv.externalLibraries = @('libopus', 'libvorbis')
        $two = @(Compare-FfmpegInventory $inv $listings $conf)
        if (($two -join ',') -ne 'decoders:flac,externalLibraries:libvorbis') {
            throw "Self-test FAILED: the inventory check did not name the missing decoder + library (got: $($two -join ', '))."
        }
        if (@(Get-FfmpegConfigureComponentArgs $FfmpegInventory) -notcontains "--enable-encoder=$((@($FfmpegInventory.encoders) | Sort-Object -Unique) -join ',')") {
            throw 'Self-test FAILED: the configure flags do not carry the inventory encoders.'
        }
        Write-Ok 'ffmpeg inventory check: a complete build passes, a missing decoder and library are named'

        # 3) The executed-vs-discovered check (Test-HeadlessCoverage). A run of
        #    the headless suite that quietly executes a third of itself reports
        #    "0 failed" and reads as green — that has happened (223 of 330).
        #    Proved in BOTH directions here, against a hand-written trx, because
        #    a coverage check that never fires is worth nothing and one that
        #    always fires blocks every release.
        $trxPath = Join-Path $work 'coverage.trx'
        Set-Content -LiteralPath $trxPath -Encoding utf8 -Value @'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="DotCharter.Tests.Perf.AlphaUiPerfSmokeTests.It_ran" outcome="Passed" />
    <UnitTestResult testName="DotCharter.Tests.Perf.AlphaUiPerfSmokeTests.Theory_ran(x: 1)" outcome="Passed" />
  </Results>
  <ResultSummary><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary>
</TestRun>
'@
        $fakeRun = Read-TrxResults $trxPath

        $script:DiscoveryCache['selftest-missing'] = @(
            'DotCharter.Tests.Perf.AlphaUiPerfSmokeTests',
            'DotCharter.Tests.Perf.BetaUiPerfSmokeTests')
        $missing = @(Test-HeadlessCoverage -Filter 'selftest-missing' -ResultsDir $work -Run $fakeRun)
        if ($missing.Count -ne 1 -or $missing[0] -ne 'DotCharter.Tests.Perf.BetaUiPerfSmokeTests') {
            throw "Self-test FAILED: a discovered class that reported NOTHING was not flagged (got: '$($missing -join ', ')')."
        }

        $script:DiscoveryCache['selftest-complete'] = @('DotCharter.Tests.Perf.AlphaUiPerfSmokeTests')
        $clean = @(Test-HeadlessCoverage -Filter 'selftest-complete' -ResultsDir $work -Run $fakeRun)
        if ($clean.Count -ne 0) {
            throw "Self-test FAILED: the coverage check cried wolf on a complete run (got: '$($clean -join ', ')')."
        }
        Write-Ok 'executed-vs-discovered check fires on a class that never reported, and only then'

        # 4) The SHARDED run (tools/ci/HeadlessShards.psm1). The plan must hand
        #    every discovered class to exactly one shard, and a shard that dies
        #    without a TRX must surface as ABSENT classes through the very same
        #    Test-HeadlessCoverage — never as a smaller green run.
        $counts = @{
            'DotCharter.Tests.Perf.AlphaUiPerfSmokeTests' = 2
            'DotCharter.Tests.Perf.BetaUiPerfSmokeTests'  = 3
            'DotCharter.Tests.Perf.GammaUiPerfSmokeTests' = 1
            'DotCharter.Tests.Perf.NewUiPerfSmokeTests'   = 4   # not in the weights: a NEW class
        }
        $weights = @{
            'DotCharter.Tests.Perf.AlphaUiPerfSmokeTests' = 10.0
            'DotCharter.Tests.Perf.BetaUiPerfSmokeTests'  = 30.0
            'DotCharter.Tests.Perf.GammaUiPerfSmokeTests' = 5.0
        }
        $plan = @(New-HeadlessShardPlan $counts $weights 2)
        $assigned = @($plan | ForEach-Object { $_.Classes } | Sort-Object)
        if ($plan.Count -ne 2 -or ($assigned -join ',') -ne (@($counts.Keys | Sort-Object) -join ',')) {
            throw "Self-test FAILED: the shard plan did not hand every class to exactly one shard (got: $($assigned -join ', '))."
        }
        $survivor = Join-Path $work 'shard-ok.trx'
        Set-Content -LiteralPath $survivor -Encoding utf8 -Value @'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="DotCharter.Tests.Perf.AlphaUiPerfSmokeTests.It_ran" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
</TestRun>
'@
        # One shard reported only Alpha (its other class died before reporting);
        # the other shard wrote no TRX at all (crashed, or killed by the detector).
        $merged = Merge-HeadlessTrx @($survivor, $null, (Join-Path $work 'shard-crashed.trx')) (Join-Path $work 'merged.trx')
        $script:DiscoveryCache['selftest-sharded'] = @($counts.Keys | Sort-Object)
        $absentAfterCrash = @(Test-HeadlessCoverage -Filter 'selftest-sharded' -ResultsDir $work -Run (Read-TrxResults $merged))
        $expectAbsent = @($counts.Keys | Where-Object { $_ -ne 'DotCharter.Tests.Perf.AlphaUiPerfSmokeTests' } | Sort-Object)
        if ((@($absentAfterCrash | Sort-Object) -join ',') -ne ($expectAbsent -join ',')) {
            throw "Self-test FAILED: classes of a shard that wrote no TRX were not all flagged absent (got: '$($absentAfterCrash -join ', ')')."
        }
        Write-Ok 'sharded run: every class planned exactly once, and a crashed shard''s classes are flagged absent'

        Write-Host 'Self-test PASSED.' -ForegroundColor Green
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Release gate: the headless UI suite
# ---------------------------------------------------------------------------
# WHY THIS IS HERE. Five classes of XAML fault build perfectly clean and fail
# only at runtime — an unresolvable StaticResource, a StringFormat given a
# Binding, a RelayCommand<int> fed a literal CommandParameter, a view that
# fails to construct and renders as "Not Found: …", and a Thickness property
# given an x:Double Space* token. The only thing in this repo that catches them
# is the headless UI suite, and it cannot share a testhost with the default
# gate: Avalonia.Headless owns a process-global dispatcher, so an unfiltered run
# fails 314 of the suite's cases plus one unrelated gate test (see
# docs/agents/testing/perf-harnesses.md). It therefore needs its OWN
# `dotnet test` invocation — which meant it had no home in the commit protocol
# and nobody ran it. Failures rotted into shipped releases exactly that way;
# DotCharter.Tests/AGENTS.md keeps the list.
#
# This is that home. A release is not cut with this suite red.
# xunit v3 simple-filter arguments (Microsoft.Testing.Platform runner); repeat
# --filter-class to OR several classes, '*' wildcards at either end.
#
# The golden screenshots (ScreenGoldenUiPerfSmokeTests) are excluded by their EXACT
# name: they need Skia drawing and are their own invocation
# (tools/agent/Invoke-Gate.ps1 -Goldens). A release step for them is a TODO in
# docs/agents/ui/target-architecture.md section 5.
$HeadlessUiFilter = @('--filter-class', '*UiPerfSmokeTests', '--filter-not-class', 'DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests')

# …and it is not cut with the suite red in GERMAN or JAPANESE either.
#
# The suite seeds its throwaway config with a culture (HeadlessSessionFixture,
# DOTCHARTER_HEADLESS_CULTURE, en-US when unset). That pin is what stopped the
# run following the developer's Windows language — and it also meant every
# non-English layout went unswept: the first de-DE run turned up EIGHT real
# faults (two settings rows arranged past a fixed 300 px control column, six
# labels sliced with no wrap and no ellipsis), all of them invisible in English
# because German prose runs ~40 % longer.
#
# COST is why this is not three full runs. One pass was ~7-8 minutes in ONE
# process (~2.5-3 sharded over four since 2026-10-04), so all three cultures
# would be ~22 minutes of preflight before a single byte is published. The
# localized passes run only the classes that compare a width or a height — the
# ones a language can change — and skip the rest (wiring, prewarm, routing,
# colour, the "it measured something" construction smoke).
#
# The list was hand-kept until #665 (21 classes); DotCharter.Tests'
# Infrastructure/HeadlessLayoutFilterLintTests now fails the gate when a class
# that compares a size is neither here nor in its NotCultureLayout list (with a
# reason), or when an entry here compares none. Seeding it added 49 classes.
# MEASURED 2026-10-11 for those 49 in ONE process on eight low-priority cores:
# ja-JP 87 s, de-DE 70 s of test time (496 cases each), before sharding over
# -HeadlessShards. 2026-08-28, for the first two classes:
# en-US full 7 m 46 s, de-DE layout 1 m 12 s, ja-JP layout 1 m 56 s.
#
# If a third localized culture ships, add it to $HeadlessSweepCultures. Entries
# are '*<Class>' — the lint reads them by that shape.
$HeadlessLayoutFilter = @(
    '--filter-class', '*UiSweepUiPerfSmokeTests',
    '--filter-class', '*AlignToRecordingDialogUiPerfSmokeTests',
    '--filter-class', '*SettingsConstructionUiPerfSmokeTests',
    '--filter-class', '*LinkedMeasuresConstructionUiPerfSmokeTests',
    '--filter-class', '*LinkDialogsUiPerfSmokeTests',
    '--filter-class', '*StatusRailNarrowWidthUiPerfSmokeTests',
    '--filter-class', '*InspectorNarrowWidthUiPerfSmokeTests',
    '--filter-class', '*EditorShortWindowUiPerfSmokeTests',
    '--filter-class', '*TimelineLaneMenuUiPerfSmokeTests',
    '--filter-class', '*LibraryDetailRailUiPerfSmokeTests',
    '--filter-class', '*LibraryTuningPopoverUiPerfSmokeTests',
    '--filter-class', '*SongTableColumnsUiPerfSmokeTests',
    '--filter-class', '*PlayModeSongListColumnsUiPerfSmokeTests',
    '--filter-class', '*PlaySetlistUiPerfSmokeTests',
    '--filter-class', '*DialogWindowFitUiPerfSmokeTests',
    '--filter-class', '*RocksmithExportLayoutUiPerfSmokeTests',
    '--filter-class', '*OnboardingSystemCheckUiPerfSmokeTests',
    '--filter-class', '*ChordShapesConstructionUiPerfSmokeTests',
    '--filter-class', '*SoundTabRailUiPerfSmokeTests',
    '--filter-class', '*AiChatPaneConstructionUiPerfSmokeTests',
    '--filter-class', '*AiChatDockUiPerfSmokeTests',
    '--filter-class', '*InspectorTabStripUiPerfSmokeTests',
    '--filter-class', '*AiChartReviewSizesUiPerfSmokeTests',
    '--filter-class', '*AiChartReviewConstructionUiPerfSmokeTests',
    '--filter-class', '*AiChartReviewTonesUiPerfSmokeTests',
    '--filter-class', '*DraftMergeReviewConstructionUiPerfSmokeTests',
    '--filter-class', '*LiveDetectionMonitorConstructionUiPerfSmokeTests',
    '--filter-class', '*GetReadyDropTunerUiPerfSmokeTests',
    '--filter-class', '*AlignBeatGridDialogUiPerfSmokeTests',
    '--filter-class', '*DcSongExportDialogUiPerfSmokeTests',
    '--filter-class', '*AmpFxRackUiPerfSmokeTests',
    '--filter-class', '*ChartFromAudioStemStripUiPerfSmokeTests',
    '--filter-class', '*ChartSynthInstrumentUiPerfSmokeTests',
    '--filter-class', '*ChartSynthPlayStyleUiPerfSmokeTests',
    '--filter-class', '*CommandPaletteUiPerfSmokeTests',
    '--filter-class', '*DialogConstructionUiPerfSmokeTests',
    '--filter-class', '*RigToneImportReviewUiPerfSmokeTests',
    '--filter-class', '*EditorPaneConstructionUiPerfSmokeTests',
    '--filter-class', '*EqModulePixelUiPerfSmokeTests',
    '--filter-class', '*FireComponentUiPerfSmokeTests',
    '--filter-class', '*FireControlsUiPerfSmokeTests',
    '--filter-class', '*FireFieldUiPerfSmokeTests',
    '--filter-class', '*HomeViewUiPerfSmokeTests',
    '--filter-class', '*InspectorConstructionUiPerfSmokeTests',
    '--filter-class', '*InterfaceScaleUiPerfSmokeTests',
    '--filter-class', '*KeybindsMidiUiPerfSmokeTests',
    '--filter-class', '*LaunchScreenCaptureUiPerfSmokeTests',
    '--filter-class', '*LibraryConstructionUiPerfSmokeTests',
    '--filter-class', '*PlayModeFlowRedesignUiPerfSmokeTests',
    '--filter-class', '*PlayModeScreensUiPerfSmokeTests',
    '--filter-class', '*PopoverPixelUiPerfSmokeTests',
    '--filter-class', '*PopoverShellUiPerfSmokeTests',
    '--filter-class', '*RigAmpFaceUiPerfSmokeTests',
    '--filter-class', '*RigAmpliTubeLayoutUiPerfSmokeTests',
    '--filter-class', '*RigBoardFaceplateUiPerfSmokeTests',
    '--filter-class', '*RigBoardWiresUiPerfSmokeTests',
    '--filter-class', '*RigCabinetEditorUiPerfSmokeTests',
    '--filter-class', '*RigCanvasOwnershipUiPerfSmokeTests',
    '--filter-class', '*RigEditorCaptureUiPerfSmokeTests',
    '--filter-class', '*RigFaceplateUiPerfSmokeTests',
    '--filter-class', '*RigFamilyEditorsUiPerfSmokeTests',
    '--filter-class', '*RigFooterStabilityUiPerfSmokeTests',
    '--filter-class', '*RigHardwareUiPerfSmokeTests',
    '--filter-class', '*RigNamRowTemplatesUiPerfSmokeTests',
    '--filter-class', '*RigNodeMetersLayoutUiPerfSmokeTests',
    '--filter-class', '*RigPedalEnclosureUiPerfSmokeTests',
    '--filter-class', '*RigReampUiPerfSmokeTests',
    '--filter-class', '*RigRoutingConsoleUiPerfSmokeTests',
    '--filter-class', '*RigRsAmpUiPerfSmokeTests',
    '--filter-class', '*RigSceneBarUiPerfSmokeTests',
    '--filter-class', '*RigTonesModeUiPerfSmokeTests',
    '--filter-class', '*RigTonesRedesignUiPerfSmokeTests',
    '--filter-class', '*SettingsScrollAnchorUiPerfSmokeTests',
    '--filter-class', '*ShellConstructionUiPerfSmokeTests',
    '--filter-class', '*SongSelectRedesignUiPerfSmokeTests',
    '--filter-class', '*SongToneEditBarUiPerfSmokeTests',
    '--filter-class', '*ToneDesignManualRouteUiPerfSmokeTests',
    '--filter-class', '*ToneDesignerSlotEditorUiPerfSmokeTests',
    '--filter-class', '*ToneDesignerGearSearchUiPerfSmokeTests',
    '--filter-class', '*UiBreakpointUiPerfSmokeTests',
    '--filter-class', '*UiResolutionAuditUiPerfSmokeTests')
$HeadlessSweepCultures = @('de-DE', 'ja-JP')

# The Avalonia headless session can fault OUTSIDE a test body: its per-test
# setup (HeadlessUnitTestSession.EnsureIsolatedApplication) used to lose a case
# or three per run to a thread race — closed by
# DotCharter.Tests/Perf/HeadlessUiThreadClaim.cs (#192) — and its teardown drain
# throws after 5 s. Such a fault is a harness fault, not a product fault — the
# stack never reaches our code —
# and a gate that failed a release on it would be ignored inside a week. So a
# fault of this shape never blocks: its CLASS is re-run in a fresh process
# (which is what gives the case a chance to run the assertions it never
# reached), a real failure from either run blocks, and a case that races again
# is reported as unverified rather than counted green. The needles that say
# "harness fault" are the ONE shared list in tools/ci/HeadlessShards.psm1
# (Test-HeadlessHarnessFault), the same CI and Invoke-Gate.ps1 -Headless use.

# Reads a trx into Total/Passed/Failed/Skipped plus one object per failure
# carrying the test name, its class, the message, the first stack frame in OUR
# code, and whether it is the harness race above.
function Read-TrxResults([string]$TrxPath) {
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $counters = (Select-Xml -Xml $trx -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node

    $failures = foreach ($node in (Select-Xml -Xml $trx -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns).Node) {
        $message = ($node.Output.ErrorInfo.Message    | Out-String).Trim()
        $stack   = ($node.Output.ErrorInfo.StackTrace | Out-String).Trim()
        $name    = [string]$node.testName
        # "Namespace.Class.Method(args)" -> "Namespace.Class". Theory arguments
        # can contain dots, so the parameter list is cut off first.
        $bare  = ($name -split '\(', 2)[0]
        $class = if ($bare -match '^(.*)\.[^.]+$') { $Matches[1] } else { $bare }
        # The stack starts in Avalonia/xunit plumbing; the frame naming a file
        # in the repo is the one a human needs.
        $frame = ($stack -split "`n" | Where-Object { $_ -match ':line \d+' } | Select-Object -First 1)
        [pscustomobject]@{
            Name      = $name
            Class     = $class
            Message   = $message
            Frame     = if ($frame) { $frame.Trim() } else { '' }
            IsHarness = Test-HeadlessHarnessFault $message $stack
        }
    }

    # The CLASSES that actually reported a result. A case killed inside the
    # Avalonia session before it reports is not a failure and not a skip — it is
    # simply absent, so counters alone cannot see it. See Test-HeadlessCoverage.
    $ranClasses = foreach ($node in (Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns).Node) {
        $bare = ([string]$node.testName -split '\(', 2)[0]
        if ($bare -match '^(.*)\.[^.]+$') { $Matches[1] } else { $bare }
    }

    [pscustomobject]@{
        Total      = [int]$counters.total
        Passed     = [int]$counters.passed
        Failed     = [int]$counters.failed
        Skipped    = [int]$counters.total - [int]$counters.executed
        Failures   = @($failures)
        RanClasses = @($ranClasses | Sort-Object -Unique)
    }
}

# What the filter DISCOVERS, independent of what ran. Cached per filter: the
# listing costs an assembly load and the release runs three passes.
$script:DiscoveryCache = @{}
function Get-DiscoveredTestClasses([string[]]$Filter, [string]$ResultsDir) {
    $key = $Filter -join ' '
    if ($script:DiscoveryCache.ContainsKey($key)) { return $script:DiscoveryCache[$key] }

    $logPath = Join-Path $ResultsDir 'headless-ui-discovery.log'
    # The runner prints a (localized!) banner and then one indented
    # fully-qualified name per line; Read-HeadlessTestListing keys on the SHAPE
    # of the lines rather than the banner, so a German shell lists the same set.
    $counts = Get-HeadlessDiscovery @('test', '--project', $script:HeadlessTestProject, '--no-build') $Filter $logPath
    $classes = @($counts.Keys | Sort-Object)

    $script:DiscoveryCounts[$key] = $counts
    $script:DiscoveryCache[$key] = @($classes)
    return @($classes)
}
$script:DiscoveryCounts = @{}

# A run that executes FEWER cases than the filter discovers is the quietest way
# this suite lies: the session race kills whole classes before they report, and
# an absent class is neither a failure nor a skip. "0 failed" on a 223-case run
# of a 330-case suite has happened and read as green. This is what says so.
function Test-HeadlessCoverage([string[]]$Filter, [string]$ResultsDir, $Run) {
    $discovered = Get-DiscoveredTestClasses -Filter $Filter -ResultsDir $ResultsDir
    if ($discovered.Count -eq 0) {
        Write-Warn2 'test discovery listed no classes — the coverage check is not looking at anything.'
        return @()
    }

    $ran = @($Run.RanClasses)
    return @($discovered | Where-Object { $ran -notcontains $_ })
}

function Write-HeadlessFailure($Failure) {
    $where = if ($Failure.PSObject.Properties['Culture'] -and $Failure.Culture) { " [$($Failure.Culture)]" } else { '' }
    Write-Host "    FAILED$where  $($Failure.Name)" -ForegroundColor Red
    foreach ($line in ($Failure.Message -split "`n" | Select-Object -First 4)) {
        if ($line.Trim()) { Write-Host "            $($line.Trim())" -ForegroundColor Gray }
    }
    if ($Failure.Frame) { Write-Host "            $($Failure.Frame)" -ForegroundColor DarkGray }
}

# One `dotnet test` invocation. Console output goes to a FILE — a run is ~8 min
# of MSBuild/xunit noise and printing it buries the one line that matters. What
# reaches the terminal is the parsed summary and, on failure, the test names.
function Invoke-HeadlessUiRun([string[]]$Filter, [string]$ResultsDir, [string]$Label, [string]$Culture) {
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $trxName = "headless-ui-$Label.trx"
    $trxPath = Join-Path $ResultsDir $trxName
    $logPath = Join-Path $ResultsDir "headless-ui-$Label.log"
    if (Test-Path $trxPath) { Remove-Item $trxPath -Force }

    # The suite reads this when it seeds the throwaway appconfig.json. Saved and
    # restored around the run so a later gate in the same shell is not left
    # sweeping Japanese.
    $previousCulture = $env:DOTCHARTER_HEADLESS_CULTURE
    if ($Culture) { $env:DOTCHARTER_HEADLESS_CULTURE = $Culture }

    try {
        & dotnet test --project $script:HeadlessTestProject --no-build @Filter `
            --report-xunit-trx --report-xunit-trx-filename $trxName `
            --results-directory $ResultsDir *> $logPath
    }
    finally {
        $env:DOTCHARTER_HEADLESS_CULTURE = $previousCulture
    }

    if (-not (Test-Path $trxPath)) {
        throw "The headless UI suite produced no results file — the run never started. See $logPath."
    }
    $results = Read-TrxResults $trxPath
    Add-Member -InputObject $results -NotePropertyName LogPath -NotePropertyValue $logPath
    Add-Member -InputObject $results -NotePropertyName TrxPath -NotePropertyValue $trxPath
    return $results
}

# The same pass as Invoke-HeadlessUiRun, split across $Shards concurrent
# testhosts (tools/ci/HeadlessShards.psm1). Discovery names the classes, each
# shard runs whole classes by exact name, and the shards' TRX files are merged
# into ONE headless-ui-$Label.trx that Read-TrxResults grades like a single
# run. A shard that crashed writes no TRX: its classes are then ABSENT from the
# merge, and Test-HeadlessCoverage (same discovery, same filter) re-runs them
# and blocks if they still report nothing — the guarantee is unchanged.
function Invoke-HeadlessUiShardedRun([string[]]$Filter, [string]$ResultsDir, [string]$Label, [string]$Culture, [int]$Shards) {
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $null = Get-DiscoveredTestClasses -Filter $Filter -ResultsDir $ResultsDir
    $counts = $script:DiscoveryCounts[($Filter -join ' ')]
    if (-not $counts -or $counts.Count -eq 0) {
        throw "Test discovery listed no headless classes for '$($Filter -join ' ')' — nothing to run. See $(Join-Path $ResultsDir 'headless-ui-discovery.log')."
    }
    $plan = @(New-HeadlessShardPlan $counts (Read-HeadlessShardWeights (Join-Path $RepoRoot 'tools/ci/headless-shard-weights.json')) $Shards)
    $runs = @(Invoke-HeadlessShards -Plan $plan -DotnetTestArgs @('test', '--project', $script:HeadlessTestProject, '--no-build') `
        -ResultsDir $ResultsDir -Label "headless-ui-$Label" -Culture $Culture -WorkingDirectory $RepoRoot)
    foreach ($r in $runs) {
        Write-Host ('    [{0}] shard {1}: {2} classes, {3:mm\:ss}, peak {4:0.0} GB, exit {5}{6}' -f `
            $Culture, $r.Index, $r.Classes.Count, $r.Elapsed, ($r.PeakBytes / 1GB), $r.ExitCode,
            $(if ($r.TrxPath) { '' } else { ' - NO RESULTS FILE (its classes count as absent)' })) -ForegroundColor Gray
    }
    $trxPath = Join-Path $ResultsDir "headless-ui-$Label.trx"
    if (Test-Path $trxPath) { Remove-Item $trxPath -Force }
    if (-not (Merge-HeadlessTrx @($runs | ForEach-Object { $_.TrxPath }) $trxPath)) {
        throw "The headless UI suite produced no results file in ANY shard — the run never started. See $($runs[0].LogPath)."
    }
    $results = Read-TrxResults $trxPath
    Add-Member -InputObject $results -NotePropertyName LogPath -NotePropertyValue (($runs | ForEach-Object { $_.LogPath }) -join ', ')
    Add-Member -InputObject $results -NotePropertyName TrxPath -NotePropertyValue $trxPath
    return $results
}

# One pass: run the filter, retry anything the session race killed, and hand
# back only the failures that are about the PRODUCT. Extracted from
# Invoke-HeadlessUiGate when the culture sweep arrived — the retry rules are the
# same whichever language the pass ran in, and two copies of them would drift.
function Invoke-HeadlessUiPass([string[]]$Filter, [string]$ResultsDir, [string]$Label, [string]$Culture, [int]$Shards = 1) {
    $started = Get-Date
    $run = if ($Shards -gt 1) {
        Invoke-HeadlessUiShardedRun -Filter $Filter -ResultsDir $ResultsDir -Label $Label -Culture $Culture -Shards $Shards
    } else {
        Invoke-HeadlessUiRun -Filter $Filter -ResultsDir $ResultsDir -Label $Label -Culture $Culture
    }
    $elapsed = (Get-Date) - $started
    Write-Host ('    [{0}] {1} cases: {2} passed, {3} failed, {4} skipped in {5:mm\:ss}' -f `
        $Culture, $run.Total, $run.Passed, $run.Failed, $run.Skipped, $elapsed) -ForegroundColor Gray

    $product = @($run.Failures | Where-Object { -not $_.IsHarness })
    $harness = @($run.Failures | Where-Object { $_.IsHarness })

    # Classes the filter discovered that never reported ANY result. They are
    # retried exactly like a session-race failure, because that is what they
    # usually are — the difference is only that they died before xunit could
    # attribute the death to a case.
    $absent = @(Test-HeadlessCoverage -Filter $Filter -ResultsDir $ResultsDir -Run $run)
    if ($absent.Count -gt 0) {
        Write-Warn2 ("[$Culture] $($absent.Count) DISCOVERED class(es) reported no result at all — the run " +
                     "silently executed less than the filter selects: " + ($absent -join ', '))
    }

    if ($harness.Count -gt 0 -or $absent.Count -gt 0) {
        # A case killed by the session race never reached its own assertions, so
        # the first run says NOTHING about it. The retry is what gives it a
        # chance to fail for real. A single case cannot be re-run alone — the
        # session is built per run, so a lone Scenario_0* hits the race every
        # time — so the retry is per CLASS, the granularity that reproduces
        # cleanly.
        $classes = @(@(@($harness | Select-Object -ExpandProperty Class) + $absent) | Sort-Object -Unique)
        Write-Warn2 ("[$Culture] $($harness.Count) case(s) died inside the Avalonia headless session itself " +
                     "(a harness fault: the setup race or the teardown drain) without running their assertions, and " +
                     "$($absent.Count) class(es) never reported. " +
                     "Re-running $($classes.Count) class(es) in a fresh process.")
        $retryFilter = [string[]]@($classes | ForEach-Object { @('--filter-class', $_) })
        $retry = Invoke-HeadlessUiRun -Filter $retryFilter -ResultsDir $ResultsDir `
            -Label "$Label-retry" -Culture $Culture
        Write-Host ('    [{0}] retry: {1} cases, {2} passed, {3} failed' -f `
            $Culture, $retry.Total, $retry.Passed, $retry.Failed) -ForegroundColor Gray

        # Only REAL failures block, in either run. A session-race exception is
        # never a product signal, so a release is not held hostage to it — but
        # a case still unverified after the retry is said out loud, because
        # "green" would be a lie about it.
        $product += @($retry.Failures | Where-Object { -not $_.IsHarness })
        $stillRacing = @($retry.Failures | Where-Object { $_.IsHarness })
        if ($stillRacing.Count -eq 0) {
            Write-Ok "[$Culture] the $($harness.Count) session-race case(s) ran clean on re-run"
        }
        else {
            Write-Warn2 ("[$Culture] $($stillRacing.Count) case(s) hit the session race AGAIN and are " +
                         "UNVERIFIED by this release: " +
                         "$(($stillRacing | Select-Object -ExpandProperty Name) -join ', ')")
        }

        # A class that STILL does not report has not been graded by anything —
        # unlike a race failure, there is no assertion result to tolerate. That
        # blocks: shipping on a suite that quietly ran two thirds of itself is
        # the exact trap this check exists for.
        $stillAbsent = @($absent | Where-Object { @($retry.RanClasses) -notcontains $_ })
        if ($stillAbsent.Count -gt 0) {
            $product += @($stillAbsent | ForEach-Object {
                [pscustomobject]@{
                    Name      = $_
                    Class     = $_
                    Message   = 'DISCOVERED but never executed, in the full run AND in its own retry process. ' +
                                'Nothing in this class was graded; treating it as green would be a lie.'
                    Frame     = ''
                    IsHarness = $false
                }
            })
        }
    }

    # A REAL failure gets the repo's own documented protocol too
    # (DotCharter.Tests/AGENTS.md: "only an isolated re-run of the NAMED case
    # decides it"): its CLASS is re-run once in a fresh process. A case that
    # fails there too blocks. A case that passes there is ORDER-DEPENDENT —
    # it is dropped from the blocking set but printed loudly, because "green"
    # would be a lie about it and somebody has to find the class that leaves
    # the state behind. 2026-08-29: v0.48.5's preflight failed twice on
    # EscapeKeyRoutingUiPerfSmokeTests.Escape_AfterFocusedElementRemoved_
    # StillNavigates — green 6/6 in isolation every time, pairwise with its
    # neighbours too, red only as the first key press of a full run.
    $orderCandidates = @($product | Where-Object { $_.Frame -ne '' -or $_.Class -ne $_.Name })
    if ($orderCandidates.Count -gt 0) {
        $classes = @($orderCandidates | Select-Object -ExpandProperty Class | Sort-Object -Unique)
        Write-Warn2 ("[$Culture] $($orderCandidates.Count) case(s) FAILED their assertions in the full run. " +
                     "Re-running $($classes.Count) class(es) in a fresh process to tell an order-dependent " +
                     "failure from a real one.")
        $isolated = Invoke-HeadlessUiRun -Filter ([string[]]@($classes | ForEach-Object { @('--filter-class', $_) })) `
            -ResultsDir $ResultsDir -Label "$Label-isolated" -Culture $Culture
        Write-Host ('    [{0}] isolated: {1} cases, {2} passed, {3} failed' -f `
            $Culture, $isolated.Total, $isolated.Passed, $isolated.Failed) -ForegroundColor Gray
        $isolatedFailed = @($isolated.Failures | Select-Object -ExpandProperty Name)
        $orderDependent = @($orderCandidates | Where-Object {
            (@($isolated.RanClasses) -contains $_.Class) -and ($isolatedFailed -notcontains $_.Name) })
        if ($orderDependent.Count -gt 0) {
            Write-Warn2 ("[$Culture] $($orderDependent.Count) case(s) passed in a fresh process and are " +
                         "ORDER-DEPENDENT in the full run — NOT blocking this release, but a class earlier " +
                         "in the run leaves state behind that breaks them. Find it: " +
                         "$(($orderDependent | Select-Object -ExpandProperty Name) -join ', ')")
            $names = @($orderDependent | Select-Object -ExpandProperty Name)
            $product = @($product | Where-Object { $names -notcontains $_.Name })
        }
    }

    # The culture is the difference between "a slot is too small" and "a slot is
    # too small IN GERMAN", and the trx does not record it.
    foreach ($failure in $product) {
        Add-Member -InputObject $failure -NotePropertyName Culture -NotePropertyValue $Culture -Force
    }

    return [pscustomobject]@{
        Run      = $run
        Product  = @($product)
    }
}

# DotCharter.Tests, built once per run: the headless gate and the release lane both run its output.
function Build-ReleaseTestProject([string]$ResultsDir) {
    if ($script:TestProjectBuilt) { return }
    $script:HeadlessTestProject = Join-Path $RepoRoot 'DotCharter.Tests/DotCharter.Tests.csproj'
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    Write-Host '    building DotCharter.Tests…' -ForegroundColor DarkGray
    $buildLog = Join-Path $ResultsDir 'headless-ui-build.log'
    & dotnet build $script:HeadlessTestProject --nologo -v:m *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content $buildLog | Select-String -Pattern 'error|Fehler' | Select-Object -First 20 |
            ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "DotCharter.Tests failed to build — full log: $buildLog"
    }
    $script:TestProjectBuilt = $true
}

# The release lane (tools/ci/release-lane.json): the slow corpus / integration / determinism
# classes the default gate leaves out. NEVER skipped - only reused when a green run of it is on
# record for this exact tree (the merge train records one; -ForceReleaseLane runs it anyway).
function Invoke-ReleaseLaneGate([string]$ResultsDir) {
    Write-Step "Gate: release lane (the slow classes tools/ci/release-lane.json lists, $HeadlessShards concurrent testhosts)"
    Build-ReleaseTestProject -ResultsDir $ResultsDir
    $tree = Get-CleanTreeId -Repo $RepoRoot
    $recorded = $null
    if (-not $tree) { Write-Host '    the tree is not clean HEAD, so no recorded release-lane run applies' -ForegroundColor DarkGray }
    elseif ($ForceReleaseLane) { Write-Host '    -ForceReleaseLane: running the lane whatever is on record' -ForegroundColor DarkGray }
    else { $recorded = Find-GateRecord -Trees (Get-GateRecordTrees -RepoRoot $RepoRoot -Tree $tree) -Kind releaseLane }
    if ($recorded) {
        Write-Ok "release lane NOT re-run: reusing the green run recorded for $(Format-GateRecord $recorded) (-ForceReleaseLane re-runs it)"
        return
    }
    $laneArgs = @('-NoProfile', '-File', (Join-Path $RepoRoot 'tools/agent/Invoke-Gate.ps1'), '-ReleaseLane', '-NoBuild', '-Shards', $HeadlessShards)
    & pwsh @laneArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The release lane is red - not cutting a release. Re-run it alone: pwsh -NoProfile -File tools/agent/Invoke-Gate.ps1 -ReleaseLane'
    }
    Write-Ok 'release lane green'
    if ($tree) {
        $saved = Save-GateRecord -Tree $tree -Commit (git -C $RepoRoot rev-parse HEAD) -ReleaseLane -Source 'New-Release.ps1 release lane'
        Write-Ok "recorded: a green release lane on tree $tree ($saved)"
    }
    $global:LASTEXITCODE = 0
}

function Invoke-HeadlessUiGate([string]$ResultsDir) {
    Write-Step "Gate: headless UI suite ($HeadlessShards concurrent testhosts, plus the localized layout sweeps)"
    $script:HeadlessTestProject = Join-Path $RepoRoot 'DotCharter.Tests/DotCharter.Tests.csproj'
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

    Build-ReleaseTestProject -ResultsDir $ResultsDir

    # One test run per tree: a green main pass on record for this exact, clean tree is
    # reused (named below); -ForceHeadlessUi runs it again. The sweeps below always run.
    $tree = Get-CleanTreeId -Repo $RepoRoot
    $recorded = $null
    if (-not $tree) { Write-Host '    the tree is not clean HEAD, so no recorded headless run applies' -ForegroundColor DarkGray }
    elseif ($ForceHeadlessUi) { Write-Host '    -ForceHeadlessUi: running the main pass whatever is on record' -ForegroundColor DarkGray }
    else { $recorded = Find-GateRecord -Trees (Get-GateRecordTrees -RepoRoot $RepoRoot -Tree $tree) -Kind headless }

    $product = @()
    if ($recorded) {
        Write-Ok "headless UI main pass NOT re-run: reusing the green run recorded for $(Format-GateRecord $recorded) (-ForceHeadlessUi re-runs it)"
        $run = [pscustomobject]@{ Passed = 'recorded'; Skipped = 'recorded'; LogPath = (Get-GateRecordPath -Tree $recorded.Tree) }
    }
    else {
        $pass = Invoke-HeadlessUiPass -Filter $HeadlessUiFilter -ResultsDir $ResultsDir `
            -Label 'run1' -Culture 'en-US' -Shards $HeadlessShards
        $run = $pass.Run
        $product = @($pass.Product)
    }

    # The localized layout sweeps. Only the classes that compare a size, because
    # a full run per culture would cost more than a release should spend before
    # it publishes anything — see the note beside $HeadlessLayoutFilter.
    foreach ($culture in $HeadlessSweepCultures) {
        # Sharded too: the layout classes are dealt across -HeadlessShards processes.
        $localized = Invoke-HeadlessUiPass -Filter $HeadlessLayoutFilter -ResultsDir $ResultsDir `
            -Label "layout-$culture" -Culture $culture -Shards $HeadlessShards
        $product += @($localized.Product)
    }

    if ($product.Count -gt 0) {
        Write-Host ''
        Write-Host "  $($product.Count) headless UI test(s) FAILED:" -ForegroundColor Red
        foreach ($failure in $product) { Write-HeadlessFailure $failure }
        Write-Host ''
        Write-Host "  Full output: $($run.LogPath)" -ForegroundColor DarkGray
        Write-Host '  Re-run just this gate:' -ForegroundColor DarkGray
        Write-Host "    dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build $($HeadlessUiFilter -join ' ')" -ForegroundColor DarkGray
        Write-Host '  …or one localized layout sweep:' -ForegroundColor DarkGray
        Write-Host "    `$env:DOTCHARTER_HEADLESS_CULTURE='de-DE'; dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build $($HeadlessLayoutFilter -join ' ')" -ForegroundColor DarkGray
        throw "The headless UI suite is red ($($product.Count) failing) — not cutting a release. " +
              'Fix it, or pass -SkipHeadlessUi only if you know exactly why that is safe.'
    }

    Write-Ok ("headless UI suite green ($($run.Passed) passed, $($run.Skipped) skipped), " +
              "and its layout classes green in $($HeadlessSweepCultures -join ' + ')")
    if ($tree -and -not $recorded) {
        $saved = Save-GateRecord -Tree $tree -Commit (git -C $RepoRoot rev-parse HEAD) -Headless -Source 'New-Release.ps1 headless gate'
        Write-Ok "recorded: a green headless run on tree $tree ($saved)"
    }
    # A tolerated session-race failure leaves $LASTEXITCODE at 1. Everything
    # after this point sets it before reading it, but a stale non-zero is a
    # footgun waiting for the next `if ($LASTEXITCODE -ne 0)` someone adds.
    $global:LASTEXITCODE = 0
}

if ($SelfTest) { Invoke-SelfTest; exit 0 }

if ($SmokeOnly) {
    $source = (Resolve-Path -LiteralPath $SmokeOnly).Path
    # A pack being re-verified is a pack about to be uploaded: the plugin check first.
    Assert-ReleasePayloadPlugins $source $PluginAssemblies -WithPlugins:$WithPlugins
    if ($PluginToolFiles.Count -gt 0) { Assert-ReleasePayloadPlugins $source $PluginToolFiles -WithPlugins:$WithPlugins }
    Write-Ok "$PluginModeText in $source"
    Invoke-PackSmoke -Source $source -ScratchRoot (Join-Path ([System.IO.Path]::GetTempPath()) "dotcharter-smoke-$([guid]::NewGuid().ToString('N'))")
    Write-Host "Smoke PASSED: $source" -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Release build
# ---------------------------------------------------------------------------
if (-not $Version) { $Version = Get-AppVersion }
$ReleaseIdentity = Get-ReleaseIdentity $Version
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "artifacts/release/v$Version" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# -WithPlugins needs a plugin checked out; and where the printed command points is
# settled before anything is built, so a missing -Repo fails in seconds.
if ($WithPlugins -and $PluginAssemblies.Count -eq 0) { throw '-WithPlugins needs at least one plugin checked out under plugins/<name>/.' }
$ReleaseFeed = Resolve-ReleaseFeed -Repo $Repo -WithPlugins:$WithPlugins -TagCheckout $TagCheckout -RepoRoot $RepoRoot

if ($PayloadOnly) {
    Write-Step "Payload stage only (-PayloadOnly): DotCharter v$Version -> $OutputDir$(if ($WithPlugins) { ' (WITH the plugins)' })"
    Clear-ReleaseBuildOutputs @((Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj'))
    $appDir = Join-Path $OutputDir "staging/$($ReleaseIdentity.RootName)/app"
    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
    Publish-UiPayload -AppDir $appDir -NoLayoutCheck
    Write-Host "Payload stage PASSED: $appDir (nothing zipped, hashed or published)" -ForegroundColor Green
    exit 0
}

Write-Step "Releasing DotCharter v$Version -> $OutputDir ($(if ($WithPlugins) { "build WITH the plugins ($PluginList)" } else { 'public build' }), for $($ReleaseFeed.Repo))"

# --- Preflight -------------------------------------------------------------
Write-Step 'Preflight checks'
$nativeDll = Join-Path $RepoRoot 'JuceSharp/src/JuceSharp/runtimes/win-x64/native/juce_sharp.dll'
if (-not (Test-Path $nativeDll)) {
    throw "juce_sharp.dll is not built ($nativeDll). Build it first:`n" +
          '  cmake -S JuceSharp/src/JuceSharp.Native -B JuceSharp/src/JuceSharp.Native/build -G "Visual Studio 17 2022" -A x64' + "`n" +
          '  cmake --build JuceSharp/src/JuceSharp.Native/build --config Release'
}
# The release-signing key is checked FIRST: an unsigned release is one
# every install from v1.1.0 on refuses, so a missing key must stop the release
# before an hour of building, not after it. `pubkey` prints only the PUBLIC half.
Write-Step 'Release-signing key present and matching the pinned public key'
if (-not (Test-Path -LiteralPath $SigningKeyPath)) {
    throw "The release-signing key is missing ($SigningKeyPath). Every release must be signed: restore the key " +
          'from its backup, or pass -SigningKeyPath / set DOTCHARTER_RELEASE_SIGNING_KEY. See tools/release/README.md § Release signing.'
}
$signingPublicKey = (& dotnet run $SignTool pubkey $SigningKeyPath | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0) { throw "The release-signing key at $SigningKeyPath could not be read (see above)." }
$pinnedLine = Select-String -Path (Join-Path $RepoRoot 'DotCharter.Services/Update/ReleaseSignature.cs') -Pattern 'PinnedPublicKeyBase64 = "([^"]+)"'
if (-not $pinnedLine -or $pinnedLine.Matches[0].Groups[1].Value -ne "$signingPublicKey".Trim()) {
    throw "The release-signing key at $SigningKeyPath is not the one pinned in ReleaseSignature.PinnedPublicKeyBase64: every install would refuse this release."
}
Initialize-VoicePacks
Assert-NoSeparatelyLicensedVoice (Join-Path $RepoRoot 'artifacts/voice-pack')
$dirty = git -C $RepoRoot status --porcelain
if ($dirty) { Write-Warn2 'Working tree is dirty — the release should be cut from a clean, committed tree.' }
if ($ReleaseFeed.IsOtherCheckout -and (git -C $ReleaseFeed.TargetCheckout status --porcelain)) {
    Write-Warn2 "The tagged checkout ($($ReleaseFeed.TargetCheckout)) is dirty — commit it first; its HEAD is what the release names."
}
$existingTag = git -C $RepoRoot tag --list "v$Version"
if ($existingTag) { Write-Warn2 "Tag v$Version already exists locally." }

# The public -> dev sync check (#83). Actions is off on the dev repos, so the scheduled
# public-sync-watch never runs: the release preflight is where a public commit that dev
# has not imported (a contributor's merged PR) is caught, before the next public snapshot
# would overwrite it. The export (Export-PublicSnapshot.ps1) stays the hard stop too.
if ($SkipPublicSyncCheck) {
    Write-Warn2 'SKIPPING the public sync check (-SkipPublicSyncCheck): run tools/release/Find-UnimportedPublicCommits.ps1 before the next export.'
}
else {
    Write-Step 'Every public commit since the last snapshot is imported into dev (Find-UnimportedPublicCommits.ps1)'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Find-UnimportedPublicCommits.ps1') -DevRepo $RepoRoot -DevRef HEAD -FailOnMissing
    if ($LASTEXITCODE -ne 0) { throw 'Public commits are not imported into dev (listed above): import each with tools/release/Import-PublicCommit.ps1, or pass -SkipPublicSyncCheck.' }
}

# Dependency licences (#828): the committed inventory must match the packages
# the projects resolve (offline), and DependencyLicenceTests (in the gate)
# holds every licence to the GPL-3.0-compatible allowlist.
# -Restore first: the check reads every obj/project.assets.json, and this
# runs BEFORE the publishes, so a fresh clone has none and an old tree has a
# stale set (the v0.70.0 rehearsal stopped here with 5 packages missing).
Write-Step 'Dependency licence inventory is current'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Update-DependencyInventory.ps1') -Check -Restore
if ($LASTEXITCODE -ne 0) { throw 'licenses/dependencies.json is out of date: run tools/release/Update-DependencyInventory.ps1 and commit it.' }

# tonelab's prebuilt files are checked HERE too: a wrong hash costs seconds, not a pack.
if (-not $SkipUi -and -not $SkipTonelab) {
    Write-Step 'tonelab prebuilt files match tools/release/tonelab/tonelab.json'
    $tonelabCommit = Test-TonelabPin
    Write-Ok "tonelab $tonelabCommit found in $TonelabDir"
}
elseif (-not $SkipUi) {
    Write-Warn2 'SKIPPING tonelab (-SkipTonelab): the install carries no tools/tonelab/, so the app offers no "Start Rocksmith".'
}

# The app-local VC++ runtime for ONNX Runtime, verified against its pin HERE for the same
# reason: a missing or changed redistributable should cost seconds, not a pack.
$vcRedistDir = Join-Path $OutputDir 'vcredist'
if (-not $SkipUi) {
    Write-Step "App-local VC++ runtime $($VcRedistPin.Version) for ONNX Runtime"
    $vcSource = Install-VcRedist -Pin $VcRedistPin -Destination $vcRedistDir
    Write-Ok "$(@($VcRedistPin.Files.Keys) -join ', ') from $vcSource, each matching its pinned SHA-256"
}

# The media tools are acquired HERE, not after the publishes: a pin that 404s
# (BtbN deleted n7.1 under the v0.59.0 pack) should cost seconds, not a pack.
$redist = $null
if (-not $SkipUi -and -not $SkipTools) {
    Write-Step 'Media tools (ffmpeg, yt-dlp) at their pinned versions'
    $redist = Ensure-RedistTools
    if ($redist.Errors.Count -gt 0) {
        $why = "Media tools could not be acquired:`n  " + ($redist.Errors -join "`n  ")
        if (-not $AllowMissingTools) {
            throw "$why`nA release without ffmpeg breaks stem Opus encoding and YouTube import for users. " +
                  'Fix the pin at the top of this script, or pass -AllowMissingTools only if you mean to ship without them.'
        }
        Write-Warn2 "$why`n    Continuing because -AllowMissingTools is set."
    }
}
Write-Ok 'preflight done'

# --- Blocking gate: the headless UI suite ----------------------------------
# Runs BEFORE the ~300 MB publishes: a red suite should cost a few minutes, not
# a full pack. See Invoke-HeadlessUiGate above for why this is the only place
# the suite has ever had.
if ($SkipHeadlessUi) {
    Write-Warn2 'SKIPPING the headless UI suite (-SkipHeadlessUi). The five silent XAML faults are UNCHECKED for this pack.'
}
else {
    Invoke-HeadlessUiGate -ResultsDir (Join-Path $OutputDir 'tests')
}

# --- Blocking gate: the release lane -----------------------------------------
# The slow classes the default gate leaves out. Not skippable (see Invoke-ReleaseLaneGate).
Invoke-ReleaseLaneGate -ResultsDir (Join-Path $OutputDir 'tests')

$assets = @()

# --- Clean Release build -----------------------------------------------------
# Every byte a release ships is compiled by THIS run. An incremental build trusts
# timestamps, and an interrupted earlier run leaves outputs whose timestamps are
# current and whose bytes are zeros (#137). The cost is one full Release compile.
if (-not $SkipUi -or -not $SkipRigLab) {
    Write-Step 'Deleting bin/Release + obj/Release of every published project (clean build)'
    $publishRoots = @()
    if (-not $SkipUi) {
        $publishRoots += Join-Path $RepoRoot 'DotCharter.UI/DotCharter.UI.csproj'
        $publishRoots += Join-Path $RepoRoot 'DotCharter.Launcher/DotCharter.Launcher.csproj'
    }
    if (-not $SkipRigLab) { $publishRoots += Join-Path $RepoRoot 'DotCharter.RigLab/DotCharter.RigLab.csproj' }
    Clear-ReleaseBuildOutputs $publishRoots
}

# --- Asset 1: UI zip (self-contained win-x64, folder-wrapped) ---------------
$uiZip = Join-Path $OutputDir $ReleaseIdentity.ZipName
if (-not $SkipUi) {
    Write-Step 'Publishing DotCharter.UI (self-contained win-x64) into app/'
    $staging = Join-Path $OutputDir 'staging'
    # $publishDir is the install's TOP; the app is published into its app/ (#413).
    $publishDir = Join-Path $staging $ReleaseIdentity.RootName
    $appDir = Join-Path $publishDir 'app'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Publish-UiPayload -AppDir $appDir

    Write-Step 'Pruning dead weight from the payload'
    Remove-DeadPayloadWeight -PublishDir $appDir

    Write-Step 'Laying out the install: launcher, voices/, tools/ and the readme at the top, the app in app/ (#413)'
    $launcherBytes = Publish-Launcher -Destination $publishDir -AppVersion $Version
    New-SplitInstallLayout -InstallDir $publishDir -AppDir $appDir
    Write-Ok ("launcher DotCharter.exe (NativeAOT, {0:N0} KB)" -f ($launcherBytes / 1KB))

    # Bundle ffmpeg/yt-dlp into <publish>/tools before zipping so stem
    # Opus encoding + YouTube import work out of the box.
    if (-not $SkipTools) {
        Write-Step 'Bundling media tools into tools/ (ffmpeg, yt-dlp)'
        $bundled = Add-BundledTools -PublishDir $publishDir -Redist $redist
        $b = { param($x) if ($x) { 'yes' } else { 'NO' } }
        $script:ToolsSummary = ("ffmpeg {0} ({1}), yt-dlp {2} ({3})" -f `
            (& $b $bundled.Ffmpeg), $redist.FfmpegVersion, `
            (& $b $bundled.YtDlp), $redist.YtDlpVersion)
        if (-not $ContestedFeatures) {
            $script:ToolsSummary = ("ffmpeg {0} ({1}), yt-dlp left out (-ContestedFeatures:`$false)" -f `
                (& $b $bundled.Ffmpeg), $redist.FfmpegVersion)
        }
        if ($bundled.Ffmpeg -and ($bundled.YtDlp -or -not $ContestedFeatures)) {
            Write-Ok "bundled: $($script:ToolsSummary)"
        }
        else {
            Write-Warn2 "media tools NOT fully bundled: $($script:ToolsSummary)"
        }
    }
    else {
        Write-Warn2 'Skipping media-tool bundling (-SkipTools) — release will not ship ffmpeg/yt-dlp.'
        $script:ToolsSummary = 'skipped (-SkipTools)'
    }

    if ($WithPlugins -and $PluginToolFiles.Count -gt 0) {
        Write-Step 'Writing the plugin tools'' launchers into tools/'
        Add-PluginToolLaunchers -RepoRoot $RepoRoot -InstallDir $publishDir
        Write-Ok "launchers: $((@(Get-PluginReleaseTools $RepoRoot) | ForEach-Object { $_.Launcher }) -join ', ')"
    }

    if (-not $SkipTonelab) {
        Write-Step 'Copying the pinned tonelab build into tools/tonelab/'
        $tonelabShipped = Add-Tonelab -PublishDir $publishDir
        Write-Ok "tonelab: $($TonelabFiles -join ', ') in $tonelabShipped"
    }
    else {
        Write-Warn2 'tonelab NOT bundled (-SkipTonelab): no tools/tonelab/ in this install.'
    }

    # onnxruntime.dll imports the dynamic C++ runtime: ship the pinned x64 DLLs beside it.
    # The binary check below fails the release if one is missing.
    foreach ($name in $VcRedistPin.Files.Keys) { Copy-Item -LiteralPath (Join-Path $vcRedistDir $name) -Destination (Join-Path $appDir $name) -Force }
    Write-Ok "VC++ runtime $($VcRedistPin.Version) copied beside onnxruntime.dll: $(@($VcRedistPin.Files.Keys) -join ', ')"

    # Every binary in the payload, the bundled tools included, BEFORE anything is
    # hashed or zipped: a corrupt file never reaches a manifest or an asset.
    Write-Step 'Checking every payload binary is a binary (MZ, PE headers, metadata, no zero-filled files)'
    Test-PayloadBinaries -PayloadDir $publishDir -Label 'UI'

    # --- payload manifest (ships INSIDE the payload AND as its own asset) ----
    # LOAD-BEARING, not informational. It records a SHA-256 per payload file, and
    # since 0.45 that is the ENTIRE small-update mechanism: the updater fetches
    # this asset, diffs it against what the install has, and range-fetches only
    # the differing members out of the ONE full zip
    # (Services/Update/PartialUpdateDownloader). No patch assets are published any
    # more - they only ever helped installs inside a fixed covering window, while
    # a range fetch works from ANY prior version and adds nothing to the release
    # page.
    #
    # It must be written BEFORE the zip so the installed payload carries its own
    # copy - that is what lets an install describe itself to the NEXT update
    # without re-hashing 414 MB.
    Write-Step 'Hashing the payload (payload-manifest.json)'
    # A split payload keeps its copy in app/ (PayloadManifest.SplitInstalledPath).
    Test-InstallLayout -InstallDir $publishDir
    $manifestPath = Join-Path $appDir 'payload-manifest.json'
    & dotnet run $ManifestTool manifest $publishDir $Version $manifestPath
    if ($LASTEXITCODE -ne 0) { throw 'payload manifest build failed.' }
    $manifestAsset = Join-Path $OutputDir 'payload-manifest.json'
    Copy-Item $manifestPath $manifestAsset -Force

    Write-Step 'Zipping UI payload (folder-wrapped, forward-slash entries)'
    Assert-ReleasePayloadPlugins $publishDir $PluginAssemblies -WithPlugins:$WithPlugins
    if ($PluginToolFiles.Count -gt 0) { Assert-ReleasePayloadPlugins $publishDir $PluginToolFiles -WithPlugins:$WithPlugins }
    New-FolderWrappedZip -Directory $publishDir -ZipPath $uiZip
    Test-ReleaseZip -ZipPath $uiZip -ExpectedRoot "$($ReleaseIdentity.RootName)/"
    Assert-ReleasePayloadPlugins $uiZip $PluginAssemblies -WithPlugins:$WithPlugins
    if ($PluginToolFiles.Count -gt 0) { Assert-ReleasePayloadPlugins $uiZip $PluginToolFiles -WithPlugins:$WithPlugins }
    Write-Ok ("{0} ({1:N0} MB)" -f (Split-Path $uiZip -Leaf), ((Get-Item $uiZip).Length / 1MB))

    # Run what SHIPS: the zip, extracted, launched. The layout validator above
    # cannot see a payload that does not start (#137); this can.
    if ($SkipLaunchSmoke) {
        Write-Warn2 'SKIPPING the launch smoke (-SkipLaunchSmoke). Nobody has started this zip''s DotCharter.exe - do it by hand before publishing.'
        $script:SmokeSummary = 'SKIPPED (-SkipLaunchSmoke)'
    }
    else {
        try {
            Invoke-PackSmoke -Source $uiZip -ScratchRoot (Join-Path $OutputDir 'smoke')
        }
        catch {
            # A zip that does not start must not sit in the output folder looking
            # like an asset: the printed `gh release create` would upload it.
            Remove-Item -LiteralPath $uiZip -Force -ErrorAction SilentlyContinue
            throw
        }
        $script:SmokeSummary = 'passed'
    }
}
$assets += $uiZip

# --- Asset 2: the standalone rig (DotCharter.RigLab) -------------------------
# Its own app and its own <Version>. Publish-RigLab.ps1 builds it (and refuses a
# publish that drags the charting stack in); zipped here the same folder-wrapped,
# forward-slash way. NEVER name it "*win-x64*" - see the header.
if (-not $SkipRigLab) {
    Write-Step 'Publishing the standalone rig (DotCharter RigLab)'
    [xml]$rigProj = Get-Content (Join-Path $RepoRoot 'DotCharter.RigLab/DotCharter.RigLab.csproj')
    $rigVersion = ($rigProj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $rigVersion) { throw 'No <Version> in DotCharter.RigLab.csproj' }
    $rigStaging = Join-Path $OutputDir 'staging-riglab'
    if (Test-Path $rigStaging) { Remove-Item $rigStaging -Recurse -Force }
    $rigArgs = @{ OutputRoot = $rigStaging; NoZip = $true }
    if ($Tone3000ClientId) { $rigArgs.Tone3000ClientId = $Tone3000ClientId }
    else { Write-Warn2 'No -Tone3000ClientId: the rig ships without a baked TONE3000 key (users paste their own).' }
    & (Join-Path $PSScriptRoot 'Publish-RigLab.ps1') @rigArgs
    if ($LASTEXITCODE) { throw 'Publish-RigLab.ps1 failed.' }
    $rigDir = Get-ChildItem $rigStaging -Directory | Select-Object -First 1
    $rigZip = Join-Path $OutputDir "DotCharter-RigLab-v$rigVersion.zip"
    if ((Split-Path $rigZip -Leaf) -match 'x64') { throw "The rig zip must not be named *x64*: $rigZip" }
    Test-PayloadBinaries -PayloadDir $rigDir.FullName -Label 'RigLab'
    New-FolderWrappedZip -Directory $rigDir.FullName -ZipPath $rigZip
    Write-Ok ("{0} ({1:N0} MB)" -f (Split-Path $rigZip -Leaf), ((Get-Item $rigZip).Length / 1MB))
    $assets += $rigZip
}

if (-not $SkipUi) { $assets += $manifestAsset }

# --- Changelog stub ----------------------------------------------------------
Write-Step 'Generating changelog stub from git log'
$notesPath = Join-Path $OutputDir "notes-v$Version.md"
# Only app release tags (v*): model files ship as their own pre-release tags
# (`models-bs-roformer-sw-v1`, #267), and the stub must start at the last APP release.
$lastTag = git -C $RepoRoot describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
$range = if ($lastTag) { "$lastTag..HEAD" } else { 'HEAD' }
$subjects = @(git -C $RepoRoot log $range --no-merges --pretty='%s')

$sections = [ordered]@{ Features = @(); Fixes = @(); Performance = @(); Other = @() }
foreach ($s in $subjects) {
    switch -Regex ($s) {
        '^feat(\(.*?\))?!?:'         { $sections.Features    += "- $s"; break }
        '^fix(\(.*?\))?!?:'          { $sections.Fixes       += "- $s"; break }
        '^perf(\(.*?\))?!?:'         { $sections.Performance += "- $s"; break }
        default                      { $sections.Other       += "- $s" }
    }
}
$notes = [System.Collections.Generic.List[string]]::new()
$notes.Add("# DotCharter v$Version")
$notes.Add('')
$notes.Add("<!-- Changelog stub generated from git log $range - edit before publishing. -->")
if ($ReleaseFeed.IsOtherCheckout) {
    $taggedSha = git -C $ReleaseFeed.TargetCheckout rev-parse --short HEAD
    $notes.Add("<!-- Built from DotCharter $(git -C $RepoRoot rev-parse --short HEAD); tagged at $taggedSha ($(Split-Path $ReleaseFeed.TargetCheckout -Leaf)). -->")
}
$notes.Add('')
$notes.Add('> **Heads-up:** from v0.70 DotCharter keeps settings it does not know when it saves. Versions before v0.70 do not: running an older DotCharter after v0.70 deletes the settings newer versions added, including the Rocksmith keys in `appconfig.json`.')
foreach ($key in $sections.Keys) {
    if ($sections[$key].Count -eq 0) { continue }
    $notes.Add('')
    $notes.Add("## $key")
    $notes.AddRange([string[]]$sections[$key])
}
Set-Content -Path $notesPath -Value ($notes -join "`n") -Encoding utf8
Write-Ok "$(Split-Path $notesPath -Leaf) ($($subjects.Count) commits since $(if ($lastTag) { $lastTag } else { 'repo start' }))"

# --- Asset 7: checksum manifest ----------------------------------------------
# SHA256SUMS.txt is a RELEASE ASSET, not a convenience: the updater fetches it to
# authenticate a downloaded package. An accepted zip is robocopied over the live
# install, so without published digests there is nothing to verify against and
# every update is applied unverified.
#
# The reader is GitHubUpdateService.TryFetchReleaseChecksumsAsync (by release
# TAG, so it works for any version, not just the latest), consumed by
# VerifyAgainstReleaseChecksumsAsync on the full-asset download path.
#
# THAT WIRE WAS MISSING FROM 0.41 TO 0.47: this file was published on every
# release page and read by nothing. Do not assume it still works because this
# comment says so — FullAssetChecksumTests is what actually proves it.
#
# Standard `sha256sum` format ("<hex>  <filename>"), which
# ReleaseChecksums.Parse reads, plus ONE `# release: v<ver>` comment line that
# binds the file to this tag (ReleaseSignature.ReleaseLinePrefix): a signed
# manifest of an older release must not verify under a newer tag. Keep the
# three in step.
$checksumPath = Join-Path $OutputDir 'SHA256SUMS.txt'
$checksumLines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -Path $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $asset -Leaf)"
}
$checksumLines = @($checksumLines) + "# release: v$Version"
Set-Content -Path $checksumPath -Value ($checksumLines -join "`n") -Encoding ascii
$assets = @($assets) + $checksumPath
Write-Ok "SHA256SUMS.txt ($($checksumLines.Count - 1) assets digested)"

# --- Asset 5: the publisher signature over SHA256SUMS.txt -------------
# The updater REFUSES a release without it: the digests above come from the same
# release page as the zip, so on their own they prove nothing about who
# published it. SignReleaseManifest.cs signs with the key outside the repo,
# refuses a key that is not the pinned one, and verifies the result exactly as
# the app will before writing it. Fails closed: no signature, no release.
$signaturePath = Join-Path $OutputDir 'SHA256SUMS.txt.sig'
& dotnet run $SignTool sign $checksumPath $SigningKeyPath $signaturePath "v$Version"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $signaturePath)) {
    throw 'Signing SHA256SUMS.txt failed (see above): the release is NOT publishable unsigned.'
}
$assets = @($assets) + $signaturePath
Write-Ok 'SHA256SUMS.txt.sig (Ed25519, verified against the pinned key)'

# --- Print the publish command (NEVER run it from here) ----------------------
# The tag goes on the commit of the repo it is created in (Resolve-ReleaseFeed).
$sha = git -C "$($ReleaseFeed.TargetCheckout)" rev-parse HEAD
Write-Host ''
if (-not $SkipUi) {
    if ($script:ToolsSummary -like 'ffmpeg yes*yt-dlp yes*' -or $script:ToolsSummary -like 'ffmpeg yes*yt-dlp left out*') {
        Write-Host "Bundled tools: $($script:ToolsSummary)" -ForegroundColor Green
    }
    else {
        Write-Host "WARNING: tools not fully bundled -> $($script:ToolsSummary)" -ForegroundColor Yellow
    }
    if ($script:SmokeSummary -eq 'passed') {
        Write-Host 'Launch smoke: the packed DotCharter.exe started, became interactive and stayed alive.' -ForegroundColor Green
    }
    else {
        Write-Host "WARNING: launch smoke $($script:SmokeSummary) - start the packed DotCharter.exe by hand before publishing." -ForegroundColor Yellow
    }
}
Write-Host 'All assets built. Review the notes, then publish with:' -ForegroundColor Cyan
Write-Host ''
$quotedAssets = ($assets | ForEach-Object { '"' + $_ + '"' }) -join " `` `n    "
Write-Host @"
gh release create v$Version -R $($ReleaseFeed.Repo) --latest ``
    --target $sha ``
    --title "DotCharter v$Version - <highlights>" ``
    --notes-file "$notesPath" ``
    $quotedAssets
"@
Write-Host ''
Write-Host 'Post-publish: verify asset sizes byte-match the local builds, then run the live updater test:' -ForegroundColor DarkGray
if ($WithPlugins) {
    Write-Host '  (a -WithPlugins release: run it from a build WITH the plugins, so it reads the feed they register)' -ForegroundColor DarkGray
}

Write-Host '  $env:DOTCHARTER_LIVE_UPDATE_TEST=''1''; $env:DOTCHARTER_LIVE_UPDATE_TOKEN=(gh auth token)' -ForegroundColor DarkGray
Write-Host '  dotnet test --project DotCharter.Tests/DotCharter.Tests.csproj --filter-method "*LiveCheckAgainstRealRepo*"' -ForegroundColor DarkGray
