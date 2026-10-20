# Finds the red playhead time chip in a capture (rows holding a horizontal run
# of >= 40 chip-red pixels; the 2-3 px playhead line cannot match), then counts
# glyph core (lum>=200) vs fringe (110-199) pixels inside it.
param([Parameter(Mandatory)][string]$In)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($In)
$rect = New-Object System.Drawing.Rectangle 0,0,$bmp.Width,$bmp.Height
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $bmp.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($data); $stride = $data.Stride
function IsRed($i) { $b=$bytes[$i]; $g=$bytes[$i+1]; $r=$bytes[$i+2]; return ($r -gt 190 -and $g -lt 70 -and $b -gt 40 -and $b -lt 130) }
$minX=[int]::MaxValue;$minY=[int]::MaxValue;$maxX=-1;$maxY=-1
for ($y=0; $y -lt $bmp.Height; $y++) {
  $run=0; $runStart=0
  for ($x=0; $x -lt $bmp.Width; $x++) {
    $i = $y*$stride + $x*4
    if (IsRed $i) { if ($run -eq 0) { $runStart=$x }; $run++ }
    else { if ($run -ge 40) { if ($runStart -lt $minX){$minX=$runStart}; if ($x-1 -gt $maxX){$maxX=$x-1}; if ($y -lt $minY){$minY=$y}; if ($y -gt $maxY){$maxY=$y} }; $run=0 } } }
if ($maxX -lt 0) { "no chip found"; exit 1 }
$core=0;$fringe=0;$peak=0
for ($y=$minY; $y -le $maxY; $y++) { for ($x=$minX; $x -le $maxX; $x++) {
  $i = $y*$stride + $x*4; $b=$bytes[$i]; $g=$bytes[$i+1]; $r=$bytes[$i+2]
  $l = [int](0.3*$r + 0.59*$g + 0.11*$b); if ($l -gt $peak){$peak=$l}
  if ($l -ge 200) {$core++} elseif ($l -ge 110) {$fringe++} } }
$w = $maxX-$minX+1; $h = $maxY-$minY+1
"$([System.IO.Path]::GetFileName($In)) chip ${w}x${h}@$minX,$minY core=$core fringe=$fringe fringe/core=$([math]::Round($fringe/[math]::Max($core,1),2)) peak=$peak"
