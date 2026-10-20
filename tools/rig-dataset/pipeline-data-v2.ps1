<#
.SYNOPSIS
    The v2 data lane, end to end and unattended: beds -> render -> mix-and-separate
    -> merge.

.DESCRIPTION
    v1 (49,908 clips, `DotCharter-local\datasets\rig`) trains a model that has never
    seen a separated stem -- `render.log` says `0 separated` -- while EVERY real
    target is one, because the match lane gets its guitar out of `StemIsolator`.
    The beds folder was empty, so there was nothing to mix a clip into, and the CUDA
    EP would not load, so there was nothing to separate with. This script is the
    whole fix in order.

    NOTHING HERE TOUCHES THE GPU UNTIL THE TRAINING PIPELINE HAS EXPORTED.
    Stage 0 waits for `$env:LOCALAPPDATA\DotCharter\ai-match\rig-estimator\model.onnx`
    to be NEWER than it was when this script started, and then to stop changing for
    -SettleSeconds. Polling for the DIRECTORY would be useless: an export from the
    previous run is already sitting there.

    Every stage is RESUMABLE and every stage is at Idle priority:

      1. beds      30 synthesised backing beds + sidecars. Skips beds already on disk.
      2. render    -Clips NEW clips. Skips clip indices already in the manifest; a
                   clip's whole draw comes from its own derived seed, so a resumed
                   run produces the same corpus as an uninterrupted one.
      3. separate  Stage B on -SeparateFraction of the rows. Skips any row already
                   carrying a `separated:` tag, so a killed pass resumes exactly.
      4. merge     Folds every manifest.shard*.jsonl into manifest.jsonl, sorted.

    STOPPING IT: create the stop file (default `<Root>\STOP`). The separate stage
    checks it between rows and exits cleanly with the manifest flushed; the outer
    loop checks it between stages. Deleting the file lets a re-run pick up where it
    stopped. `Ctrl-C` also works -- the tool flushes on the first one.

.PARAMETER Root
    The v2 dataset root. Default `DotCharter-local\datasets\rig-v2`. Deliberately NOT
    the v1 root: a resumed render SKIPS indices already in the manifest, so pointing
    this at `rig` would render nothing at all.

.PARAMETER WhatIf
    Print the commands and exit.

.EXAMPLE
    pwsh tools/rig-dataset/pipeline-data-v2.ps1
    pwsh tools/rig-dataset/pipeline-data-v2.ps1 -SkipWait -Clips 2000 -Beds 4
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Root       = "$PSScriptRoot\..\..\..\DotCharter-local\datasets\rig-v2",
    [string] $DiRoot     = "$PSScriptRoot\..\..\..\DotCharter-local\datasets\rig\di",
    [string] $StemModel  = "$PSScriptRoot\..\..\..\DotCharter-local\datasets\rig\_models\htdemucs_6s.onnx",
    [string] $NamRoot    = "$env:APPDATA\DotCharter\models\nam",
    [string] $Tool,

    [int]    $Clips      = 100000,
    [int]    $Seed       = 20260913,
    [string] $Profile    = "mixed",
    [int]    $Threads    = 4,
    [int]    $Beds       = 30,
    [double] $SeparateFraction = 0.40,

    [string] $GpuName    = "3080",
    [switch] $AllowGamingGpu,
    [switch] $Cpu,
    [int]    $SeparateThreads = 1,
    [int]    $IntraOpThreads  = 2,

    [string] $ExportModel    = "$env:LOCALAPPDATA\DotCharter\ai-match\rig-estimator\model.onnx",
    [switch] $SkipWait,
    [int]    $WaitTimeoutMinutes = 600,
    [int]    $SettleSeconds      = 120,
    [int]    $PollSeconds        = 60,

    [string] $StopFile,
    [string[]] $Stages = @("beds", "render", "separate", "merge")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $StopFile) { $StopFile = Join-Path $Root "STOP" }
$logPath = Join-Path $Root "pipeline-v2.log"

function Write-Stage([string] $message) {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $message
    Write-Host $line
    if (Test-Path (Split-Path $logPath)) { Add-Content -Path $logPath -Value $line }
}

function Test-Stop {
    if (Test-Path $StopFile) {
        Write-Stage "STOP file present at $StopFile -- stopping between stages."
        return $true
    }
    return $false
}

# The tool is run as its own PROCESS at Idle priority rather than through
# `dotnet run`: `dotnet run` rebuilds, and a rebuild mid-pipeline is an MSB3027 the
# moment a render child still holds the binaries.
function Invoke-Tool([string[]] $toolArgs, [string] $what) {
    Write-Stage "$what : $Tool $($toolArgs -join ' ')"
    if ($WhatIfPreference) { return 0 }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Tool -ArgumentList $toolArgs -NoNewWindow -PassThru
    try { $p.PriorityClass = [Diagnostics.ProcessPriorityClass]::Idle } catch { }
    $p.WaitForExit()
    Write-Stage ("$what : exit {0} after {1:hh\:mm\:ss}" -f $p.ExitCode, $sw.Elapsed)
    return $p.ExitCode
}

# ---------------------------------------------------------------- resolve the tool

