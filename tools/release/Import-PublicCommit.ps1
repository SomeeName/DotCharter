#Requires -Version 7
<#
.SYNOPSIS
    Imports a commit (or a merged pull request) of a PUBLIC repo into dev, so the next
    public snapshot does not overwrite it. The counterpart of the sync guard in
    Export-PublicSnapshot.ps1 (PublicSync.psm1).

.DESCRIPTION
    1. Fetches public `main` (-PublicUrl, default https://github.com/SomeeName/<Repo>.git)
       into the target repo: the dev checkout for DotCharter, its JuceSharp submodule for
       JuceSharp. -Pr resolves a MERGED pull request to the commits it put on public main
       (gh): the non-merge commits of a merge commit, the rebased commits, or the squash.
    2. Per commit, oldest first: each changed text file of its parent and of itself is
       turned back into dev form by the reverse rename map (Convert-PublicTextToDev: a
       public line that is the renamed form of a line in the dev file becomes that dev line;
       other lines are left alone), and the resulting diff goes through `git apply -3
       --index`, so a dev file that moved on since the snapshot gets a real 3-way merge.
       The JuceSharp gitlink of a DotCharter commit is never applied (import the JuceSharp
       commit instead).
    3. Commits ONCE on a new branch from -Base (default origin/main; the submodule's own
       origin/main for JuceSharp) with a 'Public-Commit: <Repo>@<sha>' trailer per public
       commit and a 'Co-authored-by:' per contributor. For JuceSharp the dev superproject
       also gets a gitlink bump commit carrying the same trailers (the scheduled watch reads
       only the superproject's history), on a branch of the same name.
    4. Pushes and opens the dev pull request(s) (gh, assignee SomeeName, 'Fixes #<Issue>'
       when -Issue is given; JuceSharp first). -NoPullRequest stops after the commits.

    Refuses on a dirty target, a public commit that is not on public main, an unmerged pull
    request, or a conflict (the branch is left with the conflict for you to resolve; the
    trailer lines to commit with are printed).

.EXAMPLE
    pwsh -NoProfile -File tools/release/Import-PublicCommit.ps1 -Repo DotCharter -Commit 0123abc...
    pwsh -NoProfile -File tools/release/Import-PublicCommit.ps1 -Repo JuceSharp -Pr 4 -Issue 81
#>
[CmdletBinding(DefaultParameterSetName = 'Commit')]
param(
    [Parameter(Mandatory)][ValidateSet('DotCharter', 'JuceSharp')][string]$Repo,
    [Parameter(Mandatory, ParameterSetName = 'Commit')][string[]]$Commit,
    [Parameter(Mandatory, ParameterSetName = 'Pr')][int]$Pr,
    # The dev DotCharter checkout; defaults to the repo holding this script.
    [string]$DevRepo,
    [string]$Base = 'origin/main',
    [string]$PublicUrl,
    [string]$Branch,
    # The dev issue the pull request fixes (the watch opens one per public commit).
    [int]$Issue,
    [switch]$NoPullRequest
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PublicSync.psm1') -Force

function Git { Invoke-SnapshotGit -Arguments ([string[]]$args) }

$owner = 'SomeeName'
if (-not $PublicUrl) { $PublicUrl = "https://github.com/$owner/$Repo.git" }
if (-not $DevRepo) { $DevRepo = Join-Path $PSScriptRoot '../..' }
$DevRepo = (Resolve-Path -LiteralPath $DevRepo).Path
$target = if ($Repo -eq 'JuceSharp') { Join-Path $DevRepo 'JuceSharp' } else { $DevRepo }
if (-not (Test-Path -LiteralPath (Join-Path $target '.git'))) { throw "$target is not a checkout (for JuceSharp: init the submodule first)." }

foreach ($repoPath in @($DevRepo, $target) | Select-Object -Unique) {
    if (Git -C $repoPath status --porcelain --untracked-files=no --ignore-submodules=all) { throw "$repoPath has uncommitted changes; commit or move them first." }
}

# --- 1. the public commits ------------------------------------------------------------------
Git -C $target fetch -q --no-tags $PublicUrl main | Out-Null
$publicMain = Git -C $target rev-parse FETCH_HEAD
if ($PSCmdlet.ParameterSetName -eq 'Pr') {
    $view = gh pr view $Pr -R "$owner/$Repo" --json state,mergeCommit,commits,title | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "gh pr view $Pr failed." }
    if ($view.state -ne 'MERGED') { throw "$owner/$Repo#$Pr is $($view.state): import it once it is merged, so the trailer names the commit on public main." }
    $merge = $view.mergeCommit.oid
    $parents = @((Git -C $target log -1 --format=%P $merge).Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
    if ($parents.Count -gt 1) {
        $Commit = @((Git -C $target rev-list --no-merges --reverse "$($parents[0])..$merge").Split("`n", [StringSplitOptions]::RemoveEmptyEntries))
    }
    else {
        # Squash (one commit) or rebase (the PR's commits, re-created): walk back while the
        # subject is one of the PR's own.
        $subjects = @($view.commits | ForEach-Object { $_.messageHeadline })
        $list = [System.Collections.Generic.List[string]]::new()
        $list.Add($merge)
        $at = $merge
        while ($list.Count -lt $subjects.Count) {
            $up = @((Git -C $target log -1 --format=%P $at).Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
            if ($up.Count -ne 1 -or $subjects -cnotcontains (Git -C $target log -1 --format=%s $up[0])) { break }
            $at = $up[0]
            $list.Insert(0, $at)
        }
        $Commit = @($list)
    }
    $title = "Import public $Repo PR #${Pr}: $($view.title)"
    $slug = "pr-$Pr"
}
$Commit = @($Commit | ForEach-Object { Git -C $target rev-parse --verify "$_^{commit}" })
foreach ($c in $Commit) {
    # Non-empty when $c is NOT reachable from public main.
    if (Git -C $target rev-list -1 $c --not $publicMain) { throw "$c is not on public $Repo main; only merged public commits are imported." }
    if (@((Git -C $target log -1 --format=%P $c).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)).Count -ne 1) {
        throw "$c is a merge or a root commit; import its non-merge commits (or -Pr)."
    }
}
# A snapshot is dev's own tree going out, never a contribution coming back. Recognised by the
# ONE snapshot identity the export and the watch use (PublicSync.psm1 Get-SnapshotIdentities).
$publicGitDir = Git -C $target rev-parse --path-format=absolute --git-common-dir
$publicRecords = @(Get-SyncCommitRecords -GitDir $publicGitDir -Ref $publicMain)
$snapshotIdentities = Get-SnapshotIdentities -Records $publicRecords
foreach ($record in $publicRecords | Where-Object { $Commit -contains $_.Sha }) {
    if (Test-PublicSnapshotRecord -Record $record -Product $Repo -Identities $snapshotIdentities) {
        throw "$($record.Sha) is a public snapshot ($($record.Subject)), not a contribution; there is nothing to import."
    }
}
if ($PSCmdlet.ParameterSetName -eq 'Commit') {
    $first = Git -C $target log -1 --format=%s $Commit[-1]
    $title = if ($Commit.Count -eq 1) { "Import public $Repo commit $($Commit[0].Substring(0, 12)): $first" } else { "Import $($Commit.Count) public $Repo commits (last: $first)" }
    $slug = $Commit[-1].Substring(0, 12)
}
if (-not $Branch) { $Branch = "import/public-$($Repo.ToLowerInvariant())-$slug" }

# --- 2. the dev branch and the reverse-mapped diffs ----------------------------------------------
$targetBase = if ($Repo -eq 'JuceSharp') { 'origin/main' } else { $Base }
if ($Repo -eq 'JuceSharp') { Git -C $DevRepo switch -q -c $Branch $Base | Out-Null }
Git -C $target switch -q -c $Branch $targetBase | Out-Null

$latin1 = [System.Text.Encoding]::Latin1
function Test-Binary([byte[]]$Bytes) { [Array]::IndexOf($Bytes, [byte]0, 0, [Math]::Min($Bytes.Length, 8192)) -ge 0 }

# The tree of $Rev with every path in $Paths turned back into dev form against the dev file
# at HEAD, and $Pinned entries copied from $PinTree (the gitlink stays out of the diff).
function New-DevFormTree([string]$Rev, [string[]]$Paths, [string]$PinTree) {
    $index = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-import-index-' + [guid]::NewGuid().ToString('N'))
    $envIndex = @{ GIT_INDEX_FILE = $index }
    try {
        Invoke-SnapshotGit -Arguments @('-C', $target, 'read-tree', $Rev) -Environment $envIndex | Out-Null
        foreach ($path in $Paths) {
            $entry = Invoke-SnapshotGit -Arguments @('-C', $target, 'ls-files', '-s', '--', $path) -Environment $envIndex
            if ($entry -notmatch '^(100\d{3}) ([0-9a-f]{40}) \d\t') { continue }
            $mode = $Matches[1]
            $bytes = Invoke-SnapshotGit -Arguments @('-C', $target, 'cat-file', 'blob', $Matches[2]) -AsBytes
            if (Test-Binary $bytes) { continue }
            $devBytes = Invoke-SnapshotGit -Arguments @('-C', $target, 'cat-file', 'blob', "HEAD:$path") -AsBytes -AllowExitCodes @(128)
            if ($devBytes.Length -eq 0 -or (Test-Binary $devBytes)) { continue }
            $text = $latin1.GetString($bytes)
            $devForm = Convert-PublicTextToDev -PublicText $text -DevText $latin1.GetString($devBytes)
            if ($devForm -ceq $text) { continue }
            $blob = Invoke-SnapshotGit -Arguments @('-C', $target, 'hash-object', '-w', '--stdin') -InputBytes $latin1.GetBytes($devForm)
            Invoke-SnapshotGit -Arguments @('-C', $target, 'update-index', '--cacheinfo', "$mode,$blob,$path") -Environment $envIndex | Out-Null
        }
        if ($PinTree -and $Repo -eq 'DotCharter') {
            $pin = Invoke-SnapshotGit -Arguments @('-C', $target, 'ls-tree', $PinTree, 'JuceSharp')
            if ($pin -match '^160000 commit ([0-9a-f]{40})\t') {
                Invoke-SnapshotGit -Arguments @('-C', $target, 'update-index', '--add', '--cacheinfo', "160000,$($Matches[1]),JuceSharp") -Environment $envIndex | Out-Null
            }
            else { Invoke-SnapshotGit -Arguments @('-C', $target, 'update-index', '--force-remove', '--', 'JuceSharp') -Environment $envIndex | Out-Null }
        }
        return Invoke-SnapshotGit -Arguments @('-C', $target, 'write-tree') -Environment $envIndex
    }
    finally { Remove-Item -LiteralPath $index -Force -ErrorAction SilentlyContinue }
}

$authors = [System.Collections.Generic.List[string]]::new()
$me = "$(Git -C $target config user.name) <$(Git -C $target config user.email)>"
foreach ($c in $Commit) {
    $author = Git -C $target log -1 --format='%an <%ae>' $c
    if ($author -cne $me -and $authors -cnotcontains $author) { $authors.Add($author) }
}
$trailers = @($Commit | ForEach-Object { Get-PublicCommitTrailer -Repo $Repo -Commit $_ })
$trailers += @($authors | ForEach-Object { "Co-authored-by: $_" })
foreach ($c in $Commit) {
    $paths = @((Git -C $target diff --name-only -z --no-renames "$c^" $c).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    if ($Repo -eq 'DotCharter' -and $paths -contains 'JuceSharp') { Write-Warning "$c moves the JuceSharp gitlink; that is not applied here (import the JuceSharp commit instead)." }
    $before = New-DevFormTree "$c^" $paths $null
    $after = New-DevFormTree $c $paths "$c^"
    $patch = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-import-' + [guid]::NewGuid().ToString('N') + '.patch')
    try {
        [System.IO.File]::WriteAllBytes($patch, (Invoke-SnapshotGit -Arguments @('-C', $target, 'diff', '--binary', '--full-index', $before, $after) -AsBytes))
        if ((Get-Item -LiteralPath $patch).Length -gt 0) {
            try { Git -C $target apply -3 --index $patch | Out-Null }
            catch {
                Write-Host "applying $c did not go cleanly in $target (branch $Branch). Resolve, apply the remaining commits, then commit with:"
                Write-Host ($trailers -join "`n")
                throw
            }
        }
    }
    finally { Remove-Item -LiteralPath $patch -Force -ErrorAction SilentlyContinue }
}

# --- 3. the commit(s) -------------------------------------------------------------------------------
$body = "Imported from public $owner/$Repo by tools/release/Import-PublicCommit.ps1, so the next public snapshot keeps it."
$message = "$title`n`n$body`n`n$($trailers -join "`n")`n"
$messageFile = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-import-msg-' + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.File]::WriteAllText($messageFile, $message)
    if (-not (Git -C $target diff --cached --name-only)) { Write-Warning 'The public change is already in dev: committing the trailer alone.' }
    Git -C $target commit -q --allow-empty -F $messageFile | Out-Null
    if ($Repo -eq 'JuceSharp') {
        Git -C $DevRepo add -- JuceSharp | Out-Null
        Git -C $DevRepo commit -q --allow-empty -F $messageFile | Out-Null
    }
}
finally { Remove-Item -LiteralPath $messageFile -Force -ErrorAction SilentlyContinue }

Write-Host "committed $(Git -C $target rev-parse HEAD) on $Branch in $target"
if ($Repo -eq 'JuceSharp') { Write-Host "gitlink bump $(Git -C $DevRepo rev-parse HEAD) on $Branch in $DevRepo (pin the native pair before merging: tools/release/Publish-NativeRuntime.ps1 -Upload)" }

# --- 4. push + pull request(s) -----------------------------------------------------------------------
if ($NoPullRequest) { Write-Host 'not pushed (-NoPullRequest).'; exit 0 }
$prBody = "$body`n`n$($trailers -join "`n")"
if ($Issue) { $prBody += "`n`nFixes #$Issue" }
foreach ($repoPath in @(if ($Repo -eq 'JuceSharp') { $target }) + $DevRepo) {
    Git -C $repoPath push -q -u origin $Branch | Out-Null
    Push-Location -LiteralPath $repoPath
    try {
        gh pr create --base main --head $Branch --assignee $owner --title $title --body $prBody
        if ($LASTEXITCODE -ne 0) { throw "gh pr create failed in $repoPath." }
    }
    finally { Pop-Location }
}
