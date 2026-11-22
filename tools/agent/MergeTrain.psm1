<#
.SYNOPSIS
    The decisions tools/agent/Merge-Train.ps1 makes from data alone, as
    functions DotCharter.Tests drives under Windows PowerShell 5.1
    (Infrastructure/MergeTrainModuleTests): which gate a train needs, which
    lanes a green record already covers, and which processes are the train's
    own; plus the two steps that must hold under a race, the train lock and the
    landing (#610), tested with injected commands and local bare repositories.

.DESCRIPTION
    DOCS-ONLY FAST PATH (Test-MergeTrainDocsOnly). A train whose every PR
    changes only `*.md` files or files under `docs/` cannot change a binary, so
    it runs the build plus the lints that READ those files
    (Get-MergeTrainDocsOnlyClasses) instead of the full ~16,000-case gate. A
    `.resx` is NOT docs: it is compiled into the app and read by the
    localization lints. An empty path list is not docs-only either: "nothing
    changed" must never be the cheap answer by accident.

    OWN PROCESSES (Select-MergeTrainOwnProcesses). The train kills hung test
    hosts before it resets its worktree and after its gates, and ONLY those
    whose executable or command line lies inside the train worktree. The match
    is on a directory boundary: `.claude\worktrees\train` must not claim
    `.claude\worktrees\train-20261006-021523`, another agent's checkout.

    Runs under Windows PowerShell 5.1 and pwsh 7.
#>

Set-StrictMode -Off

# The test classes a docs-only train runs: every class that reads a tracked
# Markdown or docs/ file and fails on its content. DocReferenceLintTests is the
# one the fast path exists for (dead paths, test names and links in the
# guides); the rest pin a guide's wording, a file's encoding, or a notice the
# release ships. A new test that reads a guide belongs here.
$script:DocsOnlyGateClasses = @(
    '*DocReferenceLintTests',
    '*SourceEncodingLintTests',
    '*PrewarmableFlyoutTests',
    '*ReleaseAssetLintTests',
    '*BundledNamCaptureAttributionTests',
    '*DirectMlRuntimeDeploymentTests',
    '*DspChainGateHoldTests',
    '*RscpForwardCompatibilityTests'
)

# The test-host executables a gate run starts (AGENTS.md "Kill stray test hosts").
$script:TestHostNames = @('testhost.exe', 'DotCharter.Tests.exe', 'JuceSharp.Tests.exe')

function Test-MergeTrainDocPath([string]$Path) {
    $p = ($Path -replace '\\', '/').Trim()
    if (-not $p) { return $false }
    return ($p -like '*.md') -or ($p -like 'docs/*')
}

# True when EVERY path is documentation: `*.md` anywhere, or anything under
# `docs/`. Paths are repo-relative, as `gh pr view --json files` reports them.
function Test-MergeTrainDocsOnly([string[]]$Paths) {
    $list = @($Paths | Where-Object { $_ -and $_.Trim() })
    if ($list.Count -eq 0) { return $false }
    foreach ($p in $list) {
        if (-not (Test-MergeTrainDocPath $p)) { return $false }
    }
    return $true
}

# True when a path is a view: any `.axaml`, or anything in a Views folder.
function Test-MergeTrainTouchesViews([string[]]$Paths) {
    foreach ($p in @($Paths | Where-Object { $_ })) {
        $q = $p -replace '\\', '/'
        if ($q -like '*.axaml' -or $q -like '*/Views/*' -or $q -like 'Views/*') { return $true }
    }
    return $false
}

function Get-MergeTrainDocsOnlyClasses { return @($script:DocsOnlyGateClasses) }

# The test hosts among $Processes (objects with Name, ExecutablePath and
# CommandLine, the shape Win32_Process has) that run from inside $Root.
function Select-MergeTrainOwnProcesses([object[]]$Processes, [string]$Root) {
    if (-not $Root) { return @() }
    $prefix = ($Root -replace '/', '\').TrimEnd('\') + '\'
    $own = foreach ($p in @($Processes | Where-Object { $_ })) {
        if ($script:TestHostNames -notcontains [string]$p.Name) { continue }
        $exe = ([string]$p.ExecutablePath) -replace '/', '\'
        $cmd = ([string]$p.CommandLine) -replace '/', '\'
        $inside = $exe.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            ($cmd.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        if ($inside) { $p }
    }
    return @($own)
}

<#
    The train's verdict per lane from its ONE Invoke-Gate.ps1 call (#325).
    Concurrent lanes end with `lanes: gate exit G, headless exit H, release exit R`
    (the lanes that ran). Without it the lanes ran one after the other, which
    stops at the first red one: each lane that printed its summary line
    (`gate: `, `headless: `, `release: `) ran, the last one of them carries the
    exit code, and the lanes before it were green. Nothing printed (a red
    build) = the first lane asked for is red and nothing else ran. $Reused
    names the lanes the call did not run because a green run of them is on
    record for the tested tree (Get-MergeTrainLanePlan): they count as green
    and as covered.
#>
function Get-MergeTrainLaneVerdicts([string[]]$Lines, [int]$ExitCode, [bool]$WantHeadless, [string[]]$Reused = @()) {
    $reusedLanes = @($Reused | Where-Object { $_ })
    $order = @(@('gate') + @(if ($WantHeadless) { 'headless' }) + @('release') | Where-Object { $reusedLanes -notcontains $_ })
    $exits = @{}
    foreach ($lane in $reusedLanes) { $exits[$lane] = 0 }
    $summary = @($Lines | Where-Object { "$_" -match '^lanes: ' } | Select-Object -Last 1)
    if ($summary.Count -gt 0) {
        foreach ($m in [regex]::Matches([string]$summary[0], '(gate|headless|release) exit (-?\d+)')) { $exits[$m.Groups[1].Value] = [int]$m.Groups[2].Value }
    }
    elseif ($order.Count -gt 0) {
        $ran = @($order | Where-Object { $lane = $_; @($Lines | Where-Object { "$_" -match "^$lane`: " }).Count -gt 0 })
        for ($i = 0; $i -lt $ran.Count; $i++) { $exits[$ran[$i]] = if ($i -eq $ran.Count - 1) { $ExitCode } else { 0 } }
        if ($ran.Count -eq 0) { $exits[$order[0]] = if ($ExitCode -ne 0) { $ExitCode } else { 1 } }
    }
    return [pscustomobject]@{
        GateOk = $exits.ContainsKey('gate') -and $exits['gate'] -eq 0
        HeadlessOk = -not $exits.ContainsKey('headless') -or $exits['headless'] -eq 0
        ReleaseOk = -not $exits.ContainsKey('release') -or $exits['release'] -eq 0
        RanHeadless = $exits.ContainsKey('headless')
        RanRelease = $exits.ContainsKey('release')
        Complete = @(@('gate', 'release') + @(if ($WantHeadless) { 'headless' }) | Where-Object { -not $exits.ContainsKey($_) }).Count -eq 0
    }
}

<#
    WHICH LANES THE TRAIN RUNS (#614). A red train used to throw away the lanes
    that passed: the re-run repeated all of them. Now every green lane is
    recorded against the tested tree (ReleaseCache.psm1 Save-GateRecord), and a
    re-run of the same PRs on the same main, which merges to the same tree,
    runs only the lanes without a green record: the red lane runs again in
    full, alone, so every blocking failure is still graded. -Force (the train's
    -ForceGates) runs every lane. $GateRecord, $HeadlessRecord and
    $ReleaseRecord are Find-GateRecord results ($null = none). When supplied,
    Configuration and InputKey must match each record before it is reused.
    Returns Run and Reused (lane names: gate, headless, release) and Args (the
    Invoke-Gate.ps1 switches of the lanes to run).
#>
function Get-MergeTrainLanePlan {
    param([bool]$WantHeadless, $GateRecord, $HeadlessRecord, $ReleaseRecord, [bool]$Force, [string]$Configuration, [string]$InputKey)
    $lanes = @(
        [pscustomobject]@{ Name = 'gate'; Switch = '-Gate'; Record = $GateRecord; Wanted = $true },
        [pscustomobject]@{ Name = 'headless'; Switch = '-Headless'; Record = $HeadlessRecord; Wanted = $WantHeadless },
        [pscustomobject]@{ Name = 'release'; Switch = '-ReleaseLane'; Record = $ReleaseRecord; Wanted = $true }
    )
    $run = @(); $reused = @(); $switches = @()
    foreach ($lane in @($lanes | Where-Object { $_.Wanted })) {
        $matchesInputs = (-not $Configuration -or $lane.Record.Configuration -eq $Configuration) -and
            (-not $InputKey -or $lane.Record.InputKey -eq $InputKey)
        if ($lane.Record -and $matchesInputs -and -not $Force) { $reused += $lane.Name }
        else { $run += $lane.Name; $switches += $lane.Switch }
    }
    return [pscustomobject]@{ Run = [string[]]$run; Reused = [string[]]$reused; Args = [string[]]$switches }
}

# The lanes of a verdict (Get-MergeTrainLaneVerdicts) that are green: what a train,
# red or green, may record for its tree. A lane that did not run is never green.
function Get-MergeTrainGreenLanes($Verdicts) {
    $green = @()
    if ($Verdicts.GateOk) { $green += 'gate' }
    if ($Verdicts.RanHeadless -and $Verdicts.HeadlessOk) { $green += 'headless' }
    if ($Verdicts.RanRelease -and $Verdicts.ReleaseOk) { $green += 'release' }
    return [string[]]$green
}

<#
    THE TRAIN LOCK (#610). Test-Path and then Set-Content let two trains both
    find no lock, both reset the one worktree, and each delete the other's lock.
    Enter-MergeTrainLock opens $Path with no sharing and delete-on-close in ONE
    call, so exactly one process can hold it; a second gets an IOException and
    the train stops. Windows closes the handle when the holder dies, so a
    crashed train leaves no stale lock. Returns the handle; Exit-MergeTrainLock
    closes it, which deletes the file.
#>
function Enter-MergeTrainLock([string]$Path) {
    try {
        $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::DeleteOnClose)
    }
    catch {
        throw "Another merge train holds $Path. Wait for it to finish. ($($_.Exception.Message))"
    }
    $bytes = [System.Text.Encoding]::ASCII.GetBytes([string]$PID)
    $stream.SetLength(0)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    return $stream
}

function Exit-MergeTrainLock($Handle) {
    if ($Handle) { $Handle.Dispose() }
}

# Read the repository identity, ignoring author/committer environment overrides.
function Get-MergeTrainIdentity([scriptblock]$Git) {
    $name = & $Git @('config', '--get', 'user.name')
    $email = & $Git @('config', '--get', 'user.email')
    if ($name.Code -ne 0 -or -not ([string]$name.Out).Trim()) { throw 'Configure repository user.name before making a train.' }
    if ($email.Code -ne 0 -or ([string]$email.Out).Trim() -notmatch '^[^\s<>@]+@users\.noreply\.github\.com$') {
        throw 'Train landing requires repository user.email at @users.noreply.github.com; no real address may be published.'
    }
    return @{ Name = ([string]$name.Out).Trim(); Email = ([string]$email.Out).Trim() }
}

# Make the exact commit that will be gated and pushed, with GitHub's message form.
function New-MergeTrainCommit {
    param($Pr, [scriptblock]$Git)
    $identity = Get-MergeTrainIdentity $Git
    if (-not $Pr.Owner -or -not $Pr.Branch -or -not $Pr.Title) { throw "Missing GitHub merge metadata for #$($Pr.Number)." }
    $saved = @{}
    foreach ($key in @('GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL')) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        $value = if ($key -like '*EMAIL') { $identity.Email } else { $identity.Name }
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
    try {
        $message = "Merge pull request #$($Pr.Number) from $($Pr.Owner)/$($Pr.Branch)`n`n$($Pr.Title)"
        $m = & $Git @('-c', 'commit.cleanup=whitespace', 'merge', '--no-ff', '--no-edit', '-m', $message, [string]$Pr.Head)
        if ($m.Code -ne 0) { return [pscustomobject]@{ Code = $m.Code; Out = $m.Out; Commit = '' } }
        $commit = & $Git @('rev-parse', '--verify', 'HEAD^{commit}')
        return [pscustomobject]@{ Code = $commit.Code; Out = $commit.Out; Commit = ([string]$commit.Out).Trim() }
    }
    finally {
        foreach ($key in $saved.Keys) {
            # A null string argument becomes empty; .NET 9+ keeps that variable set.
            if ($null -eq $saved[$key]) { Remove-Item -LiteralPath "Env:\$key" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
        }
    }
}

Set-Variable -Name MergeConfirmationTimeoutSeconds -Value 60 -Option Constant -Scope Script
Set-Variable -Name MergeConfirmationPollSeconds -Value 2 -Option Constant -Scope Script

# GitHub can report OPEN briefly after the exact tip has already landed.
function Wait-MergeTrainConfirmation {
    param([object[]]$Prs, [string]$Repo, [scriptblock]$Gh, [string]$Tip, [string]$Base,
        [scriptblock]$Clock, [scriptblock]$Delay)
    $deadline = (& $Clock).AddSeconds($script:MergeConfirmationTimeoutSeconds)
    $pending = @($Prs)
    while ($pending.Count -gt 0) {
        $stillPending = @()
        foreach ($p in $pending) {
            if ((& $Clock) -lt $deadline) {
                try {
                    $v = & $Gh @('pr', 'view', [string]$p.Number, '--repo', $Repo, '--json', 'state,mergeCommit')
                    if ($v.Code -eq 0) {
                        $state = ([string]$v.Out) | ConvertFrom-Json -ErrorAction Stop
                        if ($state.state -eq 'MERGED' -and -not [string]::IsNullOrWhiteSpace([string]$state.mergeCommit.oid)) { continue }
                    }
                }
                catch { } # A failed/malformed polling response is not confirmation yet.
            }
            $stillPending += $p
        }
        $pending = @($stillPending)
        if ($pending.Count -eq 0) { return }
        $remaining = ($deadline - (& $Clock)).TotalSeconds
        if ($remaining -le 0) {
            $numbers = ($pending | ForEach-Object { "#$($_.Number)" }) -join ', '
            throw "Tested tip landed, but could not confirm $numbers MERGED within $($script:MergeConfirmationTimeoutSeconds) s. The tested tip $Tip IS on $Base. Do not re-run the train; inspect those PRs. No tested comments posted."
        }
        $null = & $Delay ([Math]::Min([double]$script:MergeConfirmationPollSeconds, $remaining))
    }
}

<#
    One non-forced ref update lands the immutable gated tip, for one or many PRs.
    The server rejects a divergent base advance; no subset can land. The base
    preflight gives an early diagnostic, while the push itself closes the race.
    All PR heads are checked before the optional BeforePush evidence callback,
    which is the last step before push and must release its lock before returning. A later head move cannot alter Tip,
    but may leave a PR OPEN: poll every PR under one confirmation cap before
    posting any success comment. The clock and delay are injectable.
    Git and Gh take an argument array and return @{ Out = <text>; Code = <int> }.
    Local bare-repository fixtures: Infrastructure/MergeTrainLandingTests.
#>
function Invoke-MergeTrainMerges {
    param(
        [Parameter(Mandatory)][object[]]$Prs,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$ExpectedBase,
        [Parameter(Mandatory)][string]$Comment,
        [Parameter(Mandatory)][scriptblock]$Gh,
        [Parameter(Mandatory)][scriptblock]$Git,
        [Parameter(Mandatory)][string]$Tip,
        [string]$Remote = 'origin', [string]$BaseBranch = 'main',
        [scriptblock]$BeforePush,
        [scriptblock]$ConfirmationClock = { Get-Date },
        [scriptblock]$ConfirmationDelay = { param([double]$Seconds) Start-Sleep -Milliseconds ([int][Math]::Ceiling($Seconds * 1000)) }
    )
    $null = Get-MergeTrainIdentity $Git
    if ($Tip -notmatch '^[0-9a-f]{40}([0-9a-f]{24})?$') { throw 'The gated tip must be an immutable commit id.' }
    $ref = "refs/heads/$BaseBranch"
    $base = & $Git @('ls-remote', '--exit-code', $Remote, $ref)
    if ($base.Code -ne 0) { throw "Could not read base before landing: $($base.Out). Nothing landed." }
    $now = (([string]$base.Out).Trim() -split '\s+')[0]
    if ($now -ne $ExpectedBase) { throw "base moved since gating (expected $ExpectedBase, found $now). Nothing landed." }
    foreach ($p in $Prs) {
        $v = & $Gh @('pr', 'view', [string]$p.Number, '--repo', $Repo, '--json', 'state,headRefOid')
        if ($v.Code -ne 0) { throw "Could not check head of #$($p.Number): $($v.Out). Nothing landed." }
        $state = ([string]$v.Out) | ConvertFrom-Json
        if ($state.state -ne 'OPEN' -or $state.headRefOid -ne $p.Head) { throw "PR #$($p.Number) state/head moved since gating. Nothing landed." }
    }
    # All network preflight is complete. The callback releases its store lock
    # before returning; nothing slow belongs between this check and the ref update.
    if ($BeforePush) { $null = & $BeforePush }
    $push = & $Git @('push', '--porcelain', $Remote, "$Tip`:$ref")
    if ($push.Code -ne 0) {
        $base = & $Git @('ls-remote', '--exit-code', $Remote, $ref)
        $now = (([string]$base.Out).Trim() -split '\s+')[0]
        if ($base.Code -eq 0 -and $now -eq $Tip) { throw "Tested tip is on the base despite a push error; confirm landing manually. No tested comments posted. $($push.Out)" }
        if ($base.Code -eq 0 -and $now -ne $ExpectedBase) { throw "base moved during landing (expected $ExpectedBase, found $now). Tested tip not confirmed; no tested comments posted. $($push.Out)" }
        throw "Train push failed; landing not confirmed, no tested comments posted: $($push.Out)"
    }
    $null = Wait-MergeTrainConfirmation -Prs $Prs -Repo $Repo -Gh $Gh -Tip $Tip -Base "$Remote/$BaseBranch" -Clock $ConfirmationClock -Delay $ConfirmationDelay
    foreach ($p in $Prs) {
        Write-Host "  confirmed #$($p.Number) merged with tested tip $Tip"
        $c = & $Gh @('pr', 'comment', [string]$p.Number, '--repo', $Repo, '--body', $Comment)
        if ($c.Code -ne 0) { Write-Warning "#$($p.Number) is merged, but its train comment failed: $($c.Out)" }
    }
    return @($Prs | ForEach-Object { $_.Number })
}

Export-ModuleMember -Function Test-MergeTrainDocsOnly, Test-MergeTrainTouchesViews, Get-MergeTrainDocsOnlyClasses, `
    Select-MergeTrainOwnProcesses, Get-MergeTrainLaneVerdicts, Get-MergeTrainLanePlan, Get-MergeTrainGreenLanes, `
    Enter-MergeTrainLock, Exit-MergeTrainLock, Invoke-MergeTrainMerges, New-MergeTrainCommit
