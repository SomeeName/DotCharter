# The unattended full-dataset run: CNN control, then the CLAP transformer, each
# evaluated on the test split, then ONE export -- whichever won.
#
# Launch it DETACHED so it outlives the shell that started it:
#
#   Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File',
#     'C:\...\tools\rig-model\runs\pipeline-full.ps1'
#
# Stop it cleanly between stages:  New-Item -ItemType File runs\STOP
# (a stage already running is left to finish -- killing a trainer mid-epoch
# loses the epoch, and every stage has its own time cap anyway).
#
# Why one script rather than three commands: each stage is 1.5-4 h, the box is
# the owner's gaming machine, and a chain driven from an agent's shell dies with
# the shell. Everything here runs at BELOW-NORMAL priority with 4 loader
# workers for the same reason.
#
# BOTH RUNS SHARE ONE CACHE DIR. The full set's fp16 PCM cache is 19.4 GB and
# ~25 min to render; giving the CLAP run its own would cost both again for
# byte-identical audio.

[CmdletBinding()]
param(
    [string]$Data   = "$PSScriptRoot\..\..\..\..\DotCharter-local\datasets\rig",
    [string]$Python = "$PSScriptRoot\..\..\..\..\DotCharter-local\venv-rig\Scripts\python.exe",
    [string]$Cache  = '',
    [switch]$SkipCnn,
    [switch]$SkipClap,
    [switch]$SkipExport
)

$ErrorActionPreference = 'Stop'
$RunsDir = $PSScriptRoot
$Root    = Split-Path $RunsDir -Parent
$Log     = Join-Path $RunsDir 'pipeline-full.log'
$Stop    = Join-Path $RunsDir 'STOP'
if (-not $Cache) { $Cache = Join-Path $RunsDir 'cache-49908clips' }

$CnnOut  = Join-Path $RunsDir 'cnn-49908clips'
$ClapOut = Join-Path $RunsDir 'clap-49908clips'
$Target  = Join-Path $env:LOCALAPPDATA 'DotCharter\ai-match\rig-estimator'

function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -Path $Log -Value $line -Encoding utf8
    Write-Host $line
}

function Test-Stop {
    if (Test-Path $Stop) {
        Write-Log "STOP file present ($Stop); exiting between stages."
        return $true
    }
    return $false
}

# Runs one python stage at BELOW-NORMAL priority with its own live log, and
# returns its exit code. Start-Process rather than `cmd /c start /LOW` because
# start's first quoted argument is the window TITLE, which silently swallows a
# quoted interpreter path.
function Invoke-Stage {
    param([string]$Name, [string[]]$Arguments, [string]$StageLog)

    Write-Log "=== $Name ==="
    Write-Log "    $Python $($Arguments -join ' ')"
    Write-Log "    live log: $StageLog"
    $errLog = "$StageLog.err"
    $t0 = Get-Date
    $p = Start-Process -FilePath $Python -ArgumentList $Arguments `
        -WorkingDirectory $Root -NoNewWindow -PassThru `
        -RedirectStandardOutput $StageLog -RedirectStandardError $errLog
    try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal }
    catch { Write-Log "    (could not lower priority: $_)" }
    $p.WaitForExit()
    $code = $p.ExitCode
    Write-Log "=== $Name exited $code after $([int]((Get-Date) - $t0).TotalMinutes) min ==="
    if (Test-Path $errLog) {
        $tail = Get-Content $errLog -Tail 12 -ErrorAction SilentlyContinue
        if ($tail) { Write-Log ("    stderr tail: " + ($tail -join ' | ')) }
    }
    $tailOut = Get-Content $StageLog -Tail 6 -ErrorAction SilentlyContinue
    if ($tailOut) { foreach ($l in $tailOut) { Write-Log "    $l" } }
    return $code
}

# The composite used to pick the model that ships. Weights are here, in the
# open, because "the better one" is a judgement: the estimator is a PRIOR for
# the search-based matcher, so getting the module TYPES right (families, slots)
# is worth more than getting a knob exactly right -- search refines a knob, it
# cannot invent a missing module. The knob term is scaled against the 0.25
# chance level, not the 0.05 expert yardstick.
function Get-Score {
    param([string]$JsonPath)
    if (-not (Test-Path $JsonPath)) { return $null }
    $m = Get-Content $JsonPath -Raw | ConvertFrom-Json
    $knob = [Math]::Min([double]$m.knob_mae / 0.25, 1.0)
    $score = 0.4 * [double]$m.family_micro_f1 `
           + 0.4 * [double]$m.slot_accuracy_occupied `
           + 0.2 * (1.0 - $knob)
    return [pscustomobject]@{
        Score = $score
        FamilyF1 = [double]$m.family_micro_f1
        SlotOccupied = [double]$m.slot_accuracy_occupied
        KnobMae = [double]$m.knob_mae
        ExactChain = [double]$m.exact_chain_match
    }
}

Write-Log "pipeline-full starting. data=$Data cache=$Cache"
Write-Log "stop cleanly with:  New-Item -ItemType File '$Stop'"
if (Test-Path $Stop) { Remove-Item $Stop -Force; Write-Log "cleared a stale STOP file" }

