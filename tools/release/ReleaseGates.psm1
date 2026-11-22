# The release run's gate decisions, as pure functions (tools/release/New-Release.ps1 calls them;
# DotCharter.Tests/Infrastructure/ReleaseGatesTests pins them).
#
# A release re-proves four things on its exact tree: the default gate, the headless UI suite
# (en-US), the headless layout sweeps in every shipped culture, and the release lane. Each is
# reused when a GREEN run of it is on record for that exact git tree (ReleaseCache.psm1
# Find-GateRecord: the merge train, an earlier release run, the export's -Gate), and run
# otherwise. Nothing here touches the machine.

# The ordered gate plan. Each step is { Name; Action = 'run' | 'reuse' | 'skip'; Record }.
# - No clean tree id ($CleanTree false): every step runs, no record applies.
# - A -Force* switch runs its step whatever is on record (-ForceHeadless covers both the
#   en-US pass and the culture sweeps).
# - -SkipHeadless (New-Release's escape hatch) skips the en-US pass and the sweeps.
# NeedsTestBuild is true when any step runs: a release whose every gate is on record does
# not build DotCharter.Tests at all.
function Get-ReleaseGatePlan {
    param(
        [bool]$CleanTree,
        $GateRecord,
        $HeadlessRecord,
        $CulturesRecord,
        $ReleaseLaneRecord,
        [switch]$ForceGate,
        [switch]$ForceHeadless,
        [switch]$ForceReleaseLane,
        [switch]$SkipHeadless
    )
    $steps = @(
        (New-ReleaseGateStep 'gate' $GateRecord $ForceGate $false $CleanTree),
        (New-ReleaseGateStep 'headless' $HeadlessRecord $ForceHeadless $SkipHeadless $CleanTree),
        (New-ReleaseGateStep 'headlessCultures' $CulturesRecord $ForceHeadless $SkipHeadless $CleanTree),
        (New-ReleaseGateStep 'releaseLane' $ReleaseLaneRecord $ForceReleaseLane $false $CleanTree)
    )
    $needsBuild = @($steps | Where-Object { $_.Action -eq 'run' }).Count -gt 0
    return [pscustomobject]@{ Steps = $steps; NeedsTestBuild = $needsBuild }
}

function New-ReleaseGateStep([string]$Name, $Record, [bool]$Force, [bool]$Skip, [bool]$CleanTree) {
    $action = 'run'
    $used = $null
    if ($Skip) { $action = 'skip' }
    elseif ($CleanTree -and -not $Force -and $Record) { $action = 'reuse'; $used = $Record }
    return [pscustomobject]@{ Name = $Name; Action = $action; Record = $used }
}

# How many concurrent testhosts each localized layout sweep gets when the sweeps run AT THE
# SAME TIME. Each culture gets the full -Shards, as long as the total stays within
# -MaxTestHosts (default 2 x -Shards: the 8 testhosts the merge train's `-Gate -Headless`
# already runs side by side on CPUs 8-15). Over that budget it is dealt evenly, the remainder
# to the first cultures, at least one each. Measured 2026-11-06 on CPUs 8-15 (the 83 layout
# classes, v1.4.0's tree): de-DE then ja-JP at 4 shards each 5:50; both at once with 2 shards
# each 5:06; both at once with 4 each 3:19 (peak 7.4 GB). The testhosts are not CPU-bound,
# so splitting the budget gained little and running both in full gained the most. Returns one
# { Culture; Shards } per culture, in the order given.
function Get-CultureShardSplit {
    param([Parameter(Mandatory)][int]$Shards, [Parameter(Mandatory)][string[]]$Cultures, [int]$MaxTestHosts = 0)
    $list = @($Cultures | Where-Object { $_ })
    if ($list.Count -eq 0) { return @() }
    $per = [Math]::Max($Shards, 1)
    $budget = if ($MaxTestHosts -gt 0) { $MaxTestHosts } else { 2 * $per }
    $budget = [Math]::Max($budget, $list.Count)
    $split = @()
    if ($per * $list.Count -le $budget) {
        foreach ($c in $list) { $split += [pscustomobject]@{ Culture = $c; Shards = $per } }
        return $split
    }
    $base = [Math]::Floor($budget / $list.Count)
    $extra = $budget % $list.Count
    for ($i = 0; $i -lt $list.Count; $i++) {
        $n = [int]$base
        if ($i -lt $extra) { $n++ }
        $split += [pscustomobject]@{ Culture = $list[$i]; Shards = $n }
    }
    return $split
}

# What the localized layout sweeps do once the en-US headless pass is graded (#614):
# 'blocked' when the en-US pass has a blocking failure (the release stops there, with that
# evidence, before any sweep starts: a sweep cannot turn a red release green), else the
# sweep step's own 'reuse' or 'run'.
function Get-HeadlessSweepAction {
    param([Parameter(Mandatory)]$SweepStep, [int]$MainFailures)
    if ($MainFailures -gt 0) { return 'blocked' }
    if ($SweepStep.Action -eq 'reuse') { return 'reuse' }
    return 'run'
}

# Whether a green headless pass may be recorded for its tree (#611): only a pass that RAN
# here and left no case unverified (a case that hit the harness fault again has no accepted
# result; recording it green would let the next release skip it).
function Test-HeadlessGateRecordable {
    param([Parameter(Mandatory)]$Step, [AllowEmptyCollection()][string[]]$Unverified = @())
    return ($Step.Action -eq 'run') -and (@($Unverified | Where-Object { $_ }).Count -eq 0)
}

Export-ModuleMember -Function Get-ReleaseGatePlan, Get-CultureShardSplit, Get-HeadlessSweepAction, Test-HeadlessGateRecordable
