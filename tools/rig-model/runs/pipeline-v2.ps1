# The v2 run: the CHAIN DECODER, full fine-tune, on whichever data is current.
#
# Shape and rules are pipeline-full.ps1's, deliberately -- same detached launch,
# same BelowNormal priority, same 4 workers, same STOP file, same "the stage
# already running is left to finish". Read that script first; this one only
# states what differs.
#
# What differs:
#   * ONE training stage, not two. The front-end bake-off is already decided
#     (CLAP HTSAT); what is being measured here is the HEAD, and the v1 control
#     is whatever pipeline-full already produced -- re-running it would only
#     burn a GPU night reproducing a number that is already on disk.
#   * `--head v2 --unfreeze-backbone`, with the backbone at a tenth of the head
#     LR and gradient checkpointing on (batch 24 on the 3080's 10 GB with an
#     unfrozen 27.5 M tower does not fit otherwise).
#   * **It exports ONLY if v2 BEATS v1 on the READINESS RATE** -- the owner's
#     bar ("skip the refinement and be ready to go"), not the composite
#     pipeline-full ships on. A model that wins the composite while being ready
#     LESS often is not the model to ship for this goal, and the two numbers
#     genuinely can disagree: the composite rewards partial credit on every
#     head, readiness is all-or-nothing per clip.
#
#   Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File',
#     'C:\...\tools\rig-model\runs\pipeline-v2.ps1'
#
#   New-Item -ItemType File runs\STOP        # stops it between stages
#
# RUN IT AFTER pipeline-full HAS FINISHED. Both want the 3080 and the same
# cache dir; two trainers on one card is how a 4-hour stage becomes an
# out-of-memory crash at hour three.
#
# `-Data` defaults to the v1 dataset. When the DATA lane's mix-and-separate set
# lands, point it at that instead (`-Data ...\datasets\rig-v2 -Cache <new dir>`)
# -- a new dataset needs its OWN cache dir, because the fp16 cache is indexed
# positionally and its fingerprint is over the clip paths in order.

[CmdletBinding()]
param(
    [string]$Data    = "$PSScriptRoot\..\..\..\..\DotCharter-local\datasets\rig",
    [string]$Python  = "$PSScriptRoot\..\..\..\..\DotCharter-local\venv-rig\Scripts\python.exe",
    [string]$Cache   = '',
    [string]$V1Eval  = '',
    [int]$Epochs     = 45,
    [int]$Batch      = 24,
    [double]$Minutes = 300,
    [switch]$SkipTrain,
    [switch]$SkipExport
)

$ErrorActionPreference = 'Stop'
$RunsDir = $PSScriptRoot
$Root    = Split-Path $RunsDir -Parent
$Log     = Join-Path $RunsDir 'pipeline-v2.log'
$Stop    = Join-Path $RunsDir 'STOP'
if (-not $Cache)  { $Cache  = Join-Path $RunsDir 'cache-49908clips' }
if (-not $V1Eval) { $V1Eval = Join-Path $RunsDir 'clap-49908clips\eval-test.json' }

$V2Out   = Join-Path $RunsDir 'clap-v2'
$Weights = Join-Path $Root 'knob_sensitivity.json'
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

