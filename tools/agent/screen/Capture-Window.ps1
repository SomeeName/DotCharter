# Launches an exe with an argument, waits, screenshots its main window (by
# window rect, DPI-aware), saves the PNG, and closes the process.
param([Parameter(Mandatory)][string]$Exe, [string]$Arg, [Parameter(Mandatory)][string]$Out, [int]$WaitS = 20)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win { [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v); }
'@
[void][Win]::SetProcessDpiAwareness(2)
$p = if ($Arg) { Start-Process -FilePath $Exe -ArgumentList ('"' + $Arg + '"') -PassThru } else { Start-Process -FilePath $Exe -PassThru }
Start-Sleep -Seconds $WaitS
$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { $p2 = Get-Process | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1; if ($p2) { $h = $p2.MainWindowHandle; $p = $p2 } }
if ($h -eq [IntPtr]::Zero) { "no window (exited=$($p.HasExited))"; exit 1 }
[void][Win]::SetForegroundWindow($h); Start-Sleep -Milliseconds 800
$r = New-Object Win+RECT; [void][Win]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"captured $Out ${w}x${hh} at $($r.L),$($r.T) title='$($p.MainWindowTitle)'"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
