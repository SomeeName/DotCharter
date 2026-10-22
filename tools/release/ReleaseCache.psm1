# Release caches that live OUTSIDE every tree, so a fresh scratch export reuses them:
#
#   redist/<tool>/<key>/       the minimal ffmpeg and yt-dlp, keyed by their pins
#                              (Get-FfmpegPinKey: the build script + ffmpeg-min/*;
#                              yt-dlp: its pinned SHA-256). New-Release.ps1 still
#                              runs every version / inventory / hash check on what
#                              it copies out; a cache entry is never trusted unchecked.
#   gate-results/<tree>.json   a GREEN full gate and/or headless run, recorded per git
#                              TREE id (Save-GateRecord: Merge-Train.ps1, the export's
#                              -Gate, New-Release's headless gate). Find-GateRecord lets
#                              the next run on an identical tree skip the identical test
#                              run; the build still runs.
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

# Records that a GREEN run of -Gate (the default gate), -ReleaseLane (the slow classes of
# tools/ci/release-lane.json) and/or -Headless (the headless UI suite) finished on $Tree. Merged into an existing record: a later save never erases
# an earlier kind.
function Save-GateRecord {
    param(
        [Parameter(Mandatory)][string]$Tree,
        [string]$Commit,
        [switch]$Gate,
        [switch]$Headless,
        [switch]$ReleaseLane,
        [Parameter(Mandatory)][string]$Source
    )
    if (-not $Gate -and -not $Headless -and -not $ReleaseLane) { throw 'Save-GateRecord needs -Gate, -Headless and/or -ReleaseLane.' }
    $path = Get-GateRecordPath -Tree $Tree
    $old = Read-GateRecord $path
    $record = [ordered]@{ tree = $Tree; gate = $null; headless = $null; releaseLane = $null }
    if ($old -and $old.tree -eq $Tree) { $record.gate = $old.gate; $record.headless = $old.headless; $record.releaseLane = $old.releaseLane }
    $entry = [ordered]@{ commit = $Commit; source = $Source; at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    if ($Gate) { $record.gate = $entry }
    if ($Headless) { $record.headless = $entry }
    if ($ReleaseLane) { $record.releaseLane = $entry }
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    $partial = "$path.partial-" + [guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllText($partial, ($record | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $partial -Destination $path -Force
    return $path
}

# The first of $Trees with a green $Kind ('gate', 'headless' or 'releaseLane') on record, as
# { Tree, Commit, Source, At }; $null when none has one.
function Find-GateRecord {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Trees, [Parameter(Mandatory)][ValidateSet('gate', 'headless', 'releaseLane')][string]$Kind)
    foreach ($tree in $Trees) {
        if (-not $tree) { continue }
        $record = Read-GateRecord (Get-GateRecordPath -Tree $tree)
        if (-not $record -or $record.tree -ne $tree) { continue }
        $entry = $record.$Kind
        if ($entry) { return [pscustomobject]@{ Tree = $tree; Commit = $entry.commit; Source = $entry.source; At = $entry.at } }
    }
    return $null
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
    Get-ExportOriginPath, Write-ExportOrigin, Read-ExportOrigin, Get-ReleaseLocalDir, Get-GateRecordTrees
