# The ONE place the dev-build environment of the agent scripts is decided
# (Invoke-Gate.ps1 and Merge-Train.ps1 both import it, #153).

# The SDK's implicit SourceLink stamps HEAD's commit id into every project's
# AssemblyInformationalVersion (and its SourceLink file, a compiler input). Every
# new commit - a train's fresh merge commit, an agent's own commit in a worktree -
# changes HEAD, so with it on every project recompiles and the kept bin/obj save
# nothing (measured on #668: 187 s both cold and "incremental"). Off for dev builds
# only; product code reads the version with or without the `+sha` suffix
# (RscpFormatContract, AppVersionTests). Release publishes (New-Release.ps1) never
# go through here and keep the real stamp. A caller that already set the variable
# (to anything) keeps its value.
function Enable-IncrementalDevBuild {
    if ([string]::IsNullOrEmpty($env:SuppressImplicitGitSourceLink)) {
        $env:SuppressImplicitGitSourceLink = 'true'
    }
}

# ---- CPU plan (#171) ----------------------------------------------------------
# The owner games on CPUs 0-7 of the 16 logical processors, so builds and tests
# ALWAYS default to CPUs 8-15 (mask 0xFF00, 4 shards) at BelowNormal. There is no
# game detection (owner decision 2026-10-21): all cores (8 shards) only when
# -AllCores is passed by hand. The decision is a pure function (Get-DevCpuPlan);
# Set-DevCpuPlan is the only part that touches the machine.

$script:HalfMask = [int64]0xFF00

# Pure decision. Mask 0 = leave the affinity as the caller set it (fewer than 16
# logical processors: the half/all split does not exist). Shards: 4 on half, 8 on all.
function Get-DevCpuPlan {
    param([int]$Processors, [switch]$AllCores)
    if ($Processors -lt 16) {
        return [pscustomobject]@{ Name = 'unchanged'; Mask = [int64]0; Shards = 4; Reason = "$Processors logical processors (< 16), affinity left as set" }
    }
    if ($AllCores) {
        return [pscustomobject]@{ Name = 'all'; Mask = ([int64]1 -shl $Processors) - 1; Shards = 8; Reason = '-AllCores' }
    }
    return [pscustomobject]@{ Name = 'half'; Mask = $script:HalfMask; Shards = 4; Reason = 'default' }
}

# The MAIN checkout, even from a worktree (worktrees live under it).
function Get-DevMainCheckout {
    $common = "$(git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)".Trim() -replace '/', '\'
    return Split-Path $common -Parent
}

# Decide from the machine, pin THIS process (children inherit) and lower its
# priority (never raise it), print the choice. Returns the plan.
function Set-DevCpuPlan {
    param([switch]$AllCores, [switch]$Quiet)
    $plan = Get-DevCpuPlan -Processors ([int]$env:NUMBER_OF_PROCESSORS) -AllCores:$AllCores
    $self = [Diagnostics.Process]::GetCurrentProcess()
    if ($plan.Mask -ne 0) { $self.ProcessorAffinity = [IntPtr]$plan.Mask }
    if (@('Idle', 'BelowNormal') -notcontains [string]$self.PriorityClass) { $self.PriorityClass = 'BelowNormal' }
    if (-not $Quiet) { Write-Host ("cpus: {0} (mask {1:X}, {2} shards by default) - {3}" -f $plan.Name, $plan.Mask, $plan.Shards, $plan.Reason) }
    return $plan
}

Export-ModuleMember -Function Enable-IncrementalDevBuild, Get-DevCpuPlan, Get-DevMainCheckout, Set-DevCpuPlan
