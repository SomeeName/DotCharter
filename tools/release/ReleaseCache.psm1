# Release caches that live OUTSIDE every tree, so a fresh scratch export reuses them:
#
#   redist/<tool>/<key>/       the minimal ffmpeg and yt-dlp, keyed by their pins
#                              (Get-FfmpegPinKey: the build script + ffmpeg-min/*;
#                              yt-dlp: its pinned SHA-256). New-Release.ps1 still
#                              runs every version / inventory / hash check on what
#                              it copies out; a cache entry is never trusted unchecked.
#   gate-results/<tree>.json   a GREEN full gate, headless run, localized headless layout
#                              sweep (per culture) and/or release lane, recorded per git
#                              TREE id (Save-GateRecord: Merge-Train.ps1, the export's
#                              -Gate, New-Release's gates). Find-GateRecord lets the next
#                              run on an identical tree skip the identical test run.
#
# A record is the verdict of the last COMPLETED run of that lane for that tree,
# configuration and lane inputs. A run killed/crashed before grading has no verdict:
# it cannot record green and does not erase another completed verdict. A completed
# red run erases the record.
#
# Root: $env:DOTCHARTER_RELEASE_CACHE, else %LOCALAPPDATA%\DotCharter-release-cache.
# Windows PowerShell 5.1 compatible: tools/agent/Merge-Train.ps1 imports it.
# Tested by DotCharter.Tests/Infrastructure/ReleaseCacheTests.

function Get-ReleaseCacheRoot {
    if ($env:DOTCHARTER_RELEASE_CACHE) { return $env:DOTCHARTER_RELEASE_CACHE }
    if (-not $env:LOCALAPPDATA) { throw 'Neither DOTCHARTER_RELEASE_CACHE nor LOCALAPPDATA is set: no place for the release cache.' }
    return (Join-Path $env:LOCALAPPDATA 'DotCharter-release-cache')
}

function ConvertTo-Sha256Hex([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($Bytes) } finally { $sha.Dispose() }
    return (-join ($hash | ForEach-Object { $_.ToString('x2') }))
}

