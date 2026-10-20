<#
.SYNOPSIS
    The decisions tools/agent/Merge-Train.ps1 makes from data alone, as
    functions DotCharter.Tests drives under Windows PowerShell 5.1
    (Infrastructure/MergeTrainModuleTests): which gate a train needs, and which
    processes are the train's own.

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

Export-ModuleMember -Function Test-MergeTrainDocsOnly, Test-MergeTrainTouchesViews, Get-MergeTrainDocsOnlyClasses, `
    Select-MergeTrainOwnProcesses
