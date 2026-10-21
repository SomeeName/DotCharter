# The public -> dev sync guard. A commit merged on a PUBLIC repo (a contributor's pull
# request) exists nowhere in dev, and the next snapshot (PublicSnapshot.psm1) is the dev
# tree, so it would silently overwrite that commit. Every public commit after the last
# snapshot must therefore be imported into dev first, as a dev commit that carries the
# trailer
#
#     Public-Commit: <DotCharter|JuceSharp>@<full public sha>
#
# (Import-PublicCommit.ps1 writes it). Export-PublicSnapshot.ps1 refuses while one is
# missing; Find-UnimportedPublicCommits.ps1 is the same check for New-Release.ps1's
# preflight (-FailOnMissing), the scheduled watch and a local scheduled task.
# A public commit deliberately NOT imported still gets a dev commit with the trailer (an
# empty one saying why): the decision is then on record, and the guard is satisfied.
#
# Snapshot commits are recognised by their one-line message (Get-SnapshotMessage) AND
# their identity: author = committer = the snapshot identity (Get-PublicSnapshotIdentity,
# public-snapshot-identity.json - the ONE definition the export, the watch and the import
# share), or the author of the public root commit (the first snapshot). No content
# heuristics. The export's -Push also refuses a snapshot commit or tag that carries any
# other email (Find-SnapshotIdentityProblems).
#
# Every private name is assembled from pieces, as in PublicSnapshot.psm1.
# Tested by DotCharter.Tests/Infrastructure/PublicSyncGuardTests (pwsh, fixture repos).
# Requires PowerShell 7; runs on Windows and on the Linux runner of the watch.

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1')

$script:TrailerKey = 'Public-Commit'
$script:PublicRepos = @('DotCharter', 'JuceSharp')
$script:IdentityFile = Join-Path $PSScriptRoot 'public-snapshot-identity.json'

# The identity every public snapshot commit and tag carries (author, committer, tagger):
# Name, Email and Identity ("Name <email>"), read from public-snapshot-identity.json so the
# address is written down once.
function Get-PublicSnapshotIdentity {
    $config = Get-Content -LiteralPath $script:IdentityFile -Raw | ConvertFrom-Json
    $name = "$($config.name)".Trim()
    $email = "$($config.email)".Trim()
    if (-not $name -or $email -notmatch '^[^\s<>@]+@[^\s<>@]+$') { throw "$script:IdentityFile needs a name and an email." }
    return [pscustomobject]@{ Name = $name; Email = $email; Identity = "$name <$email>" }
}

function Get-PublicCommitTrailerKey { $script:TrailerKey }

# "Public-Commit: DotCharter@<sha>" - the full 40-character id, lower case.
function Get-PublicCommitTrailer {
    param([Parameter(Mandatory)][ValidateSet('DotCharter', 'JuceSharp')][string]$Repo, [Parameter(Mandatory)][string]$Commit)
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw "A Public-Commit trailer names a full lower-case commit id, not '$Commit'." }
    return "${script:TrailerKey}: $Repo@$Commit"
}

