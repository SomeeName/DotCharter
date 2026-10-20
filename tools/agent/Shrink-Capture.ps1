<#
.SYNOPSIS
    Make a capture cheap to Read: an 800 px overview, or a crop scaled to a
    fixed pixel budget. Prints the output size and the image-token estimate.

.DESCRIPTION
    The model's image cost is PIXELS, not bytes: roughly (width x height) / 750
    tokens, and anything over 1568 px on the long edge is downscaled by the API
    before it is seen at all. So a raw 1920x1080 capture costs ~1.8k tokens and
    a 1586x1000 harness frame ~2.1k, whatever their PNG size.

    Two modes:
      Overview  (no -Crop): downscale so the long edge is -MaxEdge (default 800).
                ~0.5k tokens. For layout questions: what is where, what overlaps.
      Crop      (-Crop x,y,w,h): cut the region at 1x, then scale it (up or down)
                so its long edge is -MaxEdge (default 1000). Upscaling uses
                nearest-neighbour so 1 px borders and gaps stay crisp; the scale
                factor is printed. ~0.8-1.3k tokens. For glyph-level questions.

    A crop is never downscaled below 1x: if the region is already wider than
    -MaxEdge you are asked for a smaller region instead.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Shrink-Capture.ps1 -Path app-series-amp.png
    powershell -NoProfile -File tools/agent/Shrink-Capture.ps1 -Path app-series-amp.png -Crop 1180,40,330,220
    powershell -NoProfile -File tools/agent/Shrink-Capture.ps1 -Path app-series-amp.png -Crop 1180,40,330,220 -MaxEdge 660 -Out gear-row.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Out,
    # "x,y,w,h" in source pixels (one string; `powershell -File` cannot pass an int array)
    [string]$Crop,
    [int]$MaxEdge = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Path).Path)
try {
    $isCrop = $false
    if ($Crop) {
        $rect = @($Crop -split '[,; ]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
        if ($rect.Count -ne 4) { throw '-Crop needs exactly four integers: "x,y,w,h"' }
        $isCrop = $true
    }
    if ($MaxEdge -le 0) { if ($isCrop) { $MaxEdge = 1000 } else { $MaxEdge = 800 } }

    if ($isCrop) {
        $x = $rect[0]; $y = $rect[1]; $w = $rect[2]; $h = $rect[3]
        if ($x -lt 0 -or $y -lt 0 -or $w -le 0 -or $h -le 0 -or $x + $w -gt $src.Width -or $y + $h -gt $src.Height) {
            throw "crop $x,$y,$w,$h is outside the ${($src.Width)}x$($src.Height) source"
        }
        $srcRect = [System.Drawing.Rectangle]::new($x, $y, $w, $h)
        $scale = [math]::Min($MaxEdge / $w, $MaxEdge / $h)
        if ($scale -lt 1) {
            throw "region ${w}x${h} is already larger than -MaxEdge $MaxEdge; crop a smaller region (a downscaled crop loses the detail the crop exists to show)"
        }
        $mode = 'crop'
    }
    else {
        $srcRect = [System.Drawing.Rectangle]::new(0, 0, $src.Width, $src.Height)
        $scale = [math]::Min(1.0, [math]::Min($MaxEdge / $src.Width, $MaxEdge / $src.Height))
        $mode = 'overview'
    }

    $dw = [int][math]::Round($srcRect.Width * $scale)
    $dh = [int][math]::Round($srcRect.Height * $scale)
    $bmp = [System.Drawing.Bitmap]::new($dw, $dh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    if ($scale -ge 1) {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    }
    else {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    }
    $g.DrawImage($src, [System.Drawing.Rectangle]::new(0, 0, $dw, $dh), $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    if (-not $Out) {
        $stem = [IO.Path]::Combine([IO.Path]::GetDirectoryName((Resolve-Path $Path).Path), [IO.Path]::GetFileNameWithoutExtension($Path))
        if ($isCrop) { $Out = "$stem-crop-$x-$y-$w-$h.png" } else { $Out = "$stem-small.png" }
    }
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $tokens = [int][math]::Ceiling($dw * $dh / 750)
    $srcTokens = [int][math]::Ceiling([math]::Min($src.Width * $src.Height, 1568 * 1568) / 750)
    Write-Output ("{0}: {1}x{2} @ {3:0.00}x -> {4}  (~{5} image tokens; the raw {6}x{7} source would cost ~{8})" -f
        $mode, $dw, $dh, $scale, $Out, $tokens, $src.Width, $src.Height, $srcTokens)
}
finally {
    $src.Dispose()
}
