<#
.SYNOPSIS
    Fails when the .NET SDK / runtime on PATH is older than the floor the release
    is built with (#278).

.DESCRIPTION
    There is no sdk pin in global.json, so `actions/setup-dotnet` with
    `dotnet-version: '10.0.x'` installs whatever the newest 10.0 SDK is on the
    day of the run. That is the right default (a self-contained publish carries
    the newest runtime pack), but a hosted image that resolved an older one would
    ship an older runtime silently. The workflow calls this right after
    setup-dotnet so the floor is a failing step, not a hope. The floor is the
    SDK/runtime pair the v0.71.0 release was built with.
#>
[CmdletBinding()]
param(
    [version]$MinimumSdk = '10.0.402',
    [version]$MinimumRuntime = '10.0.13'
)

$ErrorActionPreference = 'Stop'
$sdk = [version](((& dotnet --version) | Select-Object -First 1) -replace '-.*$', '')
$runtimes = @(& dotnet --list-runtimes | ForEach-Object {
        if ($_ -match '^Microsoft\.NETCore\.App (\d+\.\d+\.\d+)(?:-\S+)? ') { [version]$Matches[1] }
    })
$newestRuntime = $runtimes | Sort-Object -Descending | Select-Object -First 1
Write-Host "dotnet SDK $sdk (floor $MinimumSdk), newest Microsoft.NETCore.App $newestRuntime (floor $MinimumRuntime)"
if ($sdk -lt $MinimumSdk) { throw ".NET SDK $sdk is older than the floor $MinimumSdk." }
if (-not $newestRuntime -or $newestRuntime -lt $MinimumRuntime) { throw "Newest Microsoft.NETCore.App runtime is $newestRuntime, older than the floor $MinimumRuntime." }
