<#
.SYNOPSIS
    The decisions New-AgentWorktree.ps1 makes, as pure functions (#613), so the
    tests can pin them without creating a worktree.
#>

# The untracked native pair under JuceSharp/src/JuceSharp/runtimes/win-x64/native/.
# The two are built and fetched together (Get-NativeRuntime.ps1), so they are
# kept, restored and checked together.
$script:NativeRuntimeFiles = @('juce_sharp.dll', 'juce_vst_host.exe')

function Get-JuceNativeRuntimeFiles {
    return , [string[]]$script:NativeRuntimeFiles
}

# Pure: what to do with the JuceSharp copy. The copy is the main checkout's
# WORKING TREE, so even when its HEAD is the gitlink it can hold uncommitted
# edits; the tracked tree at the gitlink is therefore put in place whenever
# the base names one. The main checkout's DLL fits only when its HEAD is the gitlink.
function Get-JuceSourcePlan {
    param([string]$Gitlink = '', [string]$CopiedHead = '')
    return [pscustomobject]@{
        Extract            = [bool]$Gitlink
        MainDllFitsGitlink = (-not $Gitlink) -or ($Gitlink -eq $CopiedHead)
    }
}

# Pure: a scratch folder that belongs to ONE invocation. Two worktrees created at
# once for the same gitlink used to share "juce-<sha>" and delete or mirror each
# other's half-extracted tree.
function Get-JuceExtractDir {
    param(
        [Parameter(Mandatory)][string]$TempRoot,
        [Parameter(Mandatory)][string]$Gitlink,
        [Parameter(Mandatory)][string]$Token
    )
    $short = if ($Gitlink.Length -gt 12) { $Gitlink.Substring(0, 12) } else { $Gitlink }
    return Join-Path $TempRoot ("juce-{0}-{1}" -f $short, $Token)
}

# Pure: the native files that were there before the mirror and are gone after it.
function Get-JuceNativeRuntimeLoss {
    param([string[]]$Before = @(), [string[]]$After = @())
    # Streams the names (no comma wrap): the caller's @( ... ) counts files.
    return @($Before | Where-Object { $_ -and $After -notcontains $_ })
}

# Pure: whether the native pair is whole - both files, or (no prebuilt yet) neither.
function Test-JuceNativeRuntimePair {
    param([string[]]$Present = @())
    $count = @($script:NativeRuntimeFiles | Where-Object { $Present -contains $_ }).Count
    return $count -eq 0 -or $count -eq $script:NativeRuntimeFiles.Count
}

# Pure: local builds can supply a worktree; a train requires both manifest hashes.
# An installation is attempted at most once, and its result is checked from fresh hashes.
function Get-JuceNativePairPlan {
    param(
        [Parameter(Mandatory)][string]$Gitlink,
        [string]$CopiedHead = '',
        [bool]$SiblingFound = $false,
        $ManifestEntry = $null,
        [System.Collections.IDictionary]$Hashes = @{},
        [switch]$Train,
        [switch]$InstallAttempted,
        [string]$FailureReason = ''
    )
    $refusal = if ($Train) { 'stop' } else { 'warn' }
    if ($FailureReason) { return [pscustomobject]@{ Action = $refusal; Reason = $FailureReason } }
    if (-not $ManifestEntry -or $ManifestEntry.commit -ne $Gitlink) {
        return [pscustomobject]@{ Action = $refusal; Reason = "No native runtime pin for JuceSharp $Gitlink." }
    }
    $mismatches = @()
    foreach ($file in $script:NativeRuntimeFiles) {
        $pin = "$($ManifestEntry.files.$file)"
        if ($pin -cnotmatch '^[0-9a-f]{64}$') {
            return [pscustomobject]@{ Action = $refusal; Reason = "No valid SHA-256 pin for $file of JuceSharp $Gitlink." }
        }
        if (-not $Hashes -or "$($Hashes[$file])" -ine $pin) { $mismatches += $file }
    }
    if ($mismatches.Count -eq 0) {
        return [pscustomobject]@{ Action = 'keep'; Reason = "Both native files match the pin for JuceSharp $Gitlink." }
    }
    $whole = $Hashes -and @($script:NativeRuntimeFiles | Where-Object { -not $Hashes[$_] }).Count -eq 0
    if (-not $Train -and -not $InstallAttempted -and $whole -and ($CopiedHead -eq $Gitlink -or $SiblingFound)) {
        return [pscustomobject]@{ Action = 'keep'; Reason = "The local native pair was built for JuceSharp $Gitlink." }
    }
    $reason = "SHA-256 missing or different from the pin for JuceSharp ${Gitlink}: $($mismatches -join ', ')."
    if ($InstallAttempted) {
        return [pscustomobject]@{ Action = $refusal; Reason = "Pinned installation did not resolve the native pair. $reason" }
    }
    return [pscustomobject]@{ Action = 'install pinned'; Reason = $reason }
}

# Reads only this tree's manifest and installed files; no marker or ABI substitutes for hashes.
function Get-JuceNativePairState {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Gitlink)
    Import-Module (Join-Path $RepoRoot 'tools/setup/NativeRuntime.psm1') -Force
    $entry = $null
    $hashes = @{}
    $failures = @()
    try {
        $manifest = Read-NativeRuntimeManifest (Join-Path $RepoRoot 'tools/setup/native-runtime.json')
        $entry = Get-NativeRuntimeEntry -Manifest $manifest -Commit $Gitlink
    }
    catch { $failures += $_.Exception.Message }
    $nativeDir = Join-Path $RepoRoot 'JuceSharp/src/JuceSharp/runtimes/win-x64/native'
    foreach ($file in $script:NativeRuntimeFiles) {
        $path = Join-Path $nativeDir $file
        try { if (Test-Path -LiteralPath $path -PathType Leaf) { $hashes[$file] = Get-FileSha256 $path } }
        catch { $failures += $_.Exception.Message }
    }
    return [pscustomobject]@{ ManifestEntry = $entry; Hashes = $hashes; FailureReason = ($failures -join '; ') }
}

Export-ModuleMember -Function Get-JuceNativePairPlan, Get-JuceNativePairState, Get-JuceNativeRuntimeFiles, Get-JuceSourcePlan, Get-JuceExtractDir,
    Get-JuceNativeRuntimeLoss, Test-JuceNativeRuntimePair