# One SHA-256 over the named files (path + content, CR stripped, so a CRLF checkout and
# the LF export of the same commit give the same key), in the order given.
function Get-PinKey {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$RelativePaths)
    $text = New-Object System.Text.StringBuilder
    foreach ($relative in $RelativePaths) {
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Pin input $relative is missing under $Root." }
        $content = [System.IO.File]::ReadAllText($path).Replace("`r", '')
        [void]$text.Append($relative.Replace('\', '/')).Append("`n").Append($content).Append("`n")
    }
    return (ConvertTo-Sha256Hex ([System.Text.Encoding]::UTF8.GetBytes($text.ToString())))
}

# The minimal ffmpeg's cache key: everything that decides its bytes. The build script
# (source pins, versions, configure), and every file under ffmpeg-min/ (build.sh, the
# component inventory with the version tag, the MSYS2 package pins).
function Get-FfmpegPinKey {
    param([Parameter(Mandatory)][string]$ReleaseDir)
    $minDir = Join-Path $ReleaseDir 'ffmpeg-min'
    [string[]]$files = @(Get-ChildItem -LiteralPath $minDir -File -Recurse | ForEach-Object {
            'ffmpeg-min/' + $_.FullName.Substring($minDir.Length + 1).Replace('\', '/')
        })
    [System.Array]::Sort($files, [System.StringComparer]::Ordinal)
    return (Get-PinKey -Root $ReleaseDir -RelativePaths (@('Build-MinimalFfmpeg.ps1') + $files))
}

function Get-RedistCacheDir {
    param([Parameter(Mandatory)][string]$Tool, [Parameter(Mandatory)][string]$Key)
    if ($Key -notmatch '^[0-9a-fA-F]{64}$') { throw "A redist cache key is a SHA-256, not '$Key'." }
    return (Join-Path (Join-Path (Join-Path (Get-ReleaseCacheRoot) 'redist') $Tool) $Key.ToLowerInvariant())
}

# Copies a complete cache entry into $Destination. $false (nothing copied) when the
# entry is missing any of $Files. The caller verifies what it got.
function Copy-RedistFromCache {
    param([Parameter(Mandatory)][string]$Tool, [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string[]]$Files)
    $dir = Get-RedistCacheDir -Tool $Tool -Key $Key
    foreach ($file in $Files) { if (-not (Test-Path -LiteralPath (Join-Path $dir $file) -PathType Leaf)) { return $false } }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($file in $Files) { Copy-Item -LiteralPath (Join-Path $dir $file) -Destination (Join-Path $Destination $file) -Force }
    return $true
}

# Stores $Files from $Source as the entry, whole or not at all: written beside it under
# a temporary name, then renamed into place.
function Save-RedistToCache {
    param([Parameter(Mandatory)][string]$Tool, [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string[]]$Files)
    $dir = Get-RedistCacheDir -Tool $Tool -Key $Key
    $partial = $dir + '.partial-' + [guid]::NewGuid().ToString('N')
    New-Item -ItemType Directory -Force -Path $partial | Out-Null
    try {
        foreach ($file in $Files) { Copy-Item -LiteralPath (Join-Path $Source $file) -Destination (Join-Path $partial $file) -Force }
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        Move-Item -LiteralPath $partial -Destination $dir
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
    }
    return $dir
}

function Remove-RedistCacheEntry {
    param([Parameter(Mandatory)][string]$Tool, [Parameter(Mandatory)][string]$Key)
    $dir = Get-RedistCacheDir -Tool $Tool -Key $Key
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# --- gate records ------------------------------------------------------------------

function Get-GateRecordPath {
    param([Parameter(Mandatory)][string]$Tree)
    if ($Tree -notmatch '^[0-9a-f]{40}([0-9a-f]{24})?$') { throw "'$Tree' is not a git tree id." }
    return (Join-Path (Join-Path (Get-ReleaseCacheRoot) 'gate-results') "$Tree.json")
}

function Read-GateRecord([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return ([System.IO.File]::ReadAllText($Path) | ConvertFrom-Json) } catch { return $null }
}

# All evidence transactions share ONE store lock, including reads across aliases.
# A persistent lock file avoids delete/recreate races; the exclusive handle is
# always disposed by the transaction's finally. Contention is bounded, not a hang.
function Enter-GateRecordLock([int]$TimeoutMilliseconds) {
    $root = Get-ReleaseCacheRoot
    [void][IO.Directory]::CreateDirectory($root)
    $path = Join-Path $root '.gate-records.lock'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw "Timed out waiting for the record store lock ($path; deadline $TimeoutMilliseconds ms)." }
            [Threading.Thread]::Sleep([Math]::Min(25, [Math]::Max(1, $TimeoutMilliseconds - [int]$clock.ElapsedMilliseconds)))
        }
    }
}

