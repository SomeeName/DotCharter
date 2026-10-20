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
    3. Both trees are checked out under the scratch root and leak-checked
       (Find-PublicTreeLeaks); gitleaks runs too when it is installed.
    4. -Build builds the snapshot the public way (tools/agent/Invoke-Gate.ps1 -Build
       -Public in the scratch tree, the dev native pair copied in); -Gate also runs
       the default gate there.
    5. Prints the scratch paths, the commits, the native publish command and the leak
       report. Exit code 1 when the leak check has findings.

    Each snapshot's parent is the tip of `main` at -PublicDotCharterUrl /
    -PublicJuceSharpUrl (fetched, never pushed to), or none (a root commit) when no URL
    is given. Author and committer are the dev checkout's git user.name/user.email; the
    date is the dev commit's, so a re-run with the same inputs gives the same ids.

    Never touches the dev checkout's working tree, index, branches or refs: the scratch
    repos read the dev objects through objects/info/alternates.

    -Push pushes main + the version tag of both scratch repos (JuceSharp first). It
    refuses without BOTH URLs, refuses on any leak finding, and never force-pushes.
    It creates no repository, changes no visibility and publishes no release.

.EXAMPLE
    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1
    pwsh -NoProfile -File tools/release/Export-PublicSnapshot.ps1 -DevRef origin/main -NativeDir JuceSharp/src/JuceSharp/runtimes/win-x64/native -Build
#>
[CmdletBinding()]
param(
    # The dev DotCharter checkout (read-only). Defaults to the repo holding this script.
    [string]$DevRepo,
    # The dev commit to export.
    [string]$DevRef = 'origin/main',
    # The dev JuceSharp object store; default: the superproject's modules/JuceSharp, else JuceSharp/'s own git dir.
    [string]$DevJuceSharpGitDir,
    # Scratch root; wiped and recreated on every run.
    [string]$ScratchRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'dotcharter-public-export'),
    [string]$PublicDotCharterUrl,
    [string]$PublicJuceSharpUrl,
    # Folder holding juce_sharp.dll + juce_vst_host.exe built from the exported JuceSharp tree: pins real hashes.
    [string]$NativeDir,
    [switch]$Build,
    [switch]$Gate,
    [switch]$Push,
    # Findings printed per rule (0 = all).
    [int]$ReportPerRule = 0
)

$ErrorActionPreference = 'Stop'
if ($Push -and -not ($PublicDotCharterUrl -and $PublicJuceSharpUrl)) {
    throw '-Push needs BOTH -PublicDotCharterUrl and -PublicJuceSharpUrl: the push target is never guessed.'
}
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
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
if (Test-Path -LiteralPath $ScratchRoot) {
    if (-not (Test-Path -LiteralPath $marker)) { throw "$ScratchRoot exists and is not an export scratch folder; refusing to delete it." }
    Remove-Item -LiteralPath $ScratchRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $ScratchRoot -Force | Out-Null
Set-Content -LiteralPath $marker -Value 'scratch of tools/release/Export-PublicSnapshot.ps1; deleted on the next run'
$juceRepo = Join-Path $ScratchRoot 'JuceSharp.git'
$dcRepo = Join-Path $ScratchRoot 'DotCharter'
[void](New-SnapshotScratchRepo -Path $juceRepo -AlternateObjectDirs @(Join-Path $DevJuceSharpGitDir 'objects') -Bare)
[void](New-SnapshotScratchRepo -Path $dcRepo -AlternateObjectDirs @(Join-Path $devGitCommon 'objects'))

# --- 1. JuceSharp -----------------------------------------------------------------------
$juceTree = New-PublicTree -Repo $juceRepo -SourceTree "$devJuce^{tree}"
$juceParent = Get-PreviousSnapshot -Repo $juceRepo -Url $PublicJuceSharpUrl
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
$dcParent = Get-PreviousSnapshot -Repo $dcRepo -Url $PublicDotCharterUrl
$dc = New-SnapshotCommit -Repo $dcRepo -Tree $dcTree -Parent $dcParent `
    -Message (Get-SnapshotMessage -Product 'DotCharter' -Version $version -Parent $dcParent) `
    -Name $name -Email $email -Date $devDate -Tag "v$version"

# --- 3. leak check ------------------------------------------------------------------------
Git -C $dcRepo -c core.longpaths=true reset -q --hard main | Out-Null
Export-SnapshotWorkTree -GitDir $juceRepo -Commit $juce.Commit -Destination (Join-Path $dcRepo 'JuceSharp')
$findings = @(Find-PublicTreeLeaks -Root $dcRepo)
$report = Format-PublicLeakReport -Findings $findings -PerRule $ReportPerRule
$reportPath = Join-Path $ScratchRoot 'leak-report.txt'
Set-Content -LiteralPath $reportPath -Value (Format-PublicLeakReport -Findings $findings) -Encoding utf8NoBOM

$gitleaks = Get-Command gitleaks -ErrorAction SilentlyContinue
$gitleaksLine = 'gitleaks: not installed - key material NOT checked (install gitleaks, then re-run)'
if ($gitleaks) {
    # Scanned as '.' from inside the tree: the config's path allowlists are anchored
    # repo-relative ('^DotCharter\.Tests/...') and never match an absolute path.
    Push-Location -LiteralPath $dcRepo
    try {
        & $gitleaks.Source dir . --config .gitleaks.toml --no-banner --redact 2>&1 |
            Set-Content -LiteralPath (Join-Path $ScratchRoot 'gitleaks.txt') -Encoding utf8NoBOM
    }
    finally { Pop-Location }
    $gitleaksLine = if ($LASTEXITCODE -eq 0) { 'gitleaks: clean' } else { "gitleaks: FINDINGS (exit $LASTEXITCODE), see $(Join-Path $ScratchRoot 'gitleaks.txt')" }
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
    $gateArgs = @('-NoProfile', '-File', (Join-Path $dcRepo 'tools/agent/Invoke-Gate.ps1'), '-Public')
    $gateArgs += if ($Gate) { '-Gate' } else { '-Build' }
    $previousSkip = $env:DotCharterSkipNativeFetch
    $env:DotCharterSkipNativeFetch = 'true'
    try { & pwsh @gateArgs; $buildExit = $LASTEXITCODE }
    finally { $env:DotCharterSkipNativeFetch = $previousSkip }
    $buildLine = "build: Invoke-Gate.ps1 $($gateArgs[-2..-1] -join ' ') exited $buildExit"
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
Write-Host $buildLine

if ($Push) {
    if ($findings.Count -gt 0) { throw "-Push refused: the leak check has $($findings.Count) finding(s)." }
    Git -C $juceRepo push $PublicJuceSharpUrl "refs/heads/main:refs/heads/main" "refs/tags/$($juce.Tag)" | Out-Null
    Git -C $dcRepo push $PublicDotCharterUrl "refs/heads/main:refs/heads/main" "refs/tags/$($dc.Tag)" | Out-Null
    Write-Host "pushed JuceSharp $($juce.Commit) and DotCharter $($dc.Commit)."
}
else {
    Write-Host 'would push (dry run; -Push with both URLs to do it):'
    Write-Host "    JuceSharp  main -> $($juce.Commit), tag $($juce.Tag)"
    Write-Host "    DotCharter main -> $($dc.Commit), tag $($dc.Tag)"
}
exit $(if ($findings.Count -gt 0) { 1 } else { 0 })