# ---- stage 0: the Windows-spawn guard ---------------------------------------
# 17 s, and it is the whole reason the first attempt at this run died an hour
# in: a Dataset that carries its cache cannot be pickled to a spawned worker.
$code = Invoke-Stage -Name 'self-test (dataset pickles small)' -Arguments @(
    'data.py', '--data', $Data, '--cache-dir', $Cache
) -StageLog (Join-Path $RunsDir 'pipeline-selftest.log')
if ($code -ne 0) { Write-Log 'self-test FAILED; refusing to start a multi-hour run.'; exit 1 }

# ---- stage 0b: the time-aware LR planning arithmetic ------------------------
# Pure functions, no GPU, no dataset. This is what decides how good the shipped
# checkpoint is, and it is invisible in a training log -- which is exactly why
# it gets a gate of its own before a 5.5-hour chain starts.
$code = Invoke-Stage -Name 'self-test (LR horizon planning)' -Arguments @(
    'train.py', '--self-test-schedule'
) -StageLog (Join-Path $RunsDir 'pipeline-selftest-schedule.log')
if ($code -ne 0) { Write-Log 'schedule self-test FAILED; refusing to start.'; exit 1 }

# ---- stage 1: the CNN control ----------------------------------------------
if (-not $SkipCnn) {
    if (Test-Stop) { exit 0 }
    $code = Invoke-Stage -Name 'train cnn (control)' -Arguments @(
        'train.py', '--data', $Data, '--out', $CnnOut, '--cache-dir', $Cache,
        '--frontend', 'cnn', '--epochs', '60', '--batch', '32', '--workers', '4',
        '--minutes', '90', '--require-gpu', '3080'
    ) -StageLog (Join-Path $CnnOut 'train.log')
    if ($code -ne 0) { Write-Log 'cnn training failed; stopping.'; exit 1 }

    if (Test-Stop) { exit 0 }
    $code = Invoke-Stage -Name 'eval cnn (test split)' -Arguments @(
        'eval.py', '--checkpoint', (Join-Path $CnnOut 'best.pt'), '--data', $Data,
        '--cache-dir', $Cache, '--split', 'test',
        '--json-out', (Join-Path $CnnOut 'eval-test.json'), '--require-gpu', '3080'
    ) -StageLog (Join-Path $CnnOut 'eval.log')
    if ($code -ne 0) { Write-Log 'cnn eval failed; continuing to clap anyway.' }
}

# ---- stage 2: the CLAP transformer (the one that ships if it wins) ----------
if (-not $SkipClap) {
    if (Test-Stop) { exit 0 }
    $code = Invoke-Stage -Name 'train clap (transformer)' -Arguments @(
        'train.py', '--data', $Data, '--out', $ClapOut, '--cache-dir', $Cache,
        '--frontend', 'clap', '--epochs', '45', '--batch', '24', '--workers', '4',
        '--minutes', '240', '--require-gpu', '3080'
    ) -StageLog (Join-Path $ClapOut 'train.log')
    if ($code -ne 0) { Write-Log 'clap training failed; stopping.'; exit 1 }

    if (Test-Stop) { exit 0 }
    $code = Invoke-Stage -Name 'eval clap (test split)' -Arguments @(
        'eval.py', '--checkpoint', (Join-Path $ClapOut 'best.pt'), '--data', $Data,
        '--cache-dir', $Cache, '--split', 'test',
        '--json-out', (Join-Path $ClapOut 'eval-test.json'), '--require-gpu', '3080'
    ) -StageLog (Join-Path $ClapOut 'eval.log')
    if ($code -ne 0) { Write-Log 'clap eval failed; continuing to the comparison.' }
}

# ---- stage 3: export the winner, once --------------------------------------
if (Test-Stop) { exit 0 }
$cnn  = Get-Score (Join-Path $CnnOut  'eval-test.json')
$clap = Get-Score (Join-Path $ClapOut 'eval-test.json')
foreach ($pair in @(@('cnn', $cnn), @('clap', $clap))) {
    $n, $s = $pair
    if ($null -eq $s) { Write-Log "$n : NO eval-test.json"; continue }
    Write-Log ("{0,-5} score {1:F4}  famF1 {2:F3}  slotOcc {3:F3}  knobMAE {4:F4}  exactChain {5:F3}" `
        -f $n, $s.Score, $s.FamilyF1, $s.SlotOccupied, $s.KnobMae, $s.ExactChain)
}

if ($SkipExport) { Write-Log 'export skipped by request.'; exit 0 }
$winner = $null
if ($cnn -and $clap) { $winner = if ($clap.Score -ge $cnn.Score) { 'clap' } else { 'cnn' } }
elseif ($cnn)  { $winner = 'cnn' }
elseif ($clap) { $winner = 'clap' }
if (-not $winner) { Write-Log 'no eval result at all; nothing to export.'; exit 1 }

$winnerDir = if ($winner -eq 'clap') { $ClapOut } else { $CnnOut }
Write-Log "winner: $winner -> exporting to $Target"
$code = Invoke-Stage -Name "export $winner" -Arguments @(
    'export_onnx.py', '--checkpoint', (Join-Path $winnerDir 'best.pt'),
    '--out', $Target, '--model-id', "rig-estimator-$winner-49908clips"
) -StageLog (Join-Path $RunsDir 'pipeline-export.log')
if ($code -ne 0) { Write-Log 'export FAILED; the shipped model is unchanged.'; exit 1 }

Write-Log 'pipeline-full done.'
