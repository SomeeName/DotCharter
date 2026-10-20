<#
.SYNOPSIS
    Publishes DotCharter RigLab (the standalone rig) as a self-contained win-x64
    folder and zip, and refuses to produce one that carries the charting stack.

.DESCRIPTION
    RigLab exists so a player who only wants the rig does not install DotCharter's
    stem separation, note detection, psarc, export or AI stacks. The compiler-level
    guard is DotCharter.Tests/RigLab/RigLabDependencyTests; this script is the
    OUTPUT-level one: after `dotnet publish` it fails if any file in the folder
    belongs to that stack, and if the files RigLab needs to run are missing.

    Debug symbols (*.pdb) and XML doc files are removed from the payload.

.PARAMETER OutputRoot
    Where the folder and zip go. Default: artifacts/riglab under the repo root.

.PARAMETER NoZip
    Leave the folder, skip the zip.

.PARAMETER Tone3000ClientId
    Optional TONE3000 PUBLISHABLE key (t3k_pub_...) baked in as the default
    client id, so a fresh install can sign in to TONE3000 without setup. It is
    never committed; the script refuses anything that is not a publishable key.

.EXAMPLE
    powershell -NoProfile -File tools/release/Publish-RigLab.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputRoot,
    [switch]$NoZip,
    [string]$Tone3000ClientId
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$Project = Join-Path $RepoRoot 'DotCharter.RigLab\DotCharter.RigLab.csproj'
if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot 'artifacts\riglab' }

[xml]$csproj = Get-Content $Project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No <Version> in $Project" }

$folderName = "DotCharter RigLab $version"
$publishDir = Join-Path $OutputRoot $folderName
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing RigLab $version to $publishDir"
$extra = @()
if ($Tone3000ClientId) {
    # Publishable keys are made to ship in a client; a secret key never is.
    if ($Tone3000ClientId -notmatch '^t3k_pub_[A-Za-z0-9_-]+$') {
        throw 'Tone3000ClientId must be a TONE3000 PUBLISHABLE key (t3k_pub_...), never the secret one.'
    }
    $extra += "-p:Tone3000ClientId=$Tone3000ClientId"
    Write-Host 'Baking in the TONE3000 publishable client id.'
}
& dotnet publish $Project -c Release -r win-x64 --self-contained true -o $publishDir --nologo -v:q @extra
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (RigLab) failed.' }

# Dead weight: symbols and IntelliSense docs are for developers, not players.
Get-ChildItem $publishDir -Recurse -Include '*.pdb', '*.xml' -File | Remove-Item -Force

# What RigLab needs to run.
foreach ($file in 'DotCharter.RigLab.exe', 'DotCharter.RigLab.dll', 'DotCharter.Tone.dll',
                  'DotCharter.Tone.UI.dll', 'juce_sharp.dll', 'juce_vst_host.exe',
                  'hostfxr.dll', 'coreclr.dll', 'DotCharter.RigLab.runtimeconfig.json') {
    if (-not (Test-Path (Join-Path $publishDir $file))) {
        throw "Missing from the RigLab publish: $file"
    }
}
if (-not (Get-ChildItem (Join-Path $publishDir 'NamCaptures') -File -Recurse -ErrorAction SilentlyContinue)) {
    throw 'Missing from the RigLab publish: the bundled NAM captures (NamCaptures\).'
}

# What RigLab must never carry. Names, not paths: a charting-stack binary is a
# charting-stack binary wherever the publish put it.
$forbidden = @(
    '^DotCharter\.(exe|dll)$', '^DotCharter\.(Services|UI|Analysis|Rendering|Presentation)(\.|$)',
    '^onnxruntime', '^Microsoft\.ML\.OnnxRuntime', '^JuceSharp\.StemSeparation', '^AlphaTab', '^AlphaSkia',
    '^libAlphaSkia', '^Microsoft\.Data\.Sqlite', '^SQLitePCLRaw', '^e_sqlite3', '^LWI\.WwiseTools', '^Ww2Ogg',
    '^NVorbis', '^Dock\.', '^Silk\.NET', '^DirectML'
)
$offending = Get-ChildItem $publishDir -Recurse -File |
    Where-Object { $name = $_.Name; $forbidden | Where-Object { $name -match $_ } }
if ($offending) {
    $list = ($offending | ForEach-Object { $_.FullName.Substring($publishDir.Length + 1) }) -join "`n  "
    throw "The RigLab publish carries the charting stack:`n  $list"
}

$bytes = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("RigLab folder: {0:N1} MB, {1} files" -f ($bytes / 1MB), (Get-ChildItem $publishDir -Recurse -File).Count)

if (-not $NoZip) {
    $zip = Join-Path $OutputRoot "$folderName.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("RigLab zip:    {0:N1} MB  {1}" -f ((Get-Item $zip).Length / 1MB), $zip)
}
