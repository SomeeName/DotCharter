<#
.SYNOPSIS
    Crop a region out of a capture PNG and scale it, so a layout detail can be
    LOOKED AT rather than asserted about.

.DESCRIPTION
    The owner reviews this app from screenshots, and a 1600x900 frame read at
    1x hides exactly the faults these captures exist to find (a sliced cell, a
    tag painted into the next column, a baseline two pixels out). This crops a
    rectangle and scales it with nearest-neighbour-free high-quality
    interpolation so text stays legible.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Crop-Capture.ps1 `
        -In shot.png -Out top.png -X 0 -Y 0 -W 800 -H 220 -Scale 3
#>
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$W = 0,
    [int]$H = 0,
    [double]$Scale = 2
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $In))
try {
    if ($W -le 0) { $W = $src.Width - $X }
    if ($H -le 0) { $H = $src.Height - $Y }
    $W = [Math]::Min($W, $src.Width - $X)
    $H = [Math]::Min($H, $src.Height - $Y)

    $dw = [int]($W * $Scale)
    $dh = [int]($H * $Scale)
    $dst = New-Object System.Drawing.Bitmap($dw, $dh)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $dw, $dh)),
            (New-Object System.Drawing.Rectangle($X, $Y, $W, $H)),
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $g.Dispose() }

    $dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    Write-Host "$Out ($dw x $dh) from $In [$X,$Y $W x $H]"
}
finally { $src.Dispose() }
