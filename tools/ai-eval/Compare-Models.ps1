#Requires -Version 7.0

<#
.SYNOPSIS
    Runs the tone-design eval once per OpenRouter model and writes a ranked
    comparison — one command, no env var editing between models.

.DESCRIPTION
    For each model it sets the DOTCHARTER_AI_* variables the live eval already
    reads, runs

        dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build
                    --filter-class "*ToneDesignLiveEvalTests"

    at LOW process priority, sequentially, parses the
    "[ai-eval] artifacts: <dir>" line out of the run, loads that run's
    results.json and turns it into one row. The rows are ranked by p10, then
    mean, and written as compare.md + compare.json.

    A model whose run fails — bad key, a 400 on the JSON schema, a timeout, a
    gone model id — gets a row saying so. It never stops the sweep.

    The pure half (catalogue parsing, id resolution, estimating, ranking, the
    Markdown) is in CompareModels.psm1 beside this file and is executed by the
    default gate through DotCharter.Tests/Ai/Eval/ToneEvalCompareScriptTests.cs.

.PARAMETER Key
    The OpenRouter API key. Defaults to $env:OPENROUTER_API_KEY. Never logged:
    it reaches the child through the environment and is scrubbed out of every
    captured line before anything is written to disk.

.PARAMETER Models
    OpenRouter model ids. Defaults to the shortlist in CompareModels.psm1. Every
    id is verified against https://openrouter.ai/api/v1/models (no key needed)
    and one that is gone is DROPPED, with its near misses printed.

.PARAMETER Max
    Songs per model. 0 (the default) means the whole local corpus.

.PARAMETER Anchor
    Run ANCHORED (DOTCHARTER_AI_EVAL_ANCHOR=1). The numbers become upper bounds,
    not generalisation scores, and the report says so. Never compare an anchored
    table against an unanchored one.

.PARAMETER Short
    Also send the sparse prompt per song. Doubles calls and cost.

.PARAMETER OutDir
    Where compare.md / compare.json / the per-model logs land. Defaults to
    artifacts/ai-eval/compare-<timestamp> under the repository root.