# One record per commit reachable from $Ref in $GitDir (any git dir or repo path).
function Get-SyncCommitRecords {
    param([Parameter(Mandatory)][string]$GitDir, [Parameter(Mandatory)][string]$Ref)
    $format = '%H%x1f%P%x1f%an <%ae>%x1f%cn <%ce>%x1f%s%x1f%B%x1e'
    $out = Invoke-SnapshotGit -Arguments @("--git-dir=$GitDir", 'log', '--topo-order', "--format=$format", $Ref)
    foreach ($chunk in $out.Split([char]0x1e)) {
        $chunk = $chunk.Trim()
        if (-not $chunk) { continue }
        $f = $chunk.Split([char]0x1f)
        [pscustomobject]@{
            Sha = $f[0]; Parents = @($f[1].Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
            Author = $f[2]; Committer = $f[3]; Subject = $f[4]; Message = $f[5].Trim()
        }
    }
}

# The identities a snapshot commit may carry: the snapshot identity and the authors of the
# root commits (the first snapshot is always a root commit). The ONE definition: the export,
# Find-UnimportedPublicCommits.ps1 (release preflight, watch) and the import all use it.
function Get-SnapshotIdentities {
    param([Parameter(Mandatory)][object[]]$Records)
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$set.Add((Get-PublicSnapshotIdentity).Identity)
    foreach ($r in $Records) { if ($r.Parents.Count -eq 0) { [void]$set.Add($r.Author) } }
    return , @($set)
}

function Test-PublicSnapshotRecord {
    param([Parameter(Mandatory)][object]$Record, [Parameter(Mandatory)][string]$Product, [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Identities)
    if ($Record.Author -cne $Record.Committer -or $Identities -cnotcontains $Record.Author) { return $false }
    $initial = Get-SnapshotMessage -Product $Product -Version '0'
    $update = '^' + [regex]::Escape((Get-SnapshotMessage -Product $Product -Version 'VERSION' -Parent 'parent')).Replace('VERSION', '[0-9][0-9A-Za-z.+-]*') + '$'
    return ($Record.Message -ceq $initial) -or ($Record.Message -cmatch $update)
}

# The non-merge commits on $Ref after its last snapshot (the newest snapshot on the
# first-parent chain), oldest first. Every commit when there is no snapshot at all.
# A merge commit carries no change of its own: its non-merge commits are listed.
function Get-PublicCommitsSinceSnapshot {
    param(
        [Parameter(Mandatory)][string]$GitDir,
        [Parameter(Mandatory)][string]$Ref,
        [Parameter(Mandatory)][ValidateSet('DotCharter', 'JuceSharp')][string]$Repo
    )
    $records = @(Get-SyncCommitRecords -GitDir $GitDir -Ref $Ref)
    if ($records.Count -eq 0) { return }
    $bySha = @{}
    foreach ($r in $records) { $bySha[$r.Sha] = $r }
    $identities = Get-SnapshotIdentities -Records $records

    $snapshot = $null
    for ($r = $records[0]; $r; $r = if ($r.Parents.Count -gt 0) { $bySha[$r.Parents[0]] } else { $null }) {
        if (Test-PublicSnapshotRecord -Record $r -Product $Repo -Identities $identities) { $snapshot = $r; break }
    }
    $range = @('rev-list', '--no-merges', '--reverse', $Ref)
    if ($snapshot) { $range += "^$($snapshot.Sha)" }
    $shas = Invoke-SnapshotGit -Arguments (@("--git-dir=$GitDir") + $range)
    foreach ($sha in $shas.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        $r = $bySha[$sha.Trim()]
        [pscustomobject]@{ Repo = $Repo; Sha = $r.Sha; Subject = $r.Subject; Author = $r.Author; Snapshot = $(if ($snapshot) { $snapshot.Sha } else { $null }) }
    }
}

# Every "<Repo>@<sha>" named by a Public-Commit trailer in the history of $Ref (in $GitDir),
# as a set. Only real trailers count (git's own trailer parsing), not a mention in prose.
function Get-ImportedPublicCommits {
    param([Parameter(Mandatory)][string]$GitDir, [Parameter(Mandatory)][string]$Ref)
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $out = Invoke-SnapshotGit -Arguments @("--git-dir=$GitDir", 'log', "--grep=^${script:TrailerKey}:", "--format=%(trailers:key=${script:TrailerKey},valueonly)", $Ref)
    foreach ($line in $out.Split("`n")) {
        $m = [regex]::Match($line.Trim(), '^(DotCharter|JuceSharp)@([0-9a-f]{40})$')
        if ($m.Success) { [void]$set.Add("$($m.Groups[1].Value)@$($m.Groups[2].Value)") }
    }
    return , $set
}

# The public commits since the last snapshot that no dev history names. $DevHistories is
# a list of @{ GitDir; Ref } - for JuceSharp both the dev superproject (its gitlink bumps
# carry the trailer too) and the dev JuceSharp history.
function Find-UnimportedPublicCommits {
    param(
        [Parameter(Mandatory)][string]$PublicGitDir,
        [Parameter(Mandatory)][string]$PublicRef,
        [Parameter(Mandatory)][ValidateSet('DotCharter', 'JuceSharp')][string]$Repo,
        [Parameter(Mandatory)][object[]]$DevHistories
    )
    $imported = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($h in $DevHistories) { $imported.UnionWith((Get-ImportedPublicCommits -GitDir $h.GitDir -Ref $h.Ref)) }
    foreach ($c in Get-PublicCommitsSinceSnapshot -GitDir $PublicGitDir -Ref $PublicRef -Repo $Repo) {
        if (-not $imported.Contains("$Repo@$($c.Sha)")) { $c }
    }
}

# The dev histories whose Public-Commit trailers count for $Repo: the dev superproject at
# $DevCommit, and for JuceSharp also the dev JuceSharp history at the gitlink of
# $DevCommit when its object store ($DevJuceSharpGitDir) is at hand (the watch has none:
# it reads the superproject alone, which is why a JuceSharp import bumps the gitlink).
function Get-DevSyncHistories {
    param(
        [Parameter(Mandatory)][string]$DevGitDir,
        [Parameter(Mandatory)][string]$DevCommit,
        [Parameter(Mandatory)][ValidateSet('DotCharter', 'JuceSharp')][string]$Repo,
        [string]$DevJuceSharpGitDir
    )
    $histories = @(@{ GitDir = $DevGitDir; Ref = $DevCommit })
    if ($Repo -eq 'JuceSharp' -and $DevJuceSharpGitDir -and (Test-Path -LiteralPath $DevJuceSharpGitDir)) {
        $line = Invoke-SnapshotGit -Arguments @("--git-dir=$DevGitDir", 'ls-tree', $DevCommit, 'JuceSharp')
        if ($line -match '^160000 commit ([0-9a-f]{40})\s') {
            $gitlink = $Matches[1]
            $exists = Invoke-SnapshotGit -Arguments @("--git-dir=$DevJuceSharpGitDir", 'rev-parse', '-q', '--verify', "$gitlink^{commit}") -AllowExitCodes @(1, 128)
            if ($exists) { $histories += @{ GitDir = $DevJuceSharpGitDir; Ref = $gitlink } }
        }
    }
    return , $histories
}

# What -Push must not publish: a commit in $Commits whose author or committer email, or a
# tag in $Tags whose tagger email, is not $Email. One line per problem; none = clean.
function Find-SnapshotIdentityProblems {
    param(
        [Parameter(Mandatory)][string]$GitDir,
        [AllowEmptyCollection()][string[]]$Commits = @(),
        [AllowEmptyCollection()][string[]]$Tags = @(),
        [Parameter(Mandatory)][string]$Email
    )
    foreach ($c in $Commits) {
        $f = (Invoke-SnapshotGit -Arguments @("--git-dir=$GitDir", 'log', '-1', '--format=%ae%x1f%ce', $c)).Split([char]0x1f)
        if ($f[0] -cne $Email) { "commit $c has author email '$($f[0])', not '$Email'" }
        if ($f[1] -cne $Email) { "commit $c has committer email '$($f[1])', not '$Email'" }
    }
    foreach ($t in $Tags) {
        $tagger = Invoke-SnapshotGit -Arguments @("--git-dir=$GitDir", 'for-each-ref', '--format=%(taggeremail)', "refs/tags/$t")
        $tagger = $tagger.Trim().TrimStart('<').TrimEnd('>')
        if ($tagger -cne $Email) { "tag $t has tagger email '$tagger', not '$Email'" }
    }
}

# The refusal text: each missing commit, and the command that imports it.
function Format-PublicSyncRefusal {
    param([Parameter(Mandatory)][object[]]$Missing)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("public sync guard: $($Missing.Count) public commit(s) after the last snapshot are not in dev. The snapshot would overwrite them.")
    foreach ($c in $Missing) {
        $lines.Add("  $($c.Repo)@$($c.Sha)  $($c.Subject)  ($($c.Author))")
        $lines.Add("      import: pwsh -NoProfile -File tools/release/Import-PublicCommit.ps1 -Repo $($c.Repo) -Commit $($c.Sha)")
    }
    $lines.Add("Each import is a dev commit with the trailer '${script:TrailerKey}: <Repo>@<sha>'. To skip one on purpose, commit an empty dev commit with that trailer saying why.")
    return ($lines -join "`n")
}

# The watch's issue title. Deduplicated on the part before the colon, so a reworded
# public subject does not open a second issue.
function Get-PublicImportIssueTitle {
    param([Parameter(Mandatory)][object]$Commit)
    $subject = if ($Commit.Subject.Length -gt 80) { $Commit.Subject.Substring(0, 80) + '...' } else { $Commit.Subject }
    return "$(Get-PublicImportIssueKey $Commit): $subject"
}

function Get-PublicImportIssueKey {
    param([Parameter(Mandatory)][object]$Commit)
    return "Import public commit $($Commit.Repo)@$($Commit.Sha.Substring(0, 12))"
}

# --- the reverse rename map --------------------------------------------------------

# Public text -> dev text, line by line: a public line that is the renamed form of a line
# in the dev file ($DevText) becomes that dev line again. A line with no dev counterpart
# is left as it is. Lines keep their own "\r". Ambiguous lines (two dev lines renaming to
# the same public line) are left alone. Byte-exact through Latin-1, as New-PublicTree.
function Convert-PublicTextToDev {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$PublicText, [Parameter(Mandatory)][AllowEmptyString()][string]$DevText)
    $map = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $ambiguous = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($devLine in $DevText.Split("`n")) {
        $public = Convert-PublicText -Text $devLine
        if ($public -ceq $devLine) { continue }
        if ($map.ContainsKey($public) -and $map[$public] -cne $devLine) { [void]$ambiguous.Add($public) }
        $map[$public] = $devLine
    }
    if ($map.Count -eq 0) { return $PublicText }
    $lines = $PublicText.Split("`n")
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $key = $lines[$i]
        if ($map.ContainsKey($key) -and -not $ambiguous.Contains($key)) { $lines[$i] = $map[$key] }
    }
    return ($lines -join "`n")
}

Export-ModuleMember -Function Get-PublicSnapshotIdentity, Get-DevSyncHistories, Find-SnapshotIdentityProblems, Get-PublicCommitTrailerKey, Get-PublicCommitTrailer, Get-SyncCommitRecords, Get-SnapshotIdentities,
    Test-PublicSnapshotRecord, Get-PublicCommitsSinceSnapshot, Get-ImportedPublicCommits, Find-UnimportedPublicCommits,
    Format-PublicSyncRefusal, Get-PublicImportIssueTitle, Get-PublicImportIssueKey, Convert-PublicTextToDev
