#Requires -Version 7
<#
.SYNOPSIS
    Exports a dev commit to the public DotCharter and JuceSharp repos as ONE snapshot
    commit each, and runs the leak check on the result. A local dry run by default.

.DESCRIPTION
    1. JuceSharp: the tree of the dev gitlink commit (rename map applied) is committed
       onto the public JuceSharp line in a scratch repo.
    2. DotCharter: the tree of -DevRef with the excluded paths dropped
       (PublicSnapshot.psm1 Get-PublicExcludedPaths), the rename map applied to every
       text file, the JuceSharp gitlink set to the PUBLIC JuceSharp snapshot and a
       tools/setup/native-runtime.json pin for it: the -NativeDir pair's hashes, else the
       dev pin's when the public JuceSharp tree is byte-identical to the dev gitlink's,
       else PENDING.
       Each snapshot's one-line message (Get-SnapshotMessage) follows SomeeName/rs7string:
       "Initial public commit of <product>" for a root commit (no public parent), else
       "Update to v<version>". Both get an annotated tag v<version> with that message.
    0. The public -> dev sync guard (PublicSync.psm1): when a public URL is given, every
       public main commit after the last snapshot must be named by a
       'Public-Commit: <Repo>@<sha>' trailer in the history of -DevRef (for JuceSharp also
       of the dev gitlink). Otherwise the export REFUSES, dry run included, and names each
       missing commit with its Import-PublicCommit.ps1 command.
    2b. The push preflight (Get-PublicPushPlan), dry run included, whenever the URLs are
       given: from `git ls-remote` of BOTH public repos, public main must still be the
       snapshot's parent and the tag v<version> must be absent or already name the snapshot
       commit (the unchanged-JuceSharp case keeps its public commit and tag). A tag for
       another commit means the version was not bumped: refused.
    3. Both trees are checked out under the scratch root and leak-checked
       (Find-PublicTreeLeaks, gzip files decompressed in memory, files with a NUL read as
       Latin-1); gitleaks runs too when it is installed.
       The DotCharter work tree is KEPT between runs (-Clean drops it): it is updated like a
       checkout and its build outputs are parked outside it while both scans run, so they
       read the tracked tree only (Assert-SnapshotTracksEverything) and the build stays
       incremental.
    4. -Build builds the snapshot the public way (tools/agent/Invoke-Gate.ps1 -Build
       -Public in the scratch tree, the dev native pair copied in); -Gate also runs
       the default gate there, unless a green gate is on record (ReleaseCache.psm1) for
       this exact snapshot tree or the dev tree it was cut from: then it builds only and
       names the record. -ForceGate runs the gate anyway. A green run is recorded.
    5. Prints the scratch paths, the commits, the native publish command, the leak report
       and what -Push would refuse. Exit code 1 when the leak check has findings or the
       push preflight refuses.

    Each snapshot's parent is the tip of `main` at -PublicDotCharterUrl /
    -PublicJuceSharpUrl (fetched, never pushed to), or none (a root commit) when no URL
    is given. Author and committer are the dev checkout's git user.name/user.email (which
    -Push holds to the snapshot identity); the date is the dev commit's, so a re-run with
    the same inputs gives the same ids.

    Never touches the dev checkout's working tree, index, branches or refs: the scratch
    repos read the dev objects through objects/info/alternates.

    -Push pushes main + the version tag of each scratch repo with `git push --atomic`
    (JuceSharp first, since DotCharter's gitlink names it), and only after BOTH repos pass
    the push preflight, which is re-read right before the first push. It refuses:
      - without BOTH URLs, or without -Gate in the same run (the exact scratch tree being
        pushed built the public way a moment ago, and passed the default gate in this run
        or in a green run recorded for that tree or for the dev tree it was cut from);
      - on any leak finding; when gitleaks is not installed or reports findings;
      - when the native pin of the public JuceSharp commit is PENDING;
      - when a commit or tag it would push carries an author, committer or tagger email
        other than the snapshot identity (PublicSync.psm1 Get-PublicSnapshotIdentity,
        tools/release/public-snapshot-identity.json);
      - when the push preflight (2b) has a problem.
    The cheap refusals come before the gate runs. It never force-pushes, creates no
    repository, changes no visibility and publishes no release.

.EXAMPLE
    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1
    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -DevRef origin/main -NativeDir JuceSharp/src/JuceSharp/runtimes/win-x64/native -Build
    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -PublicDotCharterUrl https://github.com/SomeeName/DotCharter.git -PublicJuceSharpUrl https://github.com/SomeeName/JuceSharp.git -Gate -Push
#>
[CmdletBinding()]
param(
    # The dev DotCharter checkout (read-only). Defaults to the repo holding this script.
    [string]$DevRepo,
    # The dev commit to export.
    [string]$DevRef = 'origin/main',
    # The dev JuceSharp object store; default: the superproject's modules/JuceSharp, else JuceSharp/'s own git dir.
    [string]$DevJuceSharpGitDir,
    # Scratch root; recreated on every run, except the DotCharter work tree and its build
    # outputs, kept so the public build is incremental (-Clean drops them too).
    [string]$ScratchRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'dotcharter-public-export'),
    [switch]$Clean,
    [string]$PublicDotCharterUrl,
    [string]$PublicJuceSharpUrl,
    # Folder holding juce_sharp.dll + juce_vst_host.exe built from the exported JuceSharp tree: pins real hashes.
    [string]$NativeDir,
    [switch]$Build,
    [switch]$Gate,
    # Run the default gate even when a green one is on record for this tree or its dev tree.
    [switch]$ForceGate,
    [switch]$Push,
    # Findings printed per rule (0 = all).
    [int]$ReportPerRule = 0
)

