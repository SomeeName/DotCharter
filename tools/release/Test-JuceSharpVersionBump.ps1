<#
.SYNOPSIS
    Fails while the JuceSharp at the dev gitlink differs from the last public JuceSharp
    snapshot but its <Version> (Directory.Build.props) does not: New-Release.ps1's preflight,
    so the version is bumped BEFORE the native pair and the packs are built from it.
.DESCRIPTION
    Public data only: the public JuceSharp `main` is fetched into a temporary bare repository
    whose objects borrow the dev JuceSharp store, the dev tree is rewritten for the public repo
    exactly as the export does (New-PublicTree), and the two trees are compared. No public
    JuceSharp yet: nothing to compare, exit 0. Exit 1 while a bump is missing.
.EXAMPLE
    pwsh -NoProfile -File tools/release/Test-JuceSharpVersionBump.ps1 -DevRef HEAD
#>
[CmdletBinding()]
param(
    [string]$DevRepo,
    [string]$DevRef = 'HEAD',
    [string]$PublicJuceSharpUrl = 'https://github.com/SomeeName/JuceSharp.git',
    # The dev JuceSharp object store; default: the superproject's modules/JuceSharp.
    [string]$DevJuceSharpGitDir
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
if (-not $DevRepo) { $DevRepo = Join-Path $PSScriptRoot '../..' }
$devGitDir = Invoke-SnapshotGit -Arguments @('-C', (Resolve-Path -LiteralPath $DevRepo).Path, 'rev-parse', '--path-format=absolute', '--git-common-dir')
$devCommit = Invoke-SnapshotGit -Arguments @("--git-dir=$devGitDir", 'rev-parse', '--verify', "$DevRef^{commit}")
if (-not $DevJuceSharpGitDir) { $DevJuceSharpGitDir = Join-Path $devGitDir 'modules/JuceSharp' }
$devJuce = Invoke-SnapshotGit -Arguments @("--git-dir=$devGitDir", 'rev-parse', "${devCommit}:JuceSharp")
$version = Get-MsBuildVersion (Invoke-SnapshotGit -Arguments @("--git-dir=$DevJuceSharpGitDir", 'show', "${devJuce}:Directory.Build.props"))

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('dc-juce-version-' + [guid]::NewGuid().ToString('N'))
try {
    $repo = Join-Path $scratch 'JuceSharp.git'
    [void](New-SnapshotScratchRepo -Path $repo -AlternateObjectDirs @(Join-Path $DevJuceSharpGitDir 'objects') -Bare)
    $parent = Get-PreviousSnapshot -Repo $repo -Url $PublicJuceSharpUrl
    if (-not $parent) {
        Write-Host "JuceSharp ${version}: no public JuceSharp yet, nothing to compare"
        exit 0
    }
    $tree = New-PublicTree -Repo $repo -SourceTree "$devJuce^{tree}"
    $problem = Get-JuceSharpVersionProblem -Repo $repo -Tree $tree -Parent $parent -Version $version
    if ($problem) {
        Write-Host $problem
        exit 1
    }
    Write-Host "JuceSharp ${version}: the version moves with the tree (unchanged since the public snapshot, or bumped)"
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
