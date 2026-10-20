<#
.SYNOPSIS
    Publishes the standalone dotcharter-lyricsync tool as a self-contained
    win-x64 zip with a SHA256SUMS file.

.DESCRIPTION
    Mirrors tools/release/New-Release.ps1's conventions on purpose: the same
    RID, the same folder-wrapped zip shape, the same sha256sum-format checksum
    file, and ffmpeg bundled into <payload>/tools/ from the same git-ignored
    redist cache.

    TRIMMING IS DELIBERATELY OFF. The dependency graph reaches ONNX Runtime,
    Serilog, SkiaSharp and Avalonia through DotCharter.Services; ONNX Runtime
    does not support IL trimming (its provider registration and tensor element
    types are resolved reflectively at run time), and a trimmed build fails at
    the first InferenceSession rather than at publish. The payload is large;
    a silently broken aligner is worse.

    FFMPEG IS BUNDLED IF THE CACHE HAS IT, AND OTHERWISE THE TOOL FALLS BACK TO
    PATH. PcmAudioLoader resolves ffmpeg through MediaToolResolver, which looks
    beside the executable, then in <exeDir>/tools, then on PATH — so dropping
    ffmpeg.exe into the payload's tools folder is all that is needed. Without
    ffmpeg only .wav and .ogg decode (plus whatever the native JUCE decoder
    registers), which the README says out loud. This script does NOT download
    ffmpeg: fetching a 100 MB archive on a tool publish is New-Release.ps1's
    job, and that script leaves the cache behind for this one to reuse.

.PARAMETER OutputDir
    Where the zip and SHA256SUMS.txt land. Default: artifacts/lyric-sync.

.PARAMETER SkipFfmpeg
    Do not bundle ffmpeg even if the cache has it.

.EXAMPLE
    pwsh tools/lyric-sync/Publish-LyricSync.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$SkipFfmpeg
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Project   = Join-Path $RepoRoot 'tools/lyric-sync/DotCharter.LyricSync/DotCharter.LyricSync.csproj'
$RedistDir = Join-Path $RepoRoot 'tools/redist/win-x64'

if (-not (Test-Path $Project)) { throw "Project not found: $Project" }

# The version is the tool's own, not the app's — it ships on its own cadence.
[xml]$csproj = Get-Content $Project
$Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $Version) { throw "No <Version> in $Project" }

if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot "artifacts/lyric-sync/v$Version" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

$PayloadName = "DotCharter-LyricSync-v$Version-win-x64"
$StagingDir  = Join-Path $OutputDir "staging/$PayloadName"
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

Write-Host "Publishing $PayloadName ..." -ForegroundColor Cyan

# -m:1 and a low priority: this box is usually doing something else.
& dotnet publish $Project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false `
    -o $StagingDir --nologo -v:m -m:1
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $StagingDir 'dotcharter-lyricsync.exe'
if (-not (Test-Path $exe)) { throw "Publish produced no dotcharter-lyricsync.exe in $StagingDir" }

# A self-contained publish that lost its runtime is the failure mode worth
# catching here: it still has an .exe and dies on the user's machine.
foreach ($required in @('hostfxr.dll', 'coreclr.dll', 'onnxruntime.dll')) {
    if (-not (Test-Path (Join-Path $StagingDir $required))) {
        throw "Publish is missing $required — this payload would not run."
    }
}

# ffmpeg, from the release script's own cache. MediaToolResolver looks in
# <exeDir>/tools before it looks on PATH.
$ffmpegSource = Join-Path $RedistDir 'ffmpeg.exe'
if (-not $SkipFfmpeg -and (Test-Path $ffmpegSource)) {
    $toolsDir = Join-Path $StagingDir 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Copy-Item $ffmpegSource (Join-Path $toolsDir 'ffmpeg.exe') -Force

    $licence = Join-Path $RedistDir 'ffmpeg-LICENSE.txt'
    if (Test-Path $licence) { Copy-Item $licence (Join-Path $toolsDir 'ffmpeg-LICENSE.txt') -Force }

    Set-Content -Path (Join-Path $toolsDir 'THIRD-PARTY-NOTICES.txt') -Encoding utf8 -Value @'
ffmpeg.exe is a GPLv3 static build from https://github.com/BtbN/FFmpeg-Builds.
It is bundled unmodified. Source for that build is available from the URL above.
It is used only to decode the audio file you point the tool at.
'@
    Write-Host 'Bundled ffmpeg from the redist cache.' -ForegroundColor Green
}
else {
    Write-Warning "No ffmpeg at $ffmpegSource — the payload will rely on ffmpeg being on PATH."
    Write-Warning 'Without it only .wav and .ogg decode. Run tools/release/New-Release.ps1 once to fill the cache.'
}

Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $StagingDir 'README.md') -Force

$zipPath = Join-Path $OutputDir "$PayloadName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Folder-wrapped, like every other release asset here: extracting must never
# spray a few hundred files into the user's Downloads folder.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $StagingDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$checksumPath = Join-Path $OutputDir 'SHA256SUMS.txt'
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksumPath -Encoding ascii -Value "$hash  $(Split-Path $zipPath -Leaf)"

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "  $zipPath  ($sizeMb MB)" -ForegroundColor Green
Write-Host "  $checksumPath" -ForegroundColor Green
Write-Host ''
Write-Host 'The acoustic models are NOT in this zip (~226 MB + ~189 MB, and they do' -ForegroundColor Yellow
Write-Host 'not change between releases). The tool fetches them on first run.' -ForegroundColor Yellow