$ErrorActionPreference = 'Stop'
if ($Push -and -not ($PublicDotCharterUrl -and $PublicJuceSharpUrl)) {
    throw '-Push needs BOTH -PublicDotCharterUrl and -PublicJuceSharpUrl: the push target is never guessed.'
}
if ($Push -and -not $Gate) {
    throw '-Push needs -Gate in the same run: what it pushes must build the public way (Invoke-Gate.ps1 -Public) in this scratch tree and have passed the default gate (in this run, or a green run on record for this exact tree or the dev tree it was cut from).'
}
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ReleaseCache.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'PublicSync.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../setup/NativeRuntime.psm1') -Force

# A simple function on purpose: $args passes `-C` and friends through as plain strings.
function Git { Invoke-SnapshotGit -Arguments ([string[]]$args) }

if (-not $DevRepo) { $DevRepo = Join-Path $PSScriptRoot '../..' }
$DevRepo = (Resolve-Path -LiteralPath $DevRepo).Path
$devGitCommon = Git -C $DevRepo rev-parse --path-format=absolute --git-common-dir
if (-not $DevJuceSharpGitDir) {
    $DevJuceSharpGitDir = Join-Path $devGitCommon 'modules/JuceSharp'
    if (-not (Test-Path -LiteralPath $DevJuceSharpGitDir)) {
        $DevJuceSharpGitDir = Git -C (Join-Path $DevRepo 'JuceSharp') rev-parse --path-format=absolute --git-common-dir
    }
}
$DevJuceSharpGitDir = (Resolve-Path -LiteralPath $DevJuceSharpGitDir).Path

# --- inputs -----------------------------------------------------------------------
$devCommit = Git -C $DevRepo rev-parse --verify "$DevRef^{commit}"
$devDate = Git -C $DevRepo show -s --format=%cI $devCommit
$name = Git -C $DevRepo config user.name
$email = Git -C $DevRepo config user.email
if (-not $name -or -not $email) { throw "git config user.name/user.email are not set in $DevRepo." }
$version = Get-MsBuildVersion (Git -C $DevRepo show "${devCommit}:DotCharter.UI/DotCharter.UI.csproj")
$gitlinkLine = Git -C $DevRepo ls-tree $devCommit JuceSharp
if ($gitlinkLine -notmatch '^160000 commit ([0-9a-f]{40})\s') { throw "$DevRef has no JuceSharp gitlink." }
$devJuce = $Matches[1]
$devJuceDate = Git "--git-dir=$DevJuceSharpGitDir" show -s --format=%cI $devJuce
$juceVersion = Get-MsBuildVersion (Git "--git-dir=$DevJuceSharpGitDir" show "${devJuce}:Directory.Build.props")
$abi = [int]([regex]::Match((Git "--git-dir=$DevJuceSharpGitDir" show "${devJuce}:src/JuceSharp.Native/include/dotcharter_audio.h"),
        '(?m)^\s*#define\s+DCA_ABI_VERSION\s+(\d+)').Groups[1].Value)

