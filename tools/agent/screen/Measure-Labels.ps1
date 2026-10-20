# Bright-vs-mid-tone pixel counts over a strip of a capture. A crisp glyph row
# has many full-intensity pixels and few mid greys; a resampled one trades hi
# for mid. Usage: Measure-Labels.ps1 -In capture.png -X 700 -Y 500 -W 400 -H 24
param([Parameter(Mandatory)][string]$In, [int]$X, [int]$Y, [int]$W, [int]$H)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($In)
$hi=0;$mid=0;$lo=0
for ($yy=$Y; $yy -lt $Y+$H; $yy++) { for ($xx=$X; $xx -lt $X+$W; $xx++) {
  $c = $bmp.GetPixel($xx,$yy); $l = [int](0.3*$c.R + 0.59*$c.G + 0.11*$c.B)
  if ($l -gt 120) {$hi++} elseif ($l -gt 45) {$mid++} elseif ($l -gt 12) {$lo++} } }
$bmp.Dispose()
"$([System.IO.Path]::GetFileName($In)) strip ${W}x${H}@$X,$Y : hi(>120)=$hi mid(46-120)=$mid lo(13-45)=$lo mid/hi=$([math]::Round($mid/[math]::Max($hi,1),2))"
