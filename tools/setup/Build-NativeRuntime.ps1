<#
.SYNOPSIS
    Builds and deploys the native pair, then records its source and binary identity.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$CMakeCommand,
    [string]$Generator = 'Visual Studio 18 2026',
    [string]$BuildDir,
    [string]$ProvenancePath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeRuntime.psm1') -Force
if (-not $CMakeCommand) {
    $installed = Get-Command cmake -ErrorAction SilentlyContinue
    if ($installed) { $CMakeCommand = $installed.Source }
    else { $CMakeCommand = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/18/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' }
    if (-not (Test-Path -LiteralPath $CMakeCommand)) { throw 'CMake was not found; pass -CMakeCommand with the installed CMake executable.' }
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$juce = Join-Path $RepoRoot 'JuceSharp'
$source = Join-Path $juce 'src/JuceSharp.Native'
if (-not $BuildDir) { $BuildDir = Join-Path $source 'build' }
if (-not $ProvenancePath) { $ProvenancePath = Get-NativeBuildProvenancePath $RepoRoot }
$before = Get-NativeSourceTree $juce HEAD
$dirty = & git -C $juce status --porcelain -- src/JuceSharp.Native
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Native sources must be committed before building a publishable pair.' }
# Every tracked input of the pair is under src/JuceSharp.Native: its CMakeLists.txt is the only
# CMake file, it holds the dependency pins (JUCE_COMMIT, RUBBERBAND_COMMIT, ...), and every
# relative include stays inside the folder (NativeBuildProvenanceTests). What can still change
# the pair is UNTRACKED: the pins and FETCHCONTENT_SOURCE_DIR_* / JUCE_DIR are cache entries a
# reused build folder keeps, and the compiler reads flags and a toolchain from the environment.
# So the configure starts from a fresh cache, and those variables refuse the build.
$injected = @('CFLAGS', 'CXXFLAGS', 'LDFLAGS', 'CMAKE_TOOLCHAIN_FILE') | Where-Object { [Environment]::GetEnvironmentVariable($_) }
if ($injected) { throw "Unset $($injected -join ', ') before building a publishable pair: the recorded source tree would not describe what they change." }
# A failed build must leave no record that can bless an older deployed pair.
if (Test-Path -LiteralPath $ProvenancePath) { Remove-Item -LiteralPath $ProvenancePath -Force }
& $CMakeCommand --fresh -S $source -B $BuildDir -G $Generator -A x64
if ($LASTEXITCODE -ne 0) { throw "Native configure failed (exit $LASTEXITCODE)." }
# Deployment is POST_BUILD: an up-to-date target could otherwise leave a replaced,
# older runtime pair in place and record those unrelated files under this source tree.
& $CMakeCommand --build $BuildDir --config Release --clean-first --target juce_sharp juce_vst_host
if ($LASTEXITCODE -ne 0) { throw "Native build failed (exit $LASTEXITCODE)." }
if ((Get-NativeSourceTree $juce HEAD) -cne $before) { throw 'Native source identity changed during the build; rebuild from a stable checkout.' }
Write-NativeBuildProvenance -RepoRoot $RepoRoot -Path $ProvenancePath