# --- scratch ------------------------------------------------------------------------
$ScratchRoot = [System.IO.Path]::GetFullPath($ScratchRoot)
$marker = Join-Path $ScratchRoot '.dotcharter-public-export'
$juceRepo = Join-Path $ScratchRoot 'JuceSharp.git'
$dcRepo = Join-Path $ScratchRoot 'DotCharter'
# Kept between runs (unless -Clean): the DotCharter work tree with its index, and build
# outputs a crashed run left parked. Everything else, the refs included, is new each run.
$buildCache = Join-Path $ScratchRoot 'build-cache'
$keep = @()
if (Test-Path -LiteralPath $ScratchRoot) {
    if (-not (Test-Path -LiteralPath $marker)) { throw "$ScratchRoot exists and is not an export scratch folder; refusing to delete it." }
    if (-not $Clean -and (Test-Path -LiteralPath (Join-Path $dcRepo '.git'))) { $keep = @('DotCharter', 'build-cache') }
    Get-ChildItem -LiteralPath $ScratchRoot -Force | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Path $ScratchRoot -Force | Out-Null
Set-Content -LiteralPath $marker -Value 'scratch of tools/release/Export-PublicSnapshot.ps1; recreated on the next run (the DotCharter work tree and its build outputs are kept; -Clean drops them)'
$keptTree = $keep.Count -gt 0
[void](New-SnapshotScratchRepo -Path $juceRepo -AlternateObjectDirs @(Join-Path $DevJuceSharpGitDir 'objects') -Bare)
$dcGitDir = New-SnapshotScratchRepo -Path $dcRepo -AlternateObjectDirs @(Join-Path $devGitCommon 'objects')
if ($keptTree) { Reset-SnapshotScratchRefs -Repo $dcRepo }
# JuceSharp's index for its checkout inside the DotCharter work tree, kept with it.
$juceIndex = Join-Path $dcGitDir 'dotcharter-juce-snapshot.index'
if (-not $keptTree -and (Test-Path -LiteralPath $juceIndex)) { Remove-Item -LiteralPath $juceIndex -Force }

# --- 0. the public -> dev sync guard ---------------------------------------------------------
# Every public commit after the last snapshot must be in dev (a Public-Commit trailer in the
# history of -DevRef; for JuceSharp also of its gitlink), or this snapshot would overwrite it.
# A hard stop, dry run included (PublicSync.psm1).
$juceParent = Get-PreviousSnapshot -Repo $juceRepo -Url $PublicJuceSharpUrl
$dcParent = Get-PreviousSnapshot -Repo $dcRepo -Url $PublicDotCharterUrl
# Snapshots are recognised by the ONE snapshot identity (Get-SnapshotIdentities), as in the
# watch and the import; the dev histories come from Get-DevSyncHistories.
$missing = @()
if ($juceParent) {
    $missing += @(Find-UnimportedPublicCommits -PublicGitDir $juceRepo -PublicRef refs/remotes/public/main -Repo JuceSharp `
            -DevHistories (Get-DevSyncHistories -DevGitDir $devGitCommon -DevCommit $devCommit -Repo JuceSharp -DevJuceSharpGitDir $DevJuceSharpGitDir))
}
if ($dcParent) {
    $missing += @(Find-UnimportedPublicCommits -PublicGitDir $dcGitDir -PublicRef refs/remotes/public/main -Repo DotCharter `
            -DevHistories (Get-DevSyncHistories -DevGitDir $devGitCommon -DevCommit $devCommit -Repo DotCharter))
}
if ($missing.Count -gt 0) {
    Write-Host (Format-PublicSyncRefusal -Missing $missing)
    throw "Export refused: $($missing.Count) public commit(s) are not imported into $DevRef (see above)."
}
$guardLine = if ($juceParent -or $dcParent) { 'public sync guard: every public commit since the last snapshot is in dev' } else { 'public sync guard: not run (no public URL, so no previous snapshot)' }

# --- 1. JuceSharp -----------------------------------------------------------------------
$juceTree = New-PublicTree -Repo $juceRepo -SourceTree "$devJuce^{tree}"
$juce = New-SnapshotCommit -Repo $juceRepo -Tree $juceTree -Parent $juceParent `
    -Message (Get-SnapshotMessage -Product 'JuceSharp' -Version $juceVersion -Parent $juceParent) `
    -Name $name -Email $email -Date $devJuceDate -Tag "v$juceVersion"

# --- 2. DotCharter -----------------------------------------------------------------------
$pins = $null
$pinSource = $null
if ($NativeDir) {
    $NativeDir = (Resolve-Path -LiteralPath $NativeDir).Path
    $pins = [ordered]@{}
    foreach ($file in Get-NativeRuntimePairFiles) { $pins[$file] = Get-FileSha256 (Join-Path $NativeDir $file) }
    $dllAbi = Read-DllAbiVersion (Join-Path $NativeDir 'juce_sharp.dll')
    if ($dllAbi -ne $abi) { throw "$NativeDir holds an ABI $dllAbi DLL; the exported JuceSharp header says $abi." }
    $pinSource = 'the -NativeDir pair'
}
else {
    # The public JuceSharp tree is the dev gitlink's whenever the rename map changed nothing:
    # then the dev pin's pair IS this tree's pair.
    $devJuceTree = Git "--git-dir=$DevJuceSharpGitDir" rev-parse "$devJuce^{tree}"
    $devManifest = Git -C $DevRepo show "${devCommit}:tools/setup/native-runtime.json"
    $pins = Get-InheritedNativePins -Json $devManifest -DevCommit $devJuce -DevTree $devJuceTree -PublicTree $juceTree
    $pinSource = if ($pins) { "the dev pin of $devJuce (byte-identical tree $juceTree)" } else { $null }
}
$edit = {
    param($indexEnvironment)
    Invoke-SnapshotGit -Arguments @('-C', $dcRepo, 'update-index', '--cacheinfo', "160000,$($juce.Commit),JuceSharp") -Environment $indexEnvironment | Out-Null
    $manifestPath = 'tools/setup/native-runtime.json'
    $json = Get-SnapshotIndexText -Repo $dcRepo -IndexEnvironment $indexEnvironment -Path $manifestPath
    if ($null -ne $json) {
        $updated = Add-PublicNativeRuntimeEntry -Json $json -Commit $juce.Commit -Abi $abi -Files $pins
        Set-SnapshotIndexText -Repo $dcRepo -IndexEnvironment $indexEnvironment -Path $manifestPath -Text $updated
    }
}
$dcTree = New-PublicTree -Repo $dcRepo -SourceTree "$devCommit^{tree}" -Edit $edit
$dc = New-SnapshotCommit -Repo $dcRepo -Tree $dcTree -Parent $dcParent `
    -Message (Get-SnapshotMessage -Product 'DotCharter' -Version $version -Parent $dcParent) `
    -Name $name -Email $email -Date $devDate -Tag "v$version"
# Where this snapshot came from, for New-Release.ps1 run from the scratch tree: the owner's
# DotCharter-local beside the DEV checkout, and the dev tree's recorded gate runs.
$devTree = Git -C $DevRepo rev-parse "$devCommit^{tree}"
$devCheckout = if ((Split-Path $devGitCommon -Leaf) -eq '.git') { Split-Path $devGitCommon -Parent } else { $DevRepo }
[void](Write-ExportOrigin -RepoRoot $dcRepo -DevCheckout $devCheckout -DevCommit $devCommit -DevTree $devTree -PublicTree $dcTree)

# --- 2b. push preflight: both public repos, before anything is pushed -----------------------
# A -Push refusal: the reasons as plain lines (an error record wraps and prefixes them), then the stop.
function Stop-Push([string]$What, [string[]]$Reasons) {
    Write-Host "-Push $What, nothing pushed:"
    foreach ($reason in $Reasons) { Write-Host "  - $reason" }
    throw "-Push $What, nothing pushed (reasons above)."
}
$snapshots = @(
    [pscustomobject]@{ Label = 'JuceSharp'; Repo = $juceRepo; Url = $PublicJuceSharpUrl; Result = $juce; Parent = $juceParent }
    [pscustomobject]@{ Label = 'DotCharter'; Repo = $dcRepo; Url = $PublicDotCharterUrl; Result = $dc; Parent = $dcParent }
)
function Get-PushPlans {
    foreach ($s in $snapshots) {
        if (-not $s.Url) { continue }
        $plan = Get-PublicPushPlan -Url $s.Url -Commit $s.Result.Commit -Parent $s.Parent -Tag $s.Result.Tag
        $plan | Add-Member -NotePropertyName Label -NotePropertyValue $s.Label
        $plan | Add-Member -NotePropertyName Repo -NotePropertyValue $s.Repo
        $plan
    }
}
$plans = @(Get-PushPlans)
$preflightProblems = @(foreach ($p in $plans) { foreach ($problem in $p.Problems) { "$($p.Label): $problem" } })

# What -Push would publish must carry the snapshot identity, and nothing else.
$snapshotIdentity = Get-PublicSnapshotIdentity
$identityProblems = @(foreach ($s in $snapshots) {
        $range = @('rev-list', 'refs/heads/main')
        if ($s.Parent) { $range += @('--not', $s.Parent) }
        $commits = @((Git -C $s.Repo @range).Split("`n", [StringSplitOptions]::RemoveEmptyEntries))
        $plan = @($plans | Where-Object Label -EQ $s.Label)
        $pushesTag = if ($plan) { $plan[0].Refspecs -contains "refs/tags/$($s.Result.Tag):refs/tags/$($s.Result.Tag)" } else { $s.Result.Created }
        $tags = if ($pushesTag) { @($s.Result.Tag) } else { @() }
        $gitDir = Git -C $s.Repo rev-parse --path-format=absolute --git-dir
        foreach ($problem in Find-SnapshotIdentityProblems -GitDir $gitDir -Commits $commits -Tags $tags -Email $snapshotIdentity.Email) { "$($s.Label): $problem" }
    })

# --- 3. leak check ------------------------------------------------------------------------
# The kept work tree is updated like a checkout (unchanged files keep their timestamps, so
# the build below is incremental), with its build outputs PARKED in $buildCache: the leak
# check and gitleaks read the file system, and must read the tracked tree only.
$workTrees = @(
    @{ Prefix = ''; Git = @('-C', $dcRepo); Environment = @{} }
    @{ Prefix = 'JuceSharp/'; Git = @("--git-dir=$juceRepo", "--work-tree=$(Join-Path $dcRepo 'JuceSharp')"); Environment = @{ GIT_INDEX_FILE = $juceIndex } }
)
[void](Move-SnapshotBuildOutputs -Root $dcRepo -Cache $buildCache -Trees $workTrees)
# A refresh first: a file whose timestamp moved but whose bytes did not is then left alone
# by the checkout below, instead of rewritten (which would recompile its project).
foreach ($view in $workTrees) {
    if (Test-Path -LiteralPath (Join-Path $dcRepo $view.Prefix)) {
        Invoke-SnapshotGit -Arguments ($view.Git + @('update-index', '-q', '--refresh')) -Environment $view.Environment -AllowExitCodes 1 | Out-Null
    }
}
Git -C $dcRepo -c core.longpaths=true reset -q --hard main | Out-Null
Git -C $dcRepo -c core.longpaths=true clean -q -ffd | Out-Null
Export-SnapshotWorkTree -GitDir $juceRepo -Commit $juce.Commit -Destination (Join-Path $dcRepo 'JuceSharp') -IndexPath $juceIndex
Invoke-SnapshotGit -Arguments ($workTrees[1].Git + @('-c', 'core.longpaths=true', 'clean', '-q', '-ffd')) -Environment $workTrees[1].Environment | Out-Null
Assert-SnapshotTracksEverything -Trees $workTrees
$findings = @(Find-PublicTreeLeaks -Root $dcRepo)
$report = Format-PublicLeakReport -Findings $findings -PerRule $ReportPerRule
$reportPath = Join-Path $ScratchRoot 'leak-report.txt'
Set-Content -LiteralPath $reportPath -Value (Format-PublicLeakReport -Findings $findings) -Encoding utf8NoBOM

$gitleaks = Get-Command gitleaks -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
$gitleaksLine = 'gitleaks: not installed - key material NOT checked (install gitleaks, then re-run)'
$gitleaksExit = $null
if ($gitleaks) {
    # Scanned as '.' from inside the tree: the config's path allowlists are anchored
    # repo-relative ('^DotCharter\.Tests/...') and never match an absolute path.
    Push-Location -LiteralPath $dcRepo
    try {
        & $gitleaks.Source dir . --config .gitleaks.toml --no-banner --redact 2>&1 |
            Set-Content -LiteralPath (Join-Path $ScratchRoot 'gitleaks.txt') -Encoding utf8NoBOM
        $gitleaksExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $gitleaksLine = if ($gitleaksExit -eq 0) { 'gitleaks: clean' } else { "gitleaks: FINDINGS (exit $gitleaksExit), see $(Join-Path $ScratchRoot 'gitleaks.txt')" }
}
# Both scans are done: the previous run's build outputs go back for the incremental build.
$restored = @(Restore-SnapshotBuildOutputs -Root $dcRepo -Cache $buildCache)
$keptLine = if ($keptTree) { "scratch: work tree kept from the last run, $($restored.Count) build output path(s) parked during the scans and restored" } else { 'scratch: fresh work tree (a first run, or -Clean)' }

# Everything -Push refuses on, short of the gate. Checked BEFORE the gate runs.
$pushBlockers = [System.Collections.Generic.List[string]]::new()
if ($findings.Count -gt 0) { $pushBlockers.Add("the leak check has $($findings.Count) finding(s)") }
if (-not $gitleaks) { $pushBlockers.Add('gitleaks is not installed, so key material is unchecked') }
elseif ($gitleaksExit -ne 0) { $pushBlockers.Add("gitleaks reported findings (exit $gitleaksExit), see $(Join-Path $ScratchRoot 'gitleaks.txt')") }
if (-not $pins) { $pushBlockers.Add('the native pin of the public JuceSharp commit is PENDING: re-export with -NativeDir <the pair built from that tree>') }
foreach ($problem in $identityProblems) { $pushBlockers.Add("identity: $problem (the snapshot identity is $($snapshotIdentity.Identity); set git user.email to it)") }
foreach ($problem in $preflightProblems) { $pushBlockers.Add("preflight: $problem") }
if ($Push -and $pushBlockers.Count -gt 0) {
    Stop-Push 'refused' $pushBlockers
}

# --- 4. build ------------------------------------------------------------------------------
$buildLine = 'build: not run (-Build builds the snapshot the public way)'
if ($Build -or $Gate) {
    $nativeSource = if ($NativeDir) { $NativeDir } else { Join-Path $DevRepo 'JuceSharp/src/JuceSharp/runtimes/win-x64/native' }
    $nativeTarget = Join-Path $dcRepo 'JuceSharp/src/JuceSharp/runtimes/win-x64/native'
    New-Item -ItemType Directory -Path $nativeTarget -Force | Out-Null
    foreach ($file in Get-NativeRuntimePairFiles) {
        $source = Join-Path $nativeSource $file
        if (-not (Test-Path -LiteralPath $source)) { throw "$source is missing: -Build needs the native pair built from this JuceSharp tree." }
        Copy-Item -LiteralPath $source -Destination $nativeTarget -Force
    }
    # One test run per tree: a green default gate on record for this exact snapshot tree, or
    # for the dev tree it was cut from (the merge train records those), is reused. The
    # public variant is still BUILT here; only the identical test run is skipped.
    $recordedGate = $null
    if ($Gate -and -not $ForceGate) { $recordedGate = Find-GateRecord -Trees @($dcTree, $devTree) -Kind gate }
    $gateArgs = @('-NoProfile', '-File', (Join-Path $dcRepo 'tools/agent/Invoke-Gate.ps1'), '-Public')
    $gateArgs += if ($Gate -and -not $recordedGate) { '-Gate' } else { '-Build' }
    $previousSkip = $env:DotCharterSkipNativeFetch
    $env:DotCharterSkipNativeFetch = 'true'
    try { & pwsh @gateArgs; $buildExit = $LASTEXITCODE }
    finally { $env:DotCharterSkipNativeFetch = $previousSkip }
    $buildLine = "build: Invoke-Gate.ps1 $($gateArgs[-2..-1] -join ' ') exited $buildExit"
    if ($recordedGate) { $buildLine += "; the gate was NOT re-run: reusing the green gate recorded for $(Format-GateRecord $recordedGate) (-ForceGate re-runs it)" }
    if ($Push -and $buildExit -ne 0) { Stop-Push 'refused' @("$buildLine (the snapshot must build and pass the gate)") }
    if ($Gate -and -not $recordedGate -and $buildExit -eq 0) {
        [void](Save-GateRecord -Tree $dcTree -Commit $dc.Commit -Gate -Source 'Export-PublicSnapshot.ps1 -Gate (the public variant)')
        $buildLine += "; recorded for tree $dcTree"
    }
}

# --- 5. report ------------------------------------------------------------------------------
function Describe($label, $result, $repo) {
    $state = if ($result.Created) { 'new' } else { 'unchanged tree, reused' }
    $parent = if ($result.Parent) { $result.Parent } else { '(none: root commit)' }
    "$label $($result.Commit) [$state] tag $($result.Tag), parent $parent`n    scratch repo: $repo"
}
Write-Host ''
Write-Host "dev $DevRef = $devCommit (DotCharter v$version); dev JuceSharp gitlink $devJuce (v$juceVersion, ABI $abi)"
Write-Host "author/committer: $name <$email>"
Write-Host $guardLine
Write-Host (Describe 'JuceSharp ' $juce $juceRepo)
Write-Host (Describe 'DotCharter' $dc $dcRepo)
Write-Host "    work tree (JuceSharp checked out inside): $dcRepo"
Write-Host ''
if ($pins) { Write-Host "native-runtime.json: pinned the public JuceSharp commit with $pinSource." }
else {
    Write-Host 'native-runtime.json: the public JuceSharp commit is pinned as PENDING (its tree differs from the dev gitlink''s, or dev has no pin). Pin the pair, then re-export with it:'
    Write-Host "    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -DevRef $DevRef -NativeDir <folder with the pair built from JuceSharp $devJuce>"
}
$asset = Get-NativeRuntimeAssetName $juce.Commit
Write-Host "native-prebuilt publish (NOT run; once the public JuceSharp repo exists, from the exported tree):"
Write-Host "    cd `"$dcRepo`"; pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -NativeDir <pair folder> -Upload    # uploads $asset"
Write-Host ''
Write-Host $report
Write-Host "full report: $reportPath"
Write-Host $gitleaksLine
Write-Host $keptLine
Write-Host $buildLine
# The release from this exact tree. New-Release.ps1 finds the signing key in the dev
# checkout's DotCharter-local through export-origin.json; printed anyway, with the tonelab
# folder, so the command can be pasted as it stands.
$signingKey = if ($env:DOTCHARTER_RELEASE_SIGNING_KEY) { $env:DOTCHARTER_RELEASE_SIGNING_KEY } else { Join-Path (Get-ReleaseLocalDir -RepoRoot $dcRepo) 'release-signing/dotcharter-release-ed25519.key' }
$tonelab = if ($env:DOTCHARTER_TONELAB_DIR) { $env:DOTCHARTER_TONELAB_DIR } else { '<tonelab build folder>' }
Write-Host 'release from this snapshot (reuses a green headless run recorded for the dev tree it was cut from):'
Write-Host "    pwsh -NoProfile -File `"$(Join-Path $dcRepo 'tools/release/New-Release.ps1')`" -SigningKeyPath `"$signingKey`" -TonelabDir `"$tonelab`""
if ($preflightProblems.Count -gt 0) {
    Write-Host 'export refused by the push preflight:'
    foreach ($problem in $preflightProblems) { Write-Host "    $problem" }
}
elseif ($plans.Count -gt 0) { Write-Host 'push preflight: both public repos accept this snapshot (main is its parent, no tag in the way)' }
else { Write-Host 'push preflight: not run (no public URL)' }

if ($Push) {
    # Re-read both repos right before the first push: the gate took minutes.
    $plans = @(Get-PushPlans)
    $late = @(foreach ($p in $plans) { foreach ($problem in $p.Problems) { "$($p.Label): $problem" } })
    if ($late.Count -gt 0) { Stop-Push 'refused: the public repos moved during the run' $late }
    foreach ($p in $plans) {
        if ($p.Refspecs.Count -eq 0) { Write-Host "$($p.Label): $($p.Commit) and tag $($p.Tag) are already public; nothing to push."; continue }
        $refspecs = [string[]]$p.Refspecs
        Git -C $p.Repo push --atomic $p.Url @refspecs | Out-Null
        Write-Host "pushed $($p.Label) ($($p.Refspecs -join ', ')) -> $($p.Commit)"
    }
}
else {
    Write-Host 'would push (dry run; -Push -Gate with both URLs to do it):'
    Write-Host "    JuceSharp  main -> $($juce.Commit), tag $($juce.Tag)"
    Write-Host "    DotCharter main -> $($dc.Commit), tag $($dc.Tag)"
    if ($pushBlockers.Count -gt 0) {
        Write-Host '-Push would refuse:'
        foreach ($blocker in $pushBlockers) { Write-Host "    $blocker" }
    }
}
exit $(if ($findings.Count -gt 0 -or $preflightProblems.Count -gt 0) { 1 } else { 0 })
