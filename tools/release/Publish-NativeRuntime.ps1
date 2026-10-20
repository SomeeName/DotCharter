#Requires -Version 7
<#
.SYNOPSIS
    Packs a built juce_sharp.dll + juce_vst_host.exe pair as the prebuilt native
    runtime for this checkout's JuceSharp gitlink, pins it, and (with -Upload)
    attaches it to the JuceSharp release that contributors download from (#832).

.DESCRIPTION
    1. Reads the gitlink (git ls-tree HEAD JuceSharp) and DCA_ABI_VERSION from the header.
    2. Refuses unless -NativeDir holds both files and the DLL's dca_abi_version()
       equals the header's.
    3. Writes <OutDir>/juce-sharp-native-win-x64-<sha12>.zip (the two files at its root).
    4. Pins the pair in tools/setup/native-runtime.json: adds the entry, keeps an
       identical one, and REFUSES to repin a commit whose pinned files differ.
    5. Installs the zip into a temp folder through the same code the build uses
       (tools/setup/NativeRuntime.psm1), so a zip that would not install is never uploaded.
    6. -Upload only: creates the release if it does not exist and uploads the zip
       with `gh release upload` (never --clobber). Without -Upload it prints the
       commands instead and touches nothing remote.

    Commit the manifest change with the gitlink bump it belongs to.

.EXAMPLE
    pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -NativeDir JuceSharp/src/JuceSharp/runtimes/win-x64/native
    pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -NativeDir <dir> -Upload
#>
[CmdletBinding()]
param(
    # The folder holding the matched pair a native build deployed.
    [string]$NativeDir,
    [string]$OutDir,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $repoRoot 'tools/setup/NativeRuntime.psm1') -Force
if (-not $NativeDir) { $NativeDir = Join-Path $repoRoot 'JuceSharp/src/JuceSharp/runtimes/win-x64/native' }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'artifacts/native-runtime' }
$NativeDir = (Resolve-Path $NativeDir).Path
$manifestPath = Join-Path $repoRoot 'tools/setup/native-runtime.json'

$commit = Get-JuceSharpGitlink $repoRoot
$headerAbi = Get-HeaderAbiVersion (Join-Path $repoRoot 'JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h')
$asset = Get-NativeRuntimeAssetName $commit
Write-Host "JuceSharp $commit, DCA_ABI_VERSION $headerAbi -> $asset"

# --- the pair -------------------------------------------------------------
$hashes = [ordered]@{}
foreach ($file in Get-NativeRuntimePairFiles) {
    $path = Join-Path $NativeDir $file
    if (-not (Test-Path -LiteralPath $path)) { throw "$path is missing; the pair must come from ONE native build." }
    $hashes[$file] = Get-FileSha256 $path
}
$dllAbi = Read-DllAbiVersion (Join-Path $NativeDir 'juce_sharp.dll')
if ($dllAbi -ne $headerAbi) { throw "juce_sharp.dll reports ABI $dllAbi but the header says $headerAbi; it was not built from JuceSharp $commit." }

# --- the zip --------------------------------------------------------------
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$zip = Join-Path $OutDir $asset
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-NativeRuntimePairFiles) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $NativeDir $file), $file,
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
Write-Host "wrote $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

# --- the pin --------------------------------------------------------------
$manifest = Read-NativeRuntimeManifest $manifestPath
$existing = @($manifest.runtimes | Where-Object { $_.commit -eq $commit }) | Select-Object -First 1
# A PENDING entry (written by Export-PublicSnapshot.ps1, a 64-hex hash nowhere) pins
# nothing yet: it is replaced by the real pair, never compared with it.
if ($existing -and @(Get-NativeRuntimePairFiles | Where-Object { "$($existing.files.$_)" -cmatch '^[0-9a-f]{64}$' }).Count -eq 0) {
    $manifest.runtimes = @($manifest.runtimes | Where-Object { $_.commit -ne $commit })
    $existing = $null
}
if ($existing) {
    foreach ($file in $hashes.Keys) {
        if ($existing.files.$file -ne $hashes[$file]) {
            throw "native-runtime.json already pins $file of JuceSharp $commit as $($existing.files.$file), not $($hashes[$file]). A published pin is never replaced; build from the pinned commit or bump the gitlink."
        }
    }
    Write-Host "native-runtime.json already pins this pair."
}
else {
    $entry = [pscustomobject][ordered]@{ commit = $commit; abi = $headerAbi; files = [pscustomobject]$hashes }
    $manifest.runtimes = @($manifest.runtimes) + $entry
    ($manifest | ConvertTo-Json -Depth 5) + "`n" | Set-Content -LiteralPath $manifestPath -NoNewline -Encoding utf8NoBOM
    Write-Host "pinned the pair in tools/setup/native-runtime.json - commit that change with the gitlink."
}

# --- install it the way the build will --------------------------------------
$probe = Join-Path ([IO.Path]::GetTempPath()) ('dotcharter-native-probe-' + [guid]::NewGuid().ToString('N'))
$previous = $env:DOTCHARTER_NATIVE_ASSET_DIR
$env:DOTCHARTER_NATIVE_ASSET_DIR = $OutDir
try {
    [void](Install-NativeRuntime -Commit $commit -ManifestPath $manifestPath -HeaderAbi $headerAbi -Destination $probe)
    Write-Host 'the zip installs and verifies against the manifest.'
}
finally {
    $env:DOTCHARTER_NATIVE_ASSET_DIR = $previous
    Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
}

# --- upload -----------------------------------------------------------------
$repository = $manifest.repository
$release = $manifest.release
$createCommand = "gh release create $release -R $repository --title `"Prebuilt native runtimes`" --notes `"juce_sharp.dll + juce_vst_host.exe per JuceSharp commit, for DotCharter's tools/setup/Get-NativeRuntime.ps1.`" --prerelease"
$uploadCommand = "gh release upload $release `"$zip`" -R $repository"
if (-not $Upload) {
    Write-Host ''
    Write-Host 'Not uploaded (-Upload not given). To publish:'
    Write-Host "  $createCommand    # once, if the release does not exist"
    Write-Host "  $uploadCommand"
    exit 0
}

& gh release view $release -R $repository --json tagName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & gh release create $release -R $repository --title 'Prebuilt native runtimes' --prerelease `
        --notes "juce_sharp.dll + juce_vst_host.exe per JuceSharp commit, for DotCharter's tools/setup/Get-NativeRuntime.ps1."
    if ($LASTEXITCODE -ne 0) { throw "gh release create $release failed." }
}
$assets = (& gh release view $release -R $repository --json assets | ConvertFrom-Json).assets.name
if ($assets -contains $asset) {
    Write-Host "$asset is already on $repository@$release; not replaced (delete it there first if it is wrong)."
    exit 0
}
& gh release upload $release $zip -R $repository
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed." }
Write-Host "uploaded $asset to $repository@$release."