# Private: caller holds the store lock from BEFORE reading until AFTER replacement.
function Write-GateRecord([string]$Path, $Record) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null
    $partial = "$Path.partial-" + [guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllText($partial, ($Record | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

# Records that a GREEN run of -Gate (the default gate), -ReleaseLane (the slow classes of
# tools/ci/release-lane.json), -Headless (the headless UI suite) and/or the localized
# layout sweep in each of -HeadlessCultures finished on $Tree. Merged into an existing
# record: only accepted green lanes are saved, after Start-GateRecordRun revokes
# the planned lanes. Cultures are a union only for the same configuration/inputs.
# Configuration and InputKey identify a train lane's inputs (#614); legacy callers
# leave them empty. A train never reuses those unqualified legacy entries.
function Save-GateRecord {
    param(
        [Parameter(Mandatory)][string]$Tree,
        [string]$Commit,
        [string]$Configuration,
        [string]$InputKey,
        [switch]$Gate,
        [switch]$Headless,
        [switch]$ReleaseLane,
        [string[]]$HeadlessCultures = @(),
        [Parameter(Mandatory)][string]$Source,
        [ValidateRange(0, 60000)][int]$LockTimeoutMilliseconds = 10000
    )
    $cultures = @($HeadlessCultures | Where-Object { $_ })
    if (-not $Gate -and -not $Headless -and -not $ReleaseLane -and $cultures.Count -eq 0) {
        throw 'Save-GateRecord needs -Gate, -Headless, -ReleaseLane and/or -HeadlessCultures.'
    }
    $handle = Enter-GateRecordLock $LockTimeoutMilliseconds
    try {
        $path = Get-GateRecordPath -Tree $Tree
        $old = Read-GateRecord $path
        $record = [ordered]@{ tree = $Tree; gate = $null; headless = $null; releaseLane = $null; headlessCultures = $null }
        if ($old -and $old.tree -eq $Tree) {
            $record.gate = $old.gate; $record.headless = $old.headless; $record.releaseLane = $old.releaseLane
            if ($old.PSObject.Properties['headlessCultures']) { $record.headlessCultures = $old.headlessCultures }
        }
        $entry = [ordered]@{ configuration = $Configuration; inputKey = $InputKey; commit = $Commit; source = $Source; at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
        if ($Gate) { $record.gate = $entry }
        if ($Headless) { $record.headless = $entry }
        if ($ReleaseLane) { $record.releaseLane = $entry }
        if ($cultures.Count -gt 0) {
            $known = @()
            if ($record.headlessCultures -and $record.headlessCultures.configuration -eq $Configuration -and
                $record.headlessCultures.inputKey -eq $InputKey -and $record.headlessCultures.cultures) { $known = @($record.headlessCultures.cultures) }
            [string[]]$union = @(@($known) + $cultures | Sort-Object -Unique)
            $record.headlessCultures = [ordered]@{ cultures = $union; configuration = $Configuration; inputKey = $InputKey; commit = $Commit; source = $Source; at = $entry.at }
        }
        Write-GateRecord $path $record
        return $path
    }
    finally { $handle.Dispose() }
}

# Private lookup: the caller holds the record-store lock.
function Find-GateRecordEntry {
    param([string[]]$Trees, [string]$Kind, [string[]]$Cultures, [string]$Configuration, [string]$InputKey)
    if ($Kind -eq 'headlessCultures' -and @($Cultures | Where-Object { $_ }).Count -eq 0) { throw 'Revalidating headlessCultures requires the complete requested culture list.' }
    foreach ($tree in $Trees) {
        if (-not $tree) { continue }
        $record = Read-GateRecord (Get-GateRecordPath -Tree $tree)
        if (-not $record -or $record.tree -ne $tree -or -not $record.PSObject.Properties[$Kind]) { continue }
        $entry = $record.$Kind
        if ($Configuration -and $entry.configuration -ne $Configuration) { continue }
        if ($InputKey -and $entry.inputKey -ne $InputKey) { continue }
        if ($entry -and $Kind -eq 'headlessCultures') {
            $have = @($entry.cultures)
            if (@($Cultures | Where-Object { $_ -and $have -notcontains $_ }).Count -gt 0) { continue }
        }
        if ($entry) { return [pscustomobject]@{ Tree = $tree; Commit = $entry.commit; Source = $entry.source; At = $entry.at; Configuration = $entry.configuration; InputKey = $entry.inputKey } }
    }
    return $null
}

# The first of $Trees with a green $Kind ('gate', 'headless', 'releaseLane' or
# 'headlessCultures') on record, as { Tree, Commit, Source, At, Configuration, InputKey };
# $null when none has one.
# 'headlessCultures' is a hit only when EVERY one of -Cultures is on record for that tree.
function Find-GateRecord {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Trees,
        [Parameter(Mandatory)][ValidateSet('gate', 'headless', 'releaseLane', 'headlessCultures')][string]$Kind,
        [string[]]$Cultures = @(), [string]$Configuration, [string]$InputKey,
        [ValidateRange(0, 60000)][int]$LockTimeoutMilliseconds = 10000)
    if ($Kind -eq 'headlessCultures' -and @($Cultures | Where-Object { $_ }).Count -eq 0) { throw "Find-GateRecord -Kind headlessCultures needs -Cultures." }
    if (@($Trees | Where-Object { $_ }).Count -eq 0) { return $null }
    $handle = Enter-GateRecordLock $LockTimeoutMilliseconds
    try {
        return Find-GateRecordEntry -Trees $Trees -Kind $Kind -Cultures $Cultures -Configuration $Configuration -InputKey $InputKey
    }
    finally { $handle.Dispose() }
}

# Check every reused kind in ONE locked snapshot, on the exact original tree.
# A new completed green with matching inputs is valid; its timestamp/source need not match.
function Get-LostGateRecordKinds {
    param([System.Collections.IDictionary]$Records, [string[]]$Cultures = @(), [string]$Configuration, [string]$InputKey,
        [ValidateRange(0, 60000)][int]$LockTimeoutMilliseconds = 10000)
    if (-not $Records -or $Records.Count -eq 0) { return @() }
    $handle = Enter-GateRecordLock $LockTimeoutMilliseconds
    try {
        foreach ($kind in @($Records.Keys | Sort-Object)) {
            if ($kind -notin @('gate', 'headless', 'releaseLane', 'headlessCultures')) { throw "Unknown reused gate kind '$kind'." }
            $record = $Records[$kind]
            if (-not $record -or -not $record.Tree -or -not (Find-GateRecordEntry -Trees @($record.Tree) -Kind $kind -Cultures $Cultures -Configuration $Configuration -InputKey $InputKey)) { $kind }
        }
    }
    finally { $handle.Dispose() }
}

function Assert-GateRecordsCurrent {
    param([System.Collections.IDictionary]$Records, [string[]]$Cultures = @(), [string]$Configuration, [string]$InputKey)
    $lost = @(Get-LostGateRecordKinds -Records $Records -Cultures $Cultures -Configuration $Configuration -InputKey $InputKey)
    if ($lost.Count -gt 0) { throw "Reused $($lost -join ', ') evidence lost. Re-run will execute these lanes; no landing or publication is accepted." }
}

# Revoke every requested kind across its tree aliases under the store lock.
function Start-GateRecordRun {
    param([string[]]$Trees, [string[]]$Kinds, [string]$Configuration, [string]$InputKey,
        [ValidateRange(0, 60000)][int]$LockTimeoutMilliseconds = 10000)
    if (@($Trees | Where-Object { $_ }).Count -eq 0 -or @($Kinds).Count -eq 0) { return }
    $handle = Enter-GateRecordLock $LockTimeoutMilliseconds
    try {
        foreach ($tree in @($Trees | Where-Object { $_ } | Select-Object -Unique)) {
            $path = Get-GateRecordPath $tree
            $record = Read-GateRecord $path
            if (-not $record -or $record.tree -ne $tree) { continue }
            $changed = $false
            foreach ($kind in $Kinds) {
                if ($kind -notin @('gate', 'headless', 'releaseLane', 'headlessCultures')) { throw "Unknown gate kind '$kind'." }
                if (-not $record.PSObject.Properties[$kind] -or -not $record.$kind) { continue }
                # Revoke older input hashes too; retain evidence for other configurations.
                if ($Configuration -and $record.$kind.configuration -and $record.$kind.configuration -ne $Configuration) { continue }
                $record.$kind = $null
                $changed = $true
            }
            if ($changed) { Write-GateRecord $path $record }
        }
    }
    finally { $handle.Dispose() }
}

# A graded red completion removes greens saved before that completion.
# An attempt killed before grading does not call this completion operation.
function Complete-GateRecordRun {
    param([string[]]$Trees, [string[]]$Kinds, [string[]]$AcceptedKinds = @(), [string]$Configuration, [string]$InputKey,
        [ValidateRange(0, 60000)][int]$LockTimeoutMilliseconds = 10000)
    $failed = @($Kinds | Where-Object { $AcceptedKinds -notcontains $_ })
    if ($failed.Count -gt 0) {
        Start-GateRecordRun -Trees $Trees -Kinds $failed -Configuration $Configuration -InputKey $InputKey -LockTimeoutMilliseconds $LockTimeoutMilliseconds
    }
}

function Get-GateRecordInputs {
    param([string]$RepoRoot, [string]$Configuration = 'Debug')
    return @{ Configuration = $Configuration; InputKey = (Get-PinKey -Root $RepoRoot -RelativePaths @('tools/ci/release-lane.json')) }
}

# HEAD's tree id when $Repo's working tree is exactly HEAD (no change, no untracked
# file, submodule at its gitlink); $null otherwise. A record is only ever saved or
# reused for a tree that IS what was tested.
function Get-CleanTreeId {
    param([Parameter(Mandatory)][string]$Repo)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $status = & git -C $Repo status --porcelain --untracked-files=normal 2>$null
        if ($LASTEXITCODE -ne 0 -or $status) { return $null }
        $tree = & git -C $Repo rev-parse 'HEAD^{tree}' 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return ([string]$tree).Trim()
    }
    finally { $ErrorActionPreference = $previous }
}

# --- the export's origin ---------------------------------------------------------------
# Export-PublicSnapshot.ps1 writes export-origin.json beside the scratch DotCharter tree:
# which dev checkout and commit it came from, and both tree ids. New-Release.ps1 run from
# that scratch tree reads it to find the owner's DotCharter-local (beside the DEV checkout,
# not beside the scratch) and the gate records of the dev tree the snapshot was cut from.

function Get-ExportOriginPath {
    param([Parameter(Mandatory)][string]$RepoRoot)
    return (Join-Path (Split-Path $RepoRoot -Parent) 'export-origin.json')
}

function Write-ExportOrigin {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$DevCheckout,
        [Parameter(Mandatory)][string]$DevCommit, [Parameter(Mandatory)][string]$DevTree, [Parameter(Mandatory)][string]$PublicTree)
    $origin = [ordered]@{ devCheckout = $DevCheckout; devCommit = $DevCommit; devTree = $DevTree; publicTree = $PublicTree }
    $path = Get-ExportOriginPath -RepoRoot $RepoRoot
    [System.IO.File]::WriteAllText($path, ($origin | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

# The origin of the scratch export $RepoRoot is, or $null when it is not one (no
# export-origin.json, or no export marker beside it).
function Read-ExportOrigin {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $path = Get-ExportOriginPath -RepoRoot $RepoRoot
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $RepoRoot -Parent) '.dotcharter-public-export'))) { return $null }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { $origin = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { return $null }
    if (-not $origin.devCheckout -or -not $origin.devTree -or -not $origin.publicTree) { return $null }
    return $origin
}

# The owner's DotCharter-local folder: beside the dev checkout. For a scratch export that is
# the checkout it was exported FROM; for a worktree, its main checkout.
function Get-ReleaseLocalDir {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $origin = Read-ExportOrigin -RepoRoot $RepoRoot
    $checkout = $RepoRoot
    if ($origin) { $checkout = $origin.devCheckout }
    else {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $common = & git -C $RepoRoot rev-parse --path-format=absolute --git-common-dir 2>$null
            if ($LASTEXITCODE -eq 0 -and $common -and (Split-Path ([string]$common).Trim() -Leaf) -eq '.git') {
                $checkout = Split-Path ([string]$common).Trim() -Parent
            }
        }
        finally { $ErrorActionPreference = $previous }
    }
    return (Join-Path (Split-Path $checkout -Parent) 'DotCharter-local')
}

