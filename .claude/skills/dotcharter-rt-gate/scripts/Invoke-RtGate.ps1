<#
.SYNOPSIS
    Builds and runs the native real-time gate (juce_sharp_rtcheck) with the
    VS 2026 Build Tools' own CMake - neither cmake nor ctest is on PATH here.

.DESCRIPTION
    Full run (default): builds the whole native tree and runs every ctest in the
    real build tree (rt_alloc_gate plus the tone-regression and NAM-lifetime
    tests), which is what a change must pass.
    -Only <substring>: builds, then runs just the matching scenarios directly
    with the same baseline and lock-site files. The two meta-assertions
    (RtLockSiteSetIsUnchanged, RtBaselineGate) are SKIPPED in that mode, so a
    partial run is for iterating, never for sign-off.

.PARAMETER Only
    Scenario-name substring, e.g. "Metering". Exit 2 means nothing matched.
.PARAMETER NoBuild
    Skip the build (you just built).
#>
param([string]$Only, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent
$native = Join-Path $repo 'JuceSharp\src\JuceSharp.Native'
$build = Join-Path $native 'build'
if (-not (Test-Path (Join-Path $build 'CMakeCache.txt'))) {
    throw "No configured native build at $build - configure it first (JuceSharp/AGENTS.md, 'Visual Studio 18 2026' generator)."
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$bin = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$cmake = Join-Path $bin 'cmake.exe'
$ctest = Join-Path $bin 'ctest.exe'
foreach ($p in $cmake, $ctest) { if (-not (Test-Path $p)) { throw "Missing: $p" } }

# Stray test hosts hold the DLL and can hold the ASIO device exclusively.
Get-Process juce_sharp_rtcheck -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $NoBuild) {
    # A partial run needs only the gate. A full run builds everything, because
    # ctest also runs the tone-regression and NAM-lifetime executables, and
    # building just one target would leave those stale. The full build also
    # redeploys juce_sharp.dll (POST_BUILD), which a native change needs anyway.
    $target = if ($Only) { @('--target', 'juce_sharp_rtcheck') } else { @() }
    & $cmake --build $build --config Release @target -- /m /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." }
}

if ($Only) {
    $exe = Join-Path $build 'tests\Release\juce_sharp_rtcheck.exe'
    $rt = Join-Path $native 'tests\rt'
    & $exe --baseline (Join-Path $rt 'rt_baseline.txt') --lock-sites (Join-Path $rt 'rt_lock_sites.txt') --only $Only
    $code = $LASTEXITCODE
    Write-Host "rtcheck --only '$Only' exit $code (PARTIAL RUN - meta-assertions skipped)"
    exit $code
}

& $ctest --test-dir $build -C Release --output-on-failure --no-tests=error
$code = $LASTEXITCODE
Write-Host "native ctest exit $code"
exit $code
