#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the gated audio eval DETACHED, at LOW priority, into a run log.

.DESCRIPTION
    The audio eval is a 20-90 minute single-process run on a box shared with a
    game, a GPU training job and several build agents. Five such runs have been
    killed mid-flight (2026-09-12: 15 m 56 s, 13 m 13 s, 10 m 44 s; 2026-09-13:
    11 m 32 s, 46 m 58 s), and the diagnosis is what this launcher is shaped
    around:

    1.  **Another agent kills it.** Exit -1 (0xFFFFFFFF) with a zero-byte stderr
        is TerminateProcess(-1) from outside — the code .NET's Process.Kill() and
        PowerShell's Stop-Process pass, where taskkill /F would leave 1 — and each
        death lands within two minutes of another worktree starting a build.
        AGENTS.md tells every agent to kill stray `DotCharter.Tests.exe` before
        rebuilding, and by image name that is everyone's eval. Hence -Retries, and
        hence the exit code being recorded at all: through `dotnet test` it
        belongs to MSBuild and says only "no tests ran".
    2.  **It is NOT memory**, however much it looks like it. At the 2026-09-13
        kill there were 37.4 GB of commit free and 40.4 GB of physical free; the
        sampler below had logged 24-40 GB of headroom for ten minutes. The sampler
        stays precisely so nobody re-opens that question from a hunch.
    3.  **It is NOT a shell or runner timeout** (measured: a foreground command
        over its agent-tool budget is backgrounded, not killed; and there is no
        runner timeout configured anywhere in the repo). Detaching is kept anyway,
        because it costs nothing and removes the shell-lifetime class outright.

    See docs/agents/testing/audio-eval.md for the rest of the lane.

.PARAMETER Songs
    DOTCHARTER_AUDIO_EVAL_MAX — cap the number of songs. 0 leaves it unset.

.PARAMETER Match
    Adds the end-to-end matcher candidates (DOTCHARTER_MATCH_EVAL=1). This is what
    turns a 15-minute run into an hour: the retrieval library is built once, then
    every charted row spends the matcher's render budget.

.PARAMETER K
    DOTCHARTER_MATCH_EVAL_K — the retrieval library size. One render of the 20 s
    medley plus feature extraction measures 2.55 s in Release on this box, so
    K = 512 is ~22 minutes of one-off build and K = 4000 (the shipping default) is
    ~2 h 50 m. The library is cached per (extractor, K, seed), so the cost is paid
    once per machine per K.

.PARAMETER Renders
    DOTCHARTER_MATCH_EVAL_RENDERS — the matcher's per-song render budget.

.PARAMETER WallSeconds
    DOTCHARTER_MATCH_EVAL_WALL_SECONDS — the matcher's per-song wall budget. On a
    contended box this, and not the render cap, is usually what ends a search.

.PARAMETER NoChart
    DOTCHARTER_MATCH_EVAL_NOCHART — how many songs also get the naive 30-60 s span.

.PARAMETER Only
    DOTCHARTER_AUDIO_EVAL_ONLY — comma-separated song-id/title substrings.

.PARAMETER ChartedOnly
    Pass 0 to also run the matcher on head-of-song rows. Default: it does not.

.PARAMETER Configuration
    Release by default, and that is not a detail: a render costs 2.55 s in Release
    and 6.51 s in Debug, so a Debug eval is 2.5x the wall time for the same numbers.

.PARAMETER Build
    Build the test project first (LOW priority, -m:1) instead of assuming --no-build.

.PARAMETER Wait
    Block until the run finishes, printing progress. Default: return immediately
    and let the caller poll the log.

.EXAMPLE
    pwsh tools/audio-eval/Run-AudioEval.ps1 -Match -K 512 -Build
    # launches detached, prints the run log path, returns at once

.EXAMPLE
    Get-Content artifacts/audio-eval/20260913-011500/run.log -Tail 20 -Wait
