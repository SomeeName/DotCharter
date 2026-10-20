# Launches a DotCharter build on the SECONDARY monitor via the perf autopilot,
# screenshots that monitor after -WaitS seconds, and waits for the app to exit.
# The ONE scripted launch on the VISIBLE desktop (ScriptedAppLaunchLintTests'
# allow-list): it can take the foreground. A hidden desktop
# (tools/release/HiddenDesktop.psm1) is never composited, and CopyFromScreen
# there fails with "invalid handle", so a screenshot tool cannot use it.
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    # The project the autopilot opens; defaults to $env:DOTCHARTER_CAPTURE_PROJECT.
    [string]$Project = $env:DOTCHARTER_CAPTURE_PROJECT,
    [int]$WaitS = 18,
    [int]$RunS = 26,
    [string]$Bounds = '-2460,60,1920,1080',
    [string]$DataRoot = ''
)
if (-not $Project) { throw 'Pass -Project <path to a .rscp> or set DOTCHARTER_CAPTURE_PROJECT.' }
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class DpiAw { [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v); }'
[void][DpiAw]::SetProcessDpiAwareness(2)

# Never against the user's real data (#439): every per-user path - config, logs,
# the PSARC/stem/waveform caches - lives under a fresh temp root for this launch.
# Pass -DataRoot to seed one (e.g. a prepared Roaming\DotCharter\appconfig.json).
if (-not $DataRoot) { $DataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dc-capture-" + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
$env:DOTCHARTER_DATA_ROOT = $DataRoot
$env:DOTCHARTER_UI_AUTOPILOT = 'perf'
$env:DOTCHARTER_AUTOPILOT_PROJECT = $Project
$env:DOTCHARTER_AUTOPILOT_BOUNDS = $Bounds
$env:DOTCHARTER_AUTOPILOT_RUN_S = "$RunS"
$p = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe)
Start-Sleep -Seconds $WaitS

Add-Type -AssemblyName System.Windows.Forms
$scr = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
$b = $scr.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"captured $Out ($($b.Width)x$($b.Height)) pid=$($p.Id) exited=$($p.HasExited)"
$p.WaitForExit(40000) | Out-Null
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; "killed" } else { "exited code=$($p.ExitCode)" }
