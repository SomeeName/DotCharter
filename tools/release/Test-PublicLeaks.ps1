#Requires -Version 7
<#
.SYNOPSIS
    The public export's leak check on any folder: prints file:line per finding,
    grouped by rule, and exits 1 when there is one.

.DESCRIPTION
    Rules (PublicSnapshot.psm1 Get-PublicLeakRules): the dev repo names, the archive repo
    names, the private add-on's name, add-on folder paths, Windows user-profile paths, and
    every pattern of DotCharter.Tests/Infrastructure/PersonalDataLintTests (which includes
    the owner's game-library drive). The only exemption is the made-up user-folder names
    of Get-UserPathPlaceholders; every other finding is fixed in the source.
    Export-PublicSnapshot.ps1 runs the same check on the snapshot it builds.

.EXAMPLE
    pwsh -NoProfile -File tools/release/Test-PublicLeaks.ps1 -Path $env:TEMP/dotcharter-public-export/DotCharter
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [int]$PerRule = 0
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PublicSnapshot.psm1') -Force
$findings = @(Find-PublicTreeLeaks -Root $Path)
Write-Host (Format-PublicLeakReport -Findings $findings -PerRule $PerRule)
exit $(if ($findings.Count -gt 0) { 1 } else { 0 })