# Identical to pipeline-full's: Start-Process rather than `cmd /c start /LOW`,
# because start's first quoted argument is the window TITLE and silently
# swallows a quoted interpreter path.
function Invoke-Stage {
    param([string]$Name, [string[]]$Arguments, [string]$StageLog)

    Write-Log "=== $Name ==="
    Write-Log "    $Python $($Arguments -join ' ')"
    Write-Log "    live log: $StageLog"
    New-Item -ItemType Directory -Force (Split-Path $StageLog -Parent) | Out-Null
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

# The READINESS rate is the gate. `ready_rate` is "exact chain AND every knob
# within 0.1 travel"; the other numbers are logged beside it so a regression
# somewhere else is visible rather than merely absent from the decision.
function Get-Readiness {
    param([string]$JsonPath)
    if (-not (Test-Path $JsonPath)) { return $null }
    $m = Get-Content $JsonPath -Raw | ConvertFrom-Json
    if ($null -eq $m.ready_rate) {
        # A v1 eval-test.json from BEFORE the readiness metric existed has no
        # such field, and ConvertFrom-Json gives $null rather than failing.
        # Treating that as 0 would export v2 on a comparison that never
        # happened, so it is refused instead -- re-run eval.py on the v1
        # checkpoint, it is minutes.
        Write-Log "    $JsonPath has NO ready_rate (pre-v2 eval). Re-run eval.py on it."
        return $null
    }
    return [pscustomobject]@{
        Ready        = [double]$m.ready_rate
        ReadyLoose   = [double]$m.ready_rate_loose
        ExactChain   = [double]$m.exact_chain_match
        EditDistance = [double]$m.chain_edit_distance
        FamilyF1     = [double]$m.family_micro_f1
        SlotOccupied = [double]$m.slot_accuracy_occupied
        KnobMae      = [double]$m.knob_mae
        Head         = [string]$m.head
    }
}

function Write-Metrics {
    param([string]$Name, $M)
    if ($null -eq $M) { Write-Log "$Name : NO usable eval json"; return }
    Write-Log ("{0,-4} head={1} ready {2:F3} (loose {3:F3})  exact {4:F3}  edit {5:F2}  " +
               "famF1 {6:F3}  slotOcc {7:F3}  knobMAE {8:F4}" `
        -f $Name, $M.Head, $M.Ready, $M.ReadyLoose, $M.ExactChain, $M.EditDistance,
           $M.FamilyF1, $M.SlotOccupied, $M.KnobMae)
}

Write-Log "pipeline-v2 starting. data=$Data cache=$Cache out=$V2Out"
Write-Log "stop cleanly with:  New-Item -ItemType File '$Stop'"
if (Test-Path $Stop) { Remove-Item $Stop -Force; Write-Log "cleared a stale STOP file" }
if (Test-Path $Weights) { Write-Log "knob sensitivity weights: $Weights" }
else { Write-Log "NO knob_sensitivity.json -- the knob loss will be UNWEIGHTED (v1's MAE)." }

# ---- stage 0: the same two gates pipeline-full runs, for the same reasons ----
$code = Invoke-Stage -Name 'self-test (dataset pickles small)' -Arguments @(
    'data.py', '--data', $Data, '--cache-dir', $Cache
) -StageLog (Join-Path $RunsDir 'pipeline-v2-selftest.log')
if ($code -ne 0) { Write-Log 'self-test FAILED; refusing to start a multi-hour run.'; exit 1 }

$code = Invoke-Stage -Name 'self-test (LR horizon planning)' -Arguments @(
    'train.py', '--self-test-schedule'
) -StageLog (Join-Path $RunsDir 'pipeline-v2-selftest-schedule.log')
if ($code -ne 0) { Write-Log 'schedule self-test FAILED; refusing to start.'; exit 1 }

# ---- stage 0c: the v2 head's own unit tests --------------------------------
# CPU, seconds, no dataset. The decoder, the beam, the knob weighting, the
# readiness metric and the ONNX round-trip. A broken decoder that still trains
# is the failure this catches, and it is invisible in a training log.
$code = Invoke-Stage -Name 'self-test (v2 head unit tests)' -Arguments @(
    'tests/test_v2.py'
) -StageLog (Join-Path $RunsDir 'pipeline-v2-selftest-head.log')
if ($code -ne 0) { Write-Log 'v2 head self-test FAILED; refusing to start.'; exit 1 }

# ---- stage 1: train v2 ------------------------------------------------------
if (-not $SkipTrain) {
    if (Test-Stop) { exit 0 }
    $trainArgs = @(
        'train.py', '--data', $Data, '--out', $V2Out, '--cache-dir', $Cache,
        '--head', 'v2', '--frontend', 'clap', '--unfreeze-backbone',
        '--grad-checkpointing', '--backbone-lr-scale', '0.1',
        '--epochs', "$Epochs", '--batch', "$Batch", '--workers', '4',
        '--minutes', "$Minutes", '--require-gpu', '3080'
    )
    if (Test-Path $Weights) { $trainArgs += @('--knob-weights', $Weights) }
    $code = Invoke-Stage -Name 'train clap v2 (chain decoder, full fine-tune)' `
        -Arguments $trainArgs -StageLog (Join-Path $V2Out 'train.log')
    if ($code -ne 0) { Write-Log 'v2 training failed; stopping.'; exit 1 }
}

# ---- stage 2: eval v2 (greedy, then beam) -----------------------------------
if (Test-Stop) { exit 0 }
$code = Invoke-Stage -Name 'eval v2 greedy (test split)' -Arguments @(
    'eval.py', '--checkpoint', (Join-Path $V2Out 'best.pt'), '--data', $Data,
    '--cache-dir', $Cache, '--split', 'test',
    '--json-out', (Join-Path $V2Out 'eval-test.json'), '--require-gpu', '3080'
) -StageLog (Join-Path $V2Out 'eval.log')
if ($code -ne 0) { Write-Log 'v2 eval failed; nothing to compare.'; exit 1 }

# Beam is reported, never shipped: the ONNX graph decodes GREEDY, so exporting
# on a beam number would ship a model measured by a decoder the C# side does
# not run.
$code = Invoke-Stage -Name 'eval v2 beam k=3 (reported, NOT the export gate)' -Arguments @(
    'eval.py', '--checkpoint', (Join-Path $V2Out 'best.pt'), '--data', $Data,
    '--cache-dir', $Cache, '--split', 'test', '--beam', '3',
    '--json-out', (Join-Path $V2Out 'eval-test-beam.json'), '--require-gpu', '3080'
) -StageLog (Join-Path $V2Out 'eval-beam.log')
if ($code -ne 0) { Write-Log 'beam eval failed; continuing (it is not the gate).' }

# ---- stage 3: export ONLY if v2 beat v1 on readiness ------------------------
$v1 = Get-Readiness $V1Eval
$v2 = Get-Readiness (Join-Path $V2Out 'eval-test.json')
$v2b = Get-Readiness (Join-Path $V2Out 'eval-test-beam.json')
Write-Metrics 'v1'   $v1
Write-Metrics 'v2'   $v2
Write-Metrics 'v2b'  $v2b

if ($SkipExport) { Write-Log 'export skipped by request.'; exit 0 }
if ($null -eq $v2) { Write-Log 'no v2 eval; nothing to export.'; exit 1 }
if ($null -eq $v1) {
    Write-Log ("no comparable v1 readiness at $V1Eval -- refusing to export. Run eval.py " +
               "on the v1 checkpoint first; exporting without the comparison would ship " +
               "an UNMEASURED change.")
    exit 1
}

if ($v2.Ready -le $v1.Ready) {
    Write-Log ("v2 ready {0:F3} does NOT beat v1 {1:F3}. The shipped model is unchanged." `
        -f $v2.Ready, $v1.Ready)
    exit 0
}

Write-Log ("v2 ready {0:F3} > v1 {1:F3} -- exporting to $Target" -f $v2.Ready, $v1.Ready)
$code = Invoke-Stage -Name 'export v2' -Arguments @(
    'export_onnx.py', '--checkpoint', (Join-Path $V2Out 'best.pt'),
    '--out', $Target, '--model-id', 'rig-estimator-clap-v2'
) -StageLog (Join-Path $RunsDir 'pipeline-v2-export.log')
if ($code -ne 0) { Write-Log 'export FAILED; the shipped model is unchanged.'; exit 1 }

Write-Log 'pipeline-v2 done.'