if (-not $Tool) {
    $here = Split-Path -Parent $PSCommandPath
    $candidates = @(
        (Join-Path $here "DotCharter.Tools.RigDataset\bin\Release\net10.0\win-x64\DotCharter.Tools.RigDataset.exe"),
        (Join-Path $here "DotCharter.Tools.RigDataset\bin\Debug\net10.0\win-x64\DotCharter.Tools.RigDataset.exe")
    )
    $Tool = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Tool -or -not (Test-Path $Tool)) {
    throw "the rig-dataset tool was not found. Build it first, or pass -Tool <exe>."
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null
Write-Stage "=== rig data v2 ==="
Write-Stage "tool=$Tool root=$Root di=$DiRoot clips=$Clips seed=$Seed profile=$Profile"
Write-Stage "beds=$Beds separate-fraction=$SeparateFraction gpu=$(if ($Cpu) { 'CPU' } else { $GpuName })"
if (Test-Path $StopFile) {
    Write-Stage "STOP file already exists at $StopFile -- delete it to run. Exiting."
    exit 0
}

# --------------------------------------------------- stage 0: wait for the export

if (-not $SkipWait -and -not $WhatIfPreference) {
    # The BASELINE is taken now, before anything waits. An export from the PREVIOUS
    # run is already on disk (there was one from 19:53 the evening this was written),
    # so "the directory exists" and even "model.onnx exists" are both true before the
    # run being waited for has produced a single epoch.
    $baseline = if (Test-Path $ExportModel) { (Get-Item $ExportModel).LastWriteTimeUtc } else { [datetime]::MinValue }
    Write-Stage "Waiting for a NEW export at $ExportModel (baseline $baseline UTC, timeout ${WaitTimeoutMinutes}m)."
    $deadline = (Get-Date).AddMinutes($WaitTimeoutMinutes)
    $settledSince = $null
    while ($true) {
        if (Test-Stop) { exit 0 }
        if ((Get-Date) -gt $deadline) {
            Write-Stage "TIMED OUT waiting for the export. Not starting: the GPU may still be busy."
            exit 2
        }

        if (Test-Path $ExportModel) {
            $stamp = (Get-Item $ExportModel).LastWriteTimeUtc
            if ($stamp -gt $baseline) {
                if (-not $settledSince) {
                    $settledSince = $stamp
                    Write-Stage "New export seen ($stamp UTC); waiting ${SettleSeconds}s for it to settle."
                } elseif ($stamp -eq $settledSince -and
                          ((Get-Date).ToUniversalTime() - $stamp).TotalSeconds -ge $SettleSeconds) {
                    Write-Stage "Export settled. The GPU is free."
                    break
                } elseif ($stamp -ne $settledSince) {
                    $settledSince = $stamp   # still being written
                }
            }
        }

        Start-Sleep -Seconds $PollSeconds
    }
}

# ------------------------------------------------------------------- stage 1: beds

$bedsDir = Join-Path $Root "beds"
if ($Stages -contains "beds") {
    if (Test-Stop) { exit 0 }
    $code = Invoke-Tool @("beds", "--out", $Root, "--count", "$Beds", "--seed", "$Seed") "beds"
    if ($code -ne 0) { Write-Stage "beds failed ($code) -- stopping: stage B is worth little without them."; exit $code }
}

# ----------------------------------------------------------------- stage 2: render

if ($Stages -contains "render") {
    if (Test-Stop) { exit 0 }
    $renderArgs = @(
        "render", "--out", $Root, "--di", $DiRoot, "--beds", $bedsDir,
        "--clips", "$Clips", "--seed", "$Seed", "--threads", "$Threads",
        "--profile", $Profile, "--progress-every", "250",
        # Stage B is its OWN pass: a render that also separated would hold the GPU for
        # the whole 7 h render and could not be resumed independently of it.
        "--separate-fraction", "0"
    )
    # `--nam` is what lets the sampler draw `namamp` at all; without a capture library
    # the module is a label the renderer cannot honour, so it is never drawn.
    if (Test-Path $NamRoot) { $renderArgs += @("--nam", $NamRoot) }
    else { Write-Stage "No NAM captures at $NamRoot -- namamp will not be drawn." }

    $code = Invoke-Tool $renderArgs "render"
    if ($code -ne 0) { Write-Stage "render failed ($code) -- stopping."; exit $code }
}

# --------------------------------------------------------------- stage 3: separate

if ($Stages -contains "separate") {
    if (Test-Stop) { exit 0 }
    $sepArgs = @(
        "separate", "--out", $Root, "--beds", $bedsDir, "--stem-model", $StemModel,
        "--separate-fraction", ([string]::Format([cultureinfo]::InvariantCulture, "{0}", $SeparateFraction)),
        "--seed", "$Seed", "--threads", "$SeparateThreads",
        "--intra-op-threads", "$IntraOpThreads",
        "--progress-every", "50", "--stop-file", $StopFile
    )
    if ($Cpu) { $sepArgs += "--cpu" } else { $sepArgs += @("--gpu-name", $GpuName) }
    if ($AllowGamingGpu) { $sepArgs += "--allow-gaming-gpu" }

    $code = Invoke-Tool $sepArgs "separate"
    if ($code -eq 3) {
        Write-Stage "separate refused to start (no usable separator). The corpus is still GOOD -- it is"
        Write-Stage "  simply v1-shaped. Fix the backend and re-run with -Stages separate,merge; nothing"
        Write-Stage "  is lost and the render is not repeated."
        exit 3
    }
    if ($code -ne 0) { Write-Stage "separate failed ($code) -- stopping."; exit $code }
}

# ------------------------------------------------------------------ stage 4: merge

if ($Stages -contains "merge") {
    $code = Invoke-Tool @("merge", "--out", $Root) "merge"
    if ($code -ne 0) { Write-Stage "merge failed ($code)."; exit $code }
}

Write-Stage "=== done ==="
$manifest = Join-Path $Root "manifest.jsonl"
if (Test-Path $manifest) {
    $rows = (Get-Content $manifest | Measure-Object -Line).Lines
    $sep  = (Select-String -Path $manifest -Pattern '"separated:' -SimpleMatch -AllMatches |
             Measure-Object).Count
    Write-Stage "$manifest : $rows rows, $sep separated."
}
exit 0