# The trees whose gate records stand for $Tree: itself and, when $RepoRoot is a scratch
# export whose public tree IS $Tree, the dev tree it was exported from.
function Get-GateRecordTrees {
    param([Parameter(Mandatory)][string]$RepoRoot, [string]$Tree)
    if (-not $Tree) { return @() }
    $trees = @($Tree)
    $origin = Read-ExportOrigin -RepoRoot $RepoRoot
    if ($origin -and $origin.publicTree -eq $Tree -and $origin.devTree -ne $Tree) { $trees += $origin.devTree }
    return $trees
}

# One line naming a reused record, for the console.
function Format-GateRecord {
    param([Parameter(Mandatory)]$Record)
    $commit = if ($Record.Commit) { " (commit $($Record.Commit))" } else { '' }
    return "tree $($Record.Tree)$commit, $($Record.Source), $($Record.At)"
}

Export-ModuleMember -Function Get-ReleaseCacheRoot, Get-PinKey, Get-FfmpegPinKey, Get-RedistCacheDir, Copy-RedistFromCache,
    Save-RedistToCache, Remove-RedistCacheEntry, Get-GateRecordPath, Save-GateRecord, Find-GateRecord, Get-CleanTreeId, Format-GateRecord,
    Get-ExportOriginPath, Write-ExportOrigin, Read-ExportOrigin, Get-ReleaseLocalDir, Get-GateRecordTrees, Start-GateRecordRun, Complete-GateRecordRun, Get-GateRecordInputs, Get-LostGateRecordKinds, Assert-GateRecordsCurrent
