# Crops a PNG region and upscales it with nearest-neighbour so pixels stay visible.
param([Parameter(Mandatory)][string]$In, [Parameter(Mandatory)][string]$Out,
      [int]$X, [int]$Y, [int]$W, [int]$H, [double]$Scale = 3.0)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($In)
$dst = New-Object System.Drawing.Bitmap ([int]($W*$Scale)), ([int]($H*$Scale))
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$dst.Width,$dst.Height), (New-Object System.Drawing.Rectangle $X,$Y,$W,$H), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose(); $dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $dst.Dispose(); $src.Dispose()
"wrote $Out"
