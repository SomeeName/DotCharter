<#
.SYNOPSIS
    Generates compile_commands.json for the native JUCE engine so clangd (the
    clangd-lsp Claude Code plugin, or any editor) sees the REAL compile flags.

.DESCRIPTION
    The real native build uses the "Visual Studio 18 2026" generator, and CMake
    writes no compilation database for Visual Studio generators. This script
    configures a SECOND, configure-only tree with Ninja + MSVC into
    artifacts/clangd/juce-native (git-ignored, outside the submodule) with
    CMAKE_EXPORT_COMPILE_COMMANDS=ON. Nothing is compiled beyond what CMake's
    own configure step builds (JUCE's juceaide), and the real build/ tree is
    never touched.

    The root .clangd points clangd at that directory, so re-run this script
    after adding a native source file or changing CMake options.

    Toolchain: the VS 2026 Build Tools' own cmake.exe and ninja.exe, with the
    MSVC environment taken from vcvars64.bat. Neither is on PATH on the owner's
    machine (see JuceSharp/AGENTS.md). FetchContent sources are reused from the
    existing build/_deps when present, so no network is needed.

.PARAMETER Clean
    Delete the clangd tree first (use after a CMake cache problem).
#>
param([switch]$Clean)
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$src = Join-Path $repo 'JuceSharp\src\JuceSharp.Native'
$out = Join-Path $repo 'artifacts\clangd\juce-native'
if (-not (Test-Path (Join-Path $src 'CMakeLists.txt'))) {
    throw "No CMakeLists.txt at $src - is the JuceSharp submodule checked out?"
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found - install Visual Studio Build Tools 2026." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio install with the C++ x64 tools found." }

$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmakeDir = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake'
$cmake = Join-Path $cmakeDir 'CMake\bin\cmake.exe'
$ninja = Join-Path $cmakeDir 'Ninja\ninja.exe'
foreach ($p in $vcvars, $cmake, $ninja) { if (-not (Test-Path $p)) { throw "Missing: $p" } }

if ($Clean -and (Test-Path $out)) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

$cacheArgs = @(
    '-DCMAKE_EXPORT_COMPILE_COMMANDS=ON',
    '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_MAKE_PROGRAM=$($ninja -replace '\\','/')"
)
# Reuse the real build's already-fetched dependencies and its option values.
$realCache = Join-Path $src 'build\CMakeCache.txt'
if (Test-Path $realCache) {
    foreach ($line in Get-Content $realCache) {
        if ($line -match '^(FETCHCONTENT_SOURCE_DIR_[A-Z0-9_]+|DOTCHARTER_[A-Z0-9_]+|JUCESHARP_[A-Z0-9_]+):[A-Z]+=(.+)$') {
            $cacheArgs += "-D$($Matches[1])=$($Matches[2])"
        }
    }
}

$quoted = ($cacheArgs | ForEach-Object { "`"$_`"" }) -join ' '
$cmd = "call `"$vcvars`" >nul && `"$cmake`" -S `"$src`" -B `"$out`" -G Ninja $quoted"
& cmd.exe /d /c $cmd
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }

$db = Join-Path $out 'compile_commands.json'
if (-not (Test-Path $db)) { throw "Configure succeeded but $db was not written." }
$entries = (Get-Content $db -Raw | ConvertFrom-Json).Count
Write-Host "compile_commands.json: $entries entries -> $db"
