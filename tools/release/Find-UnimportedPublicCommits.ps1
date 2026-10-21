#Requires -Version 7
<#
.SYNOPSIS
    Lists the public DotCharter and JuceSharp commits after the last snapshot that the dev
    history does not import yet (no 'Public-Commit:' trailer), and with -OpenIssues opens
    ONE dev issue per such commit. The check of the export's sync guard (PublicSync.psm1,
    the same snapshot identity and dev histories) for New-Release.ps1's preflight
    (-FailOnMissing), the scheduled watch (.github/workflows/public-sync-watch.yml; Actions
    is off on the dev repos, so it does not run there) and a local scheduled task
    (tools/release/README.md).

.DESCRIPTION
    Public data only: `git ls-remote` + `git fetch` of each public `main` into a temporary
    bare repository, no token. The trailers are read from -DevRef of the dev checkout
    (the superproject: a JuceSharp import also bumps the gitlink with the same trailers),
    and for JuceSharp also from the dev JuceSharp history at the gitlink when its object
    store is local (the submodule's git dir), as the export reads them.
    -OpenIssues uses gh (GH_TOKEN, GH_REPO) and skips a commit whose issue key
    ("Import public commit <Repo>@<sha12>") is already the start of any issue title, open
    or closed. Exit code 0 when only reminding; with -FailOnMissing, 1 while a commit is
    missing (the release preflight). The export stays the hard stop either way.

.EXAMPLE
    pwsh -NoProfile -File tools/release/Find-UnimportedPublicCommits.ps1
    pwsh -NoProfile -File tools/release/Find-UnimportedPublicCommits.ps1 -DevRef HEAD -OpenIssues
    pwsh -NoProfile -File tools/release/Find-UnimportedPublicCommits.ps1 -DevRef HEAD -FailOnMissing
#>
[CmdletBinding()]
param(
    [string]$DevRepo,
    [string]$DevRef = 'origin/main',
    [string]$PublicDotCharterUrl = 'https://github.com/SomeeName/DotCharter.git',
    [string]$PublicJuceSharpUrl = 'https://github.com/SomeeName/JuceSharp.git',
    # The dev JuceSharp object store; default: the superproject's modules/JuceSharp when present.
    [string]$DevJuceSharpGitDir,
    [switch]$OpenIssues,
    # Exit 1 while a public commit is not imported (New-Release.ps1's preflight).
    [switch]$FailOnMissing
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PublicSync.psm1') -Force

if (-not $DevRepo) { $DevRepo = Join-Path $PSScriptRoot '../..' }
$devGitDir = Invoke-SnapshotGit -Arguments @('-C', (Resolve-Path -LiteralPath $DevRepo).Path, 'rev-parse', '--path-format=absolute', '--git-common-dir')
$devCommit = Invoke-SnapshotGit -Arguments @("--git-dir=$devGitDir", 'rev-parse', '--verify', "$DevRef^{commit}")
if (-not $DevJuceSharpGitDir) { $DevJuceSharpGitDir = Join-Path $devGitDir 'modules/JuceSharp' }

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-public-sync-' + [guid]::NewGuid().ToString('N'))
$missing = @()
try {
    foreach ($repo in @(@{ Name = 'JuceSharp'; Url = $PublicJuceSharpUrl }, @{ Name = 'DotCharter'; Url = $PublicDotCharterUrl })) {
        if (-not (Invoke-SnapshotGit -Arguments @('ls-remote', '--heads', $repo.Url, 'main'))) {
            Write-Host "$($repo.Name): no public main yet"
            continue
        }
        $gitDir = Join-Path $scratch "$($repo.Name).git"
        Invoke-SnapshotGit -Arguments @('init', '-q', '--bare', $gitDir) | Out-Null
        Invoke-SnapshotGit -Arguments @("--git-dir=$gitDir", 'fetch', '-q', '--no-tags', $repo.Url, '+refs/heads/main:refs/heads/main') | Out-Null
        $found = @(Find-UnimportedPublicCommits -PublicGitDir $gitDir -PublicRef main -Repo $repo.Name `
                -DevHistories (Get-DevSyncHistories -DevGitDir $devGitDir -DevCommit $devCommit -Repo $repo.Name -DevJuceSharpGitDir $DevJuceSharpGitDir))
        Write-Host "$($repo.Name): $($found.Count) public commit(s) not imported into $DevRef"
        $missing += $found
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Get-ChildItem -LiteralPath $scratch -Recurse -File | ForEach-Object { $_.Attributes = 'Normal' }
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}

if ($missing.Count -gt 0) { Write-Host (Format-PublicSyncRefusal -Missing $missing) }
$exitCode = if ($FailOnMissing -and $missing.Count -gt 0) { 1 } else { 0 }
if (-not $OpenIssues -or $missing.Count -eq 0) { exit $exitCode }

$existing = @(gh issue list --state all --limit 1000 --search 'Import public commit in:title' --json title --jq '.[].title')
if ($LASTEXITCODE -ne 0) { throw 'gh issue list failed.' }
foreach ($c in $missing) {
    $key = Get-PublicImportIssueKey $c
    if (@($existing | Where-Object { $_.StartsWith($key, [StringComparison]::Ordinal) }).Count -gt 0) { Write-Host "issue exists: $key"; continue }
    $body = @(
        "Public commit https://github.com/SomeeName/$($c.Repo)/commit/$($c.Sha) ($($c.Author)) is not in dev yet. The next public snapshot would overwrite it."
        ''
        'Import it (a dev branch + pull request with the `Public-Commit:` trailer):'
        ''
        "    pwsh -NoProfile -File tools/release/Import-PublicCommit.ps1 -Repo $($c.Repo) -Commit $($c.Sha) -Issue <this issue>"
        ''
        'To skip it on purpose, commit an empty dev commit whose last paragraph is the trailer `Public-Commit: ' + "$($c.Repo)@$($c.Sha)" + '`, saying why.'
        ''
        'Opened by .github/workflows/public-sync-watch.yml.'
    ) -join "`n"
    gh issue create --title (Get-PublicImportIssueTitle $c) --body $body --assignee SomeeName
    if ($LASTEXITCODE -ne 0) { throw "gh issue create failed for $key." }
}
exit $exitCode