#>
[CmdletBinding()]
param(
    [int] $Songs = 0,
    [switch] $Match,
    [int] $K = 0,
    [int] $Renders = 0,
    [double] $WallSeconds = 0,
    [int] $NoChart = 0,
    [string] $Only = '',
    [int] $ChartedOnly = 1,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Build,
    # Run in the foreground instead of detaching. For a two-song smoke run only.
    [switch] $Wait,

    # Internal. The script re-launches ITSELF hidden with -Attached, because the
    # thing that has to outlive the caller is not just the eval — it is the
    # supervisor that samples memory while the eval runs and records the exit code
    # when it stops. A run that dies without those two numbers is undiagnosable,
    # and that is exactly how 2026-09-12 went.
    [switch] $Attached,
    [string] $OutputDir = '',

    # How many times to RELAUNCH after a non-zero exit. Default 2, and the reason
    # is measured: on this box a long eval is killed by OTHER AGENTS, not by
    # anything wrong with it (exit -1, empty stderr, 37 GB of commit free — see
    # docs/agents/testing/audio-eval.md). Every expensive artifact is cached
    # (stems by content, excerpts, the retrieval library by (extractor, K, seed)),
    # so a relaunch re-pays the renders and nothing else.
    [int] $Retries = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$project = Join-Path $repoRoot 'DotCharter.Tests' 'DotCharter.Tests.csproj'
if (-not (Test-Path $project)) { throw "Not in a DotCharter checkout: $project is missing." }

$outputDir = if ($OutputDir -ne '') {
    $OutputDir
} else {
    Join-Path $repoRoot 'artifacts' 'audio-eval' (Get-Date -Format 'yyyyMMdd-HHmmss')
}
$stamp = Split-Path $outputDir -Leaf
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$log = Join-Path $outputDir 'run.log'

# --------------------------------------------------------------- the environment
# Set on THIS process so Start-Process inherits it. The eval reads every one of
# these by name; none of them has a command-line equivalent, which is why a
# launcher exists at all.
$env:DOTCHARTER_AUDIO_EVAL = '1'
$env:DOTCHARTER_MATCH_EVAL = $(if ($Match) { '1' } else { '0' })
$env:DOTCHARTER_MATCH_EVAL_CHARTED_ONLY = "$ChartedOnly"
$env:DOTCHARTER_MATCH_EVAL_NOCHART = "$NoChart"
if ($Songs -gt 0) { $env:DOTCHARTER_AUDIO_EVAL_MAX = "$Songs" } else { $env:DOTCHARTER_AUDIO_EVAL_MAX = $null }
if ($K -gt 0) { $env:DOTCHARTER_MATCH_EVAL_K = "$K" } else { $env:DOTCHARTER_MATCH_EVAL_K = $null }
if ($Renders -gt 0) { $env:DOTCHARTER_MATCH_EVAL_RENDERS = "$Renders" } else { $env:DOTCHARTER_MATCH_EVAL_RENDERS = $null }
if ($WallSeconds -gt 0) { $env:DOTCHARTER_MATCH_EVAL_WALL_SECONDS = "$WallSeconds" } else { $env:DOTCHARTER_MATCH_EVAL_WALL_SECONDS = $null }
if ($Only -ne '') { $env:DOTCHARTER_AUDIO_EVAL_ONLY = $Only } else { $env:DOTCHARTER_AUDIO_EVAL_ONLY = $null }

function Get-CommitHeadroom {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{
        CommitLimitGB = [math]::Round($os.TotalVirtualMemorySize / 1MB, 1)
        CommitFreeGB  = [math]::Round($os.FreeVirtualMemory / 1MB, 1)
        PhysFreeGB    = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    }
}

# ---------------------------------------------------------------------- the build
if ($Build) {
    Write-Host "Building $Configuration (LOW, -m:1)..."
    $buildArgs = @(
        'build', $project, '-c', $Configuration, '--nologo', '-v:m', '-m:1',
        '-p:UseSharedCompilation=false'
    )
    $builder = Start-Process -FilePath 'dotnet' -ArgumentList $buildArgs -NoNewWindow -PassThru
    try { $builder.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle } catch { }
    $builder.WaitForExit()
    if ($builder.ExitCode -ne 0) { throw "Build failed with exit code $($builder.ExitCode)." }
}

# --------------------------------------------------- detach: re-launch ourselves
# Everything above ran in the CALLER's process. Everything below must outlive it.
if (-not $Attached -and -not $Wait) {
    $self = $PSCommandPath
    $forward = @(
        '-NoProfile', '-NonInteractive', '-File', $self,
        '-Attached', '-OutputDir', $outputDir, '-Configuration', $Configuration,
        '-Songs', $Songs, '-K', $K, '-Renders', $Renders, '-WallSeconds', $WallSeconds,
        '-NoChart', $NoChart, '-ChartedOnly', $ChartedOnly, '-Retries', $Retries
    )
    if ($Match) { $forward += '-Match' }
    # Quoted, because a song title has spaces in it: unquoted,
    # `-Only 'Spring Thief,Walk'` reaches the supervisor as two arguments and its
    # parameter binding fails BEFORE it can write a single line of the log, which
    # reads exactly like the kill this launcher exists to survive.
    if ($Only -ne '') { $forward += @('-Only', ('"' + $Only + '"')) }

    $supervisor = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $forward `
        -WindowStyle Hidden -PassThru
    try { $supervisor.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle } catch { }

    Write-Host ''
    Write-Host 'Audio eval launched DETACHED at LOW priority.'
    Write-Host "  run log    : $log"
    Write-Host "  supervisor : pid $($supervisor.Id)"
    Write-Host "  report     : the test stamps its OWN directory under artifacts/audio-eval, seconds after this one"
    Write-Host "  follow     : Get-Content '$log' -Tail 30 -Wait"
    Write-Host ''
    return
}

# --------------------------------------------------------------------- the launch
$before = Get-CommitHeadroom
$header = @(
    "# audio eval run $stamp"
    "repo          : $repoRoot"
    "configuration : $Configuration"
    "match         : $($env:DOTCHARTER_MATCH_EVAL)  K=$K renders=$Renders wall=$WallSeconds nochart=$NoChart chartedOnly=$ChartedOnly"
    "songs         : $(if ($Songs -gt 0) { $Songs } else { 'all' })  only='$Only'"
    "started       : $(Get-Date -Format 'o')"
    "commit before : limit $($before.CommitLimitGB) GB, free $($before.CommitFreeGB) GB, phys free $($before.PhysFreeGB) GB"
    ''
) -join [Environment]::NewLine
Set-Content -Path $log -Value $header

# The test project builds a Microsoft.Testing.Platform EXECUTABLE, so it is
# launched DIRECTLY rather than through `dotnet test`. That is not a shortcut: it
# removes MSBuild and the `dotnet test` parent from the tree, so $process is the
# thing that actually runs the eval and $process.ExitCode is ITS exit code — and
# an exit code is the only evidence that separates "the CLR died" (0xC0000409,
# 0xE0434352, …) from "something outside called TerminateProcess" (1, from
# taskkill /F or a Restart Manager shutdown). Through `dotnet test` the exit code
# belongs to MSBuild and says only "no tests ran".
$exe = Join-Path $repoRoot 'DotCharter.Tests' 'bin' $Configuration 'net10.0' 'DotCharter.Tests.exe'
if (-not (Test-Path $exe)) { throw "Test executable missing: $exe — build first, or pass -Build." }

for ($attempt = 0; $attempt -le $Retries; $attempt++) {
    $suffix = if ($attempt -eq 0) { '' } else { ".retry$attempt" }
    $stdout = Join-Path $outputDir "run.out$suffix.log"
    $stderr = Join-Path $outputDir "run.err$suffix.log"

    $process = Start-Process -FilePath $exe -ArgumentList '-class', '*AudioEvalTests*' `
        -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try { $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle } catch { }

    Add-Content -Path $log -Value @(
        ''
        "attempt $attempt"
        "test pid      : $($process.Id) (LOW)"
        "stdout        : $stdout"
        "stderr        : $stderr  <-- the CLR writes 'Fatal error.' here and NOWHERE else"
        ''
        'time     commitFreeGB physFreeGB pageUseMB rssMB'
    )

    # The sampler. Without it a death is a single data point; with it a commit
    # headroom that walks to zero is visible, and a headroom that does NOT move
    # ACQUITS memory — which is the half that is easy to forget to measure, and
    # the half that overturned this lane's first diagnosis.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited) {
        $h = Get-CommitHeadroom
        $page = (Get-CimInstance Win32_PageFileUsage).CurrentUsage
        $rss = 0
        try { $rss = [int]((Get-Process -Id $process.Id -ErrorAction Stop).WorkingSet64 / 1MB) } catch { }
        Add-Content -Path $log -Value ("{0:hh\:mm\:ss} {1,12} {2,10} {3,9} {4,5}" -f `
            $sw.Elapsed, $h.CommitFreeGB, $h.PhysFreeGB, $page, $rss)
        Start-Sleep -Seconds 30
    }

    $after = Get-CommitHeadroom
    $code = $process.ExitCode
    Add-Content -Path $log -Value @(
        ''
        "finished      : $(Get-Date -Format 'o')  after $($sw.Elapsed)"
        ("exit          : {0} (0x{0:X8}){1}" -f $code,
            $(if ($code -eq -1) { '  <-- TerminateProcess(-1): Process.Kill()/Stop-Process, i.e. SOMETHING ELSE KILLED IT' } else { '' }))
        "commit after  : limit $($after.CommitLimitGB) GB, free $($after.CommitFreeGB) GB, phys free $($after.PhysFreeGB) GB"
        ''
        '--- stderr ---'
    )
    Add-Content -Path $log -Value (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)
    Add-Content -Path $log -Value @('', '--- stdout tail ---')
    Add-Content -Path $log -Value (Get-Content $stdout -Tail 25 -ErrorAction SilentlyContinue)

    if ($code -eq 0) { break }
    if ($attempt -lt $Retries) {
        Add-Content -Path $log -Value "RELAUNCHING (attempt $($attempt + 1) of $Retries) — caches make this cheap."
    }
}