.PARAMETER BaseUrl
    The OpenAI-compatible base URL. Defaults to https://openrouter.ai/api — the
    app appends /v1/chat/completions — so any OpenAI-shaped host works (xAI
    https://api.x.ai, Groq https://api.groq.com/openai, LM Studio
    http://localhost:1234).

.PARAMETER TimeoutMinutes
    Per model. A run past it is killed and recorded as a failure. Default 90.

.PARAMETER DryRun
    Verify the ids, print the estimated cost per model and the total, and exit
    WITHOUT spending anything. Needs no key.

.EXAMPLE
    pwsh -File tools/ai-eval/Compare-Models.ps1 -DryRun -Max 35

.EXAMPLE
    $env:OPENROUTER_API_KEY = 'sk-or-…'
    pwsh -File tools/ai-eval/Compare-Models.ps1 -Max 35
#>

[CmdletBinding()]
param(
    [string] $Key = $env:OPENROUTER_API_KEY,
    [string[]] $Models,
    [int] $Max = 0,
    [switch] $Anchor,
    [switch] $Short,
    [string] $OutDir,
    [string] $BaseUrl = 'https://openrouter.ai/api',
    [int] $TimeoutMinutes = 90,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Every price and score in this report is INVARIANT-formatted. Without this the
# owner's box (de-DE) prints "$0,73" and a column of European decimal commas into
# a Markdown table that is then diffed against one written on a CI runner.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

Import-Module (Join-Path $PSScriptRoot 'CompareModels.psm1') -Force -DisableNameChecking

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$testProject = Join-Path $repoRoot 'DotCharter.Tests' 'DotCharter.Tests.csproj'
if (-not (Test-Path $testProject)) {
    throw "Could not find $testProject — run this from a DotCharter checkout."
}

if (-not $Models -or $Models.Count -eq 0) { $Models = Get-ToneEvalDefaultModel }

# `pwsh -File script.ps1 -Models a,b,c` hands the whole list over as ONE string —
# -File does not parse PowerShell syntax in its arguments. Splitting here is what
# makes the documented one-liner work from cmd, from a task runner and from
# another pwsh alike, instead of silently evaluating one model called "a,b,c".
$Models = @($Models | ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot 'artifacts' 'ai-eval' "compare-$timestamp"
}

# ------------------------------------------------------- verify the model ids

Write-Host "[compare] verifying $($Models.Count) model id(s) against $BaseUrl/v1/models" -ForegroundColor Cyan
$catalog = [ordered]@{}
try {
    $payload = Invoke-RestMethod -Uri "$BaseUrl/v1/models" -TimeoutSec 60
    $catalog = ConvertFrom-OpenRouterCatalog $payload
    Write-Host "[compare] catalogue: $($catalog.Count) models"
}
catch {
    $why = Remove-ToneEvalSecret $_.Exception.Message @($Key)
    if ($DryRun) { throw "could not read the models endpoint, and -DryRun has nothing to price without it: $why" }
    Write-Warning "could not read the models endpoint ($why). Ids go through unverified and costs fall back to the report's Opus-list estimate."
}

$resolved = @()
if ($catalog.Count -gt 0) {
    foreach ($row in Resolve-ToneEvalModel -Requested $Models -Catalog $catalog) {
        switch ($row.Status) {
            'ok' { Write-Host "  ok      $($row.Id)" }
            'renamed' { Write-Host "  renamed $($row.Requested) -> $($row.Id)  [$($row.Note)]" -ForegroundColor Yellow }
            default { Write-Host "  DROPPED $($row.Requested)  [$($row.Note)]" -ForegroundColor Red }
        }
        if ($row.Status -ne 'missing') { $resolved += $row }
    }
}
else {
    $resolved = @($Models | ForEach-Object {
            [pscustomobject]@{ Requested = $_; Id = $_; Status = 'ok'; Note = 'unverified'; InUsdPerMTok = $null; OutUsdPerMTok = $null }
        })
}

if ($resolved.Count -eq 0) { throw 'no usable model ids left after verification.' }

# ------------------------------------------------------------- the estimate

# The eval caps at MaxSongs and the local corpus measured 35 songs on 2026-09-12;
# with -Max 0 we cannot know the count before the run, so the estimate says which
# number it assumed instead of quietly under-reporting.
$assumedSongs = if ($Max -gt 0) { $Max } else { 35 }
$calls = $assumedSongs * ($(if ($Short) { 2 } else { 1 }))
$estimates = @(Get-ToneEvalCostEstimate -Models $resolved -Calls $calls)

Write-Host ''
Write-Host "[compare] $($resolved.Count) model(s) x $calls request(s) each" -ForegroundColor Cyan
if ($Max -le 0) { Write-Host "          (-Max not set: pricing assumes the 35-song local corpus)" }
$total = 0.0
foreach ($estimate in $estimates) {
    $price = if ($null -ne $estimate.InUsdPerMTok) {
        ('${0:F2}/${1:F2} per MTok' -f $estimate.InUsdPerMTok, $estimate.OutUsdPerMTok)
    }
    else { 'price unknown' }
    $cost = if ($null -ne $estimate.EstimateUsd) { '${0:F2}' -f $estimate.EstimateUsd } else { '?' }
    if ($null -ne $estimate.EstimateUsd) { $total += $estimate.EstimateUsd }
    Write-Host ('  {0,-34} {1,8}   {2}' -f $estimate.Id, $cost, $price)
}
Write-Host ('  {0,-34} {1,8}   TOTAL (at ~6,000 in / 900 out per request)' -f '', ('${0:F2}' -f $total)) -ForegroundColor Cyan
Write-Host ''

if ($DryRun) {
    Write-Host '[compare] -DryRun: nothing was spent and no run was started.' -ForegroundColor Green
    return
}

if ([string]::IsNullOrWhiteSpace($Key)) {
    throw 'no key: pass -Key or set OPENROUTER_API_KEY (use -DryRun to price a sweep without one).'
}

# ---------------------------------------------------------------- the sweep

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "[compare] out: $OutDir"

$saved = @{}
foreach ($name in @(
        'DOTCHARTER_AI_EVAL', 'DOTCHARTER_AI_PROVIDER', 'DOTCHARTER_AI_MODEL',
        'DOTCHARTER_AI_BASE_URL', 'DOTCHARTER_AI_KEY', 'DOTCHARTER_AI_EVAL_MAX',
        'DOTCHARTER_AI_EVAL_SHORT', 'DOTCHARTER_AI_EVAL_ANCHOR',
        'DOTCHARTER_AI_EVAL_PRICE_IN', 'DOTCHARTER_AI_EVAL_PRICE_OUT')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}

$rows = @()
try {
    $env:DOTCHARTER_AI_EVAL = '1'
    $env:DOTCHARTER_AI_PROVIDER = 'openai'
    $env:DOTCHARTER_AI_BASE_URL = $BaseUrl
    $env:DOTCHARTER_AI_KEY = $Key
    $env:DOTCHARTER_AI_EVAL_SHORT = $(if ($Short) { '1' } else { $null })
    $env:DOTCHARTER_AI_EVAL_ANCHOR = $(if ($Anchor) { '1' } else { $null })
    if ($Max -gt 0) { $env:DOTCHARTER_AI_EVAL_MAX = "$Max" } else { $env:DOTCHARTER_AI_EVAL_MAX = '100000' }

    $index = 0
    foreach ($model in $resolved) {
        $index++
        Write-Host ''
        Write-Host "[compare] ($index/$($resolved.Count)) $($model.Id)" -ForegroundColor Cyan

        $env:DOTCHARTER_AI_MODEL = $model.Id
        # The model's REAL price goes into the run, so its own report.md prints the
        # right dollars instead of Opus list price.
        $env:DOTCHARTER_AI_EVAL_PRICE_IN = if ($null -ne $model.InUsdPerMTok) {
            $model.InUsdPerMTok.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        } else { $null }
        $env:DOTCHARTER_AI_EVAL_PRICE_OUT = if ($null -ne $model.OutUsdPerMTok) {
            $model.OutUsdPerMTok.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        } else { $null }

        $slug = $model.Id -replace '[^A-Za-z0-9._-]', '_'
        $stdout = Join-Path $OutDir "$slug.out.log"
        $stderr = Join-Path $OutDir "$slug.err.log"
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 'ok'
        $failure = ''
        $run = $null

        try {
            $arguments = @(
                'test', '--project', $testProject, '--no-build',
                '--filter-class', '*ToneDesignLiveEvalTests'
            )
            $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments `
                -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            try { $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle } catch { }

            if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
                try { $process.Kill($true) } catch { }
                $status = 'timeout'
                $failure = "no result after $TimeoutMinutes minute(s); the run was killed."
            }
            elseif ($process.ExitCode -ne 0) {
                $status = 'failed'
            }
        }
        catch {
            $status = 'failed'
            $failure = Remove-ToneEvalSecret $_.Exception.Message @($Key)
        }
        $clock.Stop()

        # Scrub before anything is read back or shown — a gateway that echoes the
        # Authorization header would otherwise put the key in a file on disk.
        foreach ($log in @($stdout, $stderr)) {
            if (Test-Path $log) {
                $clean = Remove-ToneEvalSecret (Get-Content -Raw -LiteralPath $log) @($Key)
                Set-Content -LiteralPath $log -Value $clean -NoNewline
            }
        }

        $console = if (Test-Path $stdout) { Get-Content -Raw -LiteralPath $stdout } else { '' }
        $artifacts = Get-ToneEvalArtifactDirectory $console
        if ($artifacts -and (Test-Path (Join-Path $artifacts 'results.json'))) {
            try {
                $run = Get-Content -Raw -LiteralPath (Join-Path $artifacts 'results.json') | ConvertFrom-Json
                if ($status -ne 'ok') { $status = 'partial' }
            }
            catch {
                $status = 'failed'
                $failure = "results.json at $artifacts could not be parsed: $($_.Exception.Message)"
            }
        }
        elseif ($status -eq 'ok') {
            $status = 'failed'
            $failure = 'the run reported success but printed no "[ai-eval] artifacts:" line.'
        }

        if ($status -ne 'ok' -and [string]::IsNullOrWhiteSpace($failure)) {
            $failure = Get-ToneEvalFailureReason $console
        }

        $row = ConvertTo-ToneEvalCompareRow -Model $model.Id -Run $run -Status $status `
            -Failure $failure -WallSeconds $clock.Elapsed.TotalSeconds `
            -InUsdPerMTok $model.InUsdPerMTok -OutUsdPerMTok $model.OutUsdPerMTok
        $rows += $row

        if ($row.Status -eq 'ok') {
            Write-Host ('  p10 {0:F1} · mean {1:F1} · p50 {2:F1} · {3} failed · ${4:F4} · {5:F0}s' -f `
                    $row.P10, $row.Mean, $row.Median, $row.Failures, $row.CostUsd, $row.WallSeconds)
        }
        else {
            Write-Host "  $($row.Status.ToUpperInvariant()): $($row.Failure)" -ForegroundColor Red
        }
    }
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}

# --------------------------------------------------------------- the report

$meta = @{
    TimestampUtc = $timestamp
    BaseUrl      = $BaseUrl
    Songs        = if ($Max -gt 0) { "$Max (capped)" } else { 'the whole local corpus' }
    Short        = [bool] $Short
    Mode         = if ($Anchor) { 'ANCHORED (upper bound, not a generalisation score)' } else { 'unanchored' }
}

$markdown = Write-ToneEvalCompareMarkdown -Rows $rows -Meta $meta
$markdownPath = Join-Path $OutDir 'compare.md'
Set-Content -LiteralPath $markdownPath -Value $markdown -NoNewline

$jsonPath = Join-Path $OutDir 'compare.json'
[pscustomobject]@{
    TimestampUtc = $timestamp
    BaseUrl      = $BaseUrl
    MaxSongs     = $Max
    Short        = [bool] $Short
    Anchored     = [bool] $Anchor
    Rows         = @(Sort-ToneEvalCompareRow $rows)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -NoNewline

Write-Host ''
Write-Host "[compare] $markdownPath" -ForegroundColor Green
Write-Host "[compare] $jsonPath" -ForegroundColor Green
