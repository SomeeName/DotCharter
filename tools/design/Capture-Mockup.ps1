<#
.SYNOPSIS
    Renders a `.dc.html` design mockup to a PNG with headless Edge.

.DESCRIPTION
    The mockups in `%USERPROFILE%\Downloads\fire\New Menu` are the pixel source
    of truth for the Fire redesign. Reading their markup is NOT a substitute for
    looking at them: the owner has repeatedly caught layout drift -- a control
    squashed to the wrong edge, a header row of figures that belongs top-right
    sitting top-left, day cells at half their designed height -- that reads
    perfectly fine in the HTML and is obvious the moment the two images are put
    side by side.

    So: render the mockup, capture the app at the SAME size, and compare. The
    app-side capture is `scratchpad/shot.ps1`, which drives the real window on
    the second monitor.

.EXAMPLE
    ./Capture-Mockup.ps1 -Name 'Practice Calendar'
    ./Capture-Mockup.ps1 -All -Width 1999 -Height 1087
#>
param(
    [string]  $Name,
    [switch]  $All,
    [string]  $MockupDir = (Join-Path $env:USERPROFILE 'Downloads\fire\New Menu'),
    [string]  $OutDir,
    [int]     $Width  = 1999,
    [int]     $Height = 1087
)

$ErrorActionPreference = 'Stop'

if (-not $OutDir) {
    $OutDir = Join-Path $env:TEMP 'dotcharter-mockups'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edge)) { $edge = (Get-Command msedge -ErrorAction SilentlyContinue).Source }
if (-not $edge) { throw 'msedge not found -- install Edge or point $edge at a Chromium binary' }

$targets = if ($All) {
    Get-ChildItem $MockupDir -Filter '*.dc.html' | Where-Object { $_.Name -notlike '*Kopie*' }
} elseif ($Name) {
    $hit = Get-ChildItem $MockupDir -Filter '*.dc.html' |
           Where-Object { $_.BaseName -replace '\.dc$','' -like "*$Name*" }
    if (-not $hit) { throw "no mockup matching '$Name' in $MockupDir" }
    $hit
} else {
    throw 'pass -Name <substring> or -All'
}

foreach ($f in $targets) {
    $slug = ($f.BaseName -replace '\.dc$','') -replace '[^A-Za-z0-9]+','-'
    $png  = Join-Path $OutDir "$slug.png"
    # file:// needs forward slashes and %20 for spaces.
    $url  = 'file:///' + ($f.FullName -replace '\\','/' -replace ' ','%20')

    & $edge --headless=new --disable-gpu --hide-scrollbars `
            --window-size="$Width,$Height" --screenshot="$png" $url 2>&1 | Out-Null

    # Edge returns before the file is flushed on a cold start.
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }

    if (Test-Path $png) {
        Write-Host ("SAVED {0,-34} {1,8:N0} bytes" -f $slug, (Get-Item $png).Length)
    } else {
        Write-Host ("FAILED {0}" -f $slug)
    }
}

Write-Host "out: $OutDir"
